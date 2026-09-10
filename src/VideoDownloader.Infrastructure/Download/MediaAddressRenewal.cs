using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Errors;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Detection;

namespace VideoDownloader.Infrastructure.Download;

internal static class MediaAddressRenewal
{
    internal static async Task<MediaVariant> ResolveAsync(Uri page, MediaVariant previous, RequestContext context,
        IEnumerable<IExternalSiteResolver> resolvers, CancellationToken ct,
        Func<MediaVariant, CancellationToken, Task>? validate = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
        var errors = new List<string>();
        var alternate = await TryAlternativesAsync(previous, validate, timeout.Token, errors);
        if (alternate is not null)
            return alternate;
        page = previous.RecoveryPageUrl ?? page;
        // Feed roots (?recommend=1) are not stable; rebuild a detail URL from content identity.
        if (!HasStableContentAddress(page))
            page = RecoveryAddress(page, previous.ContentIdentity) ?? page;
        if (!HasStableContentAddress(page))
            throw new DownloadException(ErrorCodes.ContextExpired, "The feed has no stable video address; rediscover the intended video.");
        var siteId = InferSiteId(page);
        foreach (var resolver in resolvers.Where(r => r.IsAvailable && (siteId is null || r.SupportsSite(siteId))).Take(3))
        {
            IReadOnlyList<DetectedVideo> videos;
            try { videos = await resolver.ResolveAsync(page, context, timeout.Token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add("resolve:" + ex.GetType().Name); continue; }
            var matches = videos.Where(v => !v.IsDrmProtected &&
                    (previous.ContentIdentity is not null && v.SiteContentId is not null
                        ? previous.ContentIdentity == "id:" + v.SiteContentId : v.PageUrl == page))
                .SelectMany(v => v.Variants)
                .Where(v => Compatible(previous, v) &&
                    !IsKnownUndersizedVideo(v) &&
                    (validate is not null || !v.Tracks.Select(t => t.SourceUrl).SequenceEqual(previous.Tracks.Select(t => t.SourceUrl))))
                .OrderByDescending(v => v.TotalContentLength ?? v.Bandwidth ?? 0)
                .ThenByDescending(DurableHostScore)
                .Take(8);
            foreach (var match in matches)
                try
                {
                    if (validate is not null) await validate(match, timeout.Token);
                    if (IsKnownUndersizedVideo(match))
                    {
                        errors.Add("renewed:undersized");
                        continue;
                    }

                    return match with { ContentIdentity = previous.ContentIdentity, RecoveryPageUrl = page, Alternatives = [] };
                }
                catch (DownloadException ex) { errors.Add("renewed:" + ex.ErrorCode); }
                catch (HttpRequestException) { errors.Add("renewed:network"); }
        }
        throw new DownloadException(ErrorCodes.ContextExpired, "No usable address for the original video/quality. " + string.Join(",", errors));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new DownloadException(ErrorCodes.NetTimeout, "HTTP_403 recovery timed out."); }
    }

    /// <summary>
    /// Try sibling format URLs only (no yt-dlp). Used so 403 recovery can switch CDN before gateway renew.
    /// </summary>
    internal static async Task<MediaVariant?> TryAlternativesAsync(
        MediaVariant previous,
        Func<MediaVariant, CancellationToken, Task>? validate,
        CancellationToken ct,
        List<string>? errors = null)
    {
        errors ??= [];
        foreach (var alternate in previous.Alternatives
                     .OrderByDescending(DurableHostScore)
                     .ThenByDescending(a => SameHost(previous, a) ? 0 : 1)
                     .ThenByDescending(a => a.TotalContentLength ?? a.Bandwidth ?? 0)
                     .Take(6))
        {
            if (!SameContent(previous, alternate) || !Compatible(previous, alternate)) continue;
            if (IsKnownUndersizedVideo(alternate)) continue;
            // Same fragile signed host/family rarely unlocks after 403; never retry web-prime↔web-prime.
            if (IsFragileSignedHost(previous.SourceUrl) && IsFragileSignedHost(alternate.SourceUrl))
                continue;
            if (string.Equals(alternate.SourceUrl.AbsoluteUri, previous.SourceUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                if (validate is not null) await validate(alternate, ct);
                return alternate with
                {
                    ContentIdentity = previous.ContentIdentity ?? alternate.ContentIdentity,
                    RecoveryPageUrl = previous.RecoveryPageUrl ?? alternate.RecoveryPageUrl,
                    Alternatives = []
                };
            }
            catch (DownloadException ex) { errors.Add("alternate:" + ex.ErrorCode); }
            catch (HttpRequestException) { errors.Add("alternate:network"); }
        }

        return null;
    }

    /// <summary>Reject watermark / preview shells whose declared size is below a credible VOD floor.</summary>
    internal static bool IsKnownUndersizedVideo(MediaVariant variant)
    {
        if (!variant.Tracks.Any(t => t.Kind is MediaTrackKind.Combined or MediaTrackKind.Video))
            return false;

        if (variant.TotalContentLength is > 0 and < MediaResourceSizeFilter.MinProgressiveVideoBytes)
            return true;

        return variant.Tracks.Any(t =>
            t.Kind is MediaTrackKind.Combined or MediaTrackKind.Video &&
            t.ContentLength is > 0 and < MediaResourceSizeFilter.MinProgressiveVideoBytes);
    }

    internal static bool Compatible(MediaVariant a, MediaVariant b) =>
        // Same ladder rung when both declare height; otherwise allow Combined↔Combined swaps so
        // Douyin format siblings (often height=null) remain usable after a rejected CDN.
        (a.Height is null || b.Height is null || a.Height == b.Height) &&
        a.Tracks.Select(t => t.Kind).Order().SequenceEqual(b.Tracks.Select(t => t.Kind).Order());

    internal static bool SameContent(MediaVariant a, MediaVariant b)
    {
        if (a.ContentIdentity is { Length: > 0 } left &&
            b.ContentIdentity is { Length: > 0 } right)
            return string.Equals(left, right, StringComparison.Ordinal);
        // Missing identity on one side: still accept when both are Combined and share recovery page.
        return a.RecoveryPageUrl is not null &&
               b.RecoveryPageUrl is not null &&
               string.Equals(a.RecoveryPageUrl.AbsoluteUri, b.RecoveryPageUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    internal static int DurableHostScore(MediaVariant variant)
    {
        var host = variant.SourceUrl.Host;
        if (host.Contains("zjcdn", StringComparison.OrdinalIgnoreCase)) return 300;
        if (host.Contains("bytecdn", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("byteicdn", StringComparison.OrdinalIgnoreCase)) return 200;
        if (host.Contains("web-prime", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("webapp-prime", StringComparison.OrdinalIgnoreCase)) return -400;
        if (host.Contains("douyinvod", StringComparison.OrdinalIgnoreCase)) return 50;
        return 0;
    }

    private static bool SameHost(MediaVariant a, MediaVariant b) =>
        string.Equals(a.SourceUrl.Host, b.SourceUrl.Host, StringComparison.OrdinalIgnoreCase);

    internal static bool IsFragileSignedHost(Uri url) =>
        url.Host.Contains("web-prime", StringComparison.OrdinalIgnoreCase) ||
        url.Host.Contains("webapp-prime", StringComparison.OrdinalIgnoreCase);

    internal static Uri? RecoveryAddress(Uri page, string? identity)
    {
        if (HasStableContentAddress(page)) return page;
        if (identity?.StartsWith("id:", StringComparison.Ordinal) != true) return null;
        var id = Uri.EscapeDataString(identity[3..]);
        if (page.Host == "tiktok.com" || page.Host.EndsWith(".tiktok.com", StringComparison.OrdinalIgnoreCase))
            return new Uri($"https://www.tiktok.com/@i/video/{id}");
        if (page.Host == "douyin.com" || page.Host.EndsWith(".douyin.com", StringComparison.OrdinalIgnoreCase))
            return new Uri($"https://www.douyin.com/video/{id}");
        return null;
    }

    internal static bool HasStableContentAddress(Uri page)
    {
        var keys = page.Query.TrimStart('?').Split('&').Select(p => p.Split('=')[0]);
        return keys.Any(k => k is "v" or "id" or "video_id" or "modal_id" or "aweme_id" or "item_id" or "bvid" or "aid") ||
            page.AbsolutePath.Contains("/video/",StringComparison.OrdinalIgnoreCase) ||
            page.AbsolutePath.Contains("/shorts/",StringComparison.OrdinalIgnoreCase) ||
            System.Text.RegularExpressions.Regex.IsMatch(page.AbsolutePath, @"/archives/\d+/?$") ||
            Path.HasExtension(page.AbsolutePath);
    }

    /// <summary>Map page host to the isolated yt-dlp extractor site id (null → try Generic only via SupportsSite).</summary>
    private static string? InferSiteId(Uri page)
    {
        var host = page.Host;
        if (host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase))
            return SiteIds.YouTube;
        if (host.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase))
            return SiteIds.TikTok;
        if (host.Contains("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("bilibili.tv", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("b23.tv", StringComparison.OrdinalIgnoreCase))
            return SiteIds.Bilibili;
        if (host.Contains("douyin.com", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("iesdouyin.com", StringComparison.OrdinalIgnoreCase))
            return SiteIds.Douyin;
        return SiteIds.Generic;
    }
}
