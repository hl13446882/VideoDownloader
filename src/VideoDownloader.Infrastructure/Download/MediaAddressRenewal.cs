using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Errors;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Detection;
using VideoDownloader.Infrastructure.Detection.Sites.Bilibili;

namespace VideoDownloader.Infrastructure.Download;

internal static class MediaAddressRenewal
{
    internal static async Task<MediaVariant> ResolveAsync(Uri page, MediaVariant previous, RequestContext context,
        IEnumerable<IExternalSiteResolver> resolvers, CancellationToken ct,
        Func<MediaVariant, CancellationToken, Task>? validate = null,
        long? expectedTotalBytes = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
        var sizeAnchor = expectedTotalBytes ?? previous.TotalContentLength;
        var errors = new List<string>();
        var alternate = await TryAlternativesAsync(previous, validate, timeout.Token, errors, sizeAnchor);
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
            var matches = videos.Where(v => !v.IsDrmProtected && SameDetectedContent(previous, v, page))
                .SelectMany(v => v.Variants)
                .Select(v => BilibiliCdnPreference.AlignDashRenewal(previous, v)
                             ?? BilibiliCdnPreference.AlignDashRenewalBySize(previous, v, sizeAnchor)
                             ?? v)
                .Where(v => (Compatible(previous, v) ||
                             BilibiliCdnPreference.SameDashObjects(previous, v) ||
                             BilibiliCdnPreference.SameDashSize(previous, v, sizeAnchor) ||
                             SameYoutubePlayback(previous, v)) &&
                    !IsKnownUndersizedVideo(v) &&
                    (validate is not null || !v.Tracks.Select(t => t.SourceUrl).SequenceEqual(previous.Tracks.Select(t => t.SourceUrl))))
                .OrderByDescending(v => BilibiliCdnPreference.SameDashObjects(previous, v) || SameYoutubePlayback(previous, v) ? 2
                    : BilibiliCdnPreference.SameDashSize(previous, v, sizeAnchor) ? 1 : 0)
                .ThenByDescending(v => BilibiliCdnPreference.IsMediaHost(v.SourceUrl) ? BilibiliCdnPreference.Score(v.SourceUrl) : 0)
                .ThenByDescending(v => v.TotalContentLength ?? v.Bandwidth ?? 0)
                .ThenByDescending(DurableHostScore)
                .Take(8)
                .ToList();
            if (matches.Count == 0 && videos.Count > 0)
                errors.Add("renewed:no_quality_match");
            foreach (var match in matches)
                try
                {
                    if (validate is not null) await validate(match, timeout.Token);
                    if (IsKnownUndersizedVideo(match))
                    {
                        errors.Add("renewed:undersized");
                        continue;
                    }

                    var aligned = BilibiliCdnPreference.AlignDashRenewal(previous, match)
                        ?? BilibiliCdnPreference.AlignDashRenewalBySize(previous, match, sizeAnchor)
                        ?? match;
                    var withIdentity = aligned with
                    {
                        ContentIdentity = previous.ContentIdentity,
                        RecoveryPageUrl = page
                    };
                    return MediaVariantAlternatives.WithLadder(
                        withIdentity,
                        videos.SelectMany(v => v.Variants).Append(previous));
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
    /// Prefer exact DASH object / size-aligned siblings so a partial .part can continue.
    /// </summary>
    internal static async Task<MediaVariant?> TryAlternativesAsync(
        MediaVariant previous,
        Func<MediaVariant, CancellationToken, Task>? validate,
        CancellationToken ct,
        List<string>? errors = null,
        long? expectedTotalBytes = null)
    {
        errors ??= [];
        var sizeAnchor = expectedTotalBytes ?? previous.TotalContentLength;
        var ordered = previous.Alternatives
            .Select(a => (
                Raw: a,
                Aligned: BilibiliCdnPreference.AlignDashRenewal(previous, a)
                         ?? BilibiliCdnPreference.AlignDashRenewalBySize(previous, a, sizeAnchor)
                         ?? (Compatible(previous, a) && SameContent(previous, a) ? a : null)))
            .Where(x => x.Aligned is not null)
            .OrderByDescending(x => BilibiliCdnPreference.SameDashObjects(previous, x.Raw) ? 2
                : BilibiliCdnPreference.SameDashSize(previous, x.Raw, sizeAnchor) ? 1 : 0)
            .ThenByDescending(x => DurableHostScore(x.Aligned!))
            .ThenByDescending(x => SameHost(previous, x.Aligned!) ? 0 : 1)
            .ThenByDescending(x => x.Aligned!.TotalContentLength ?? x.Aligned.Bandwidth ?? 0)
            .Take(MediaVariantAlternatives.MaxStored)
            .ToList();

        foreach (var (_, aligned) in ordered)
        {
            var candidate = aligned!;
            if (IsKnownUndersizedVideo(candidate)) continue;
            if (IsFragileSignedHost(previous.SourceUrl) && IsFragileSignedHost(candidate.SourceUrl))
                continue;
            if (string.Equals(candidate.SourceUrl.AbsoluteUri, previous.SourceUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                if (validate is not null) await validate(candidate, ct);
                return candidate with
                {
                    ContentIdentity = previous.ContentIdentity ?? candidate.ContentIdentity,
                    RecoveryPageUrl = previous.RecoveryPageUrl ?? candidate.RecoveryPageUrl,
                    Alternatives = previous.Alternatives
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

    /// <summary>
    /// yt-dlp Bilibili id is often the numeric aid while the job stored BV; YouTube ids also
    /// appear in the watch URL. Exact <c>id:</c> equality is too strict for 403 URL renew.
    /// </summary>
    internal static bool SameDetectedContent(MediaVariant previous, DetectedVideo video, Uri page)
    {
        var identity = previous.ContentIdentity;
        if (identity?.StartsWith("id:", StringComparison.Ordinal) == true)
            identity = identity[3..];

        if (!string.IsNullOrWhiteSpace(identity) &&
            !string.IsNullOrWhiteSpace(video.SiteContentId) &&
            string.Equals(identity, video.SiteContentId, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(identity) &&
            (ContainsToken(page, identity) || ContainsToken(video.PageUrl, identity)))
            return true;

        if (!string.IsNullOrWhiteSpace(video.SiteContentId) &&
            (ContainsToken(page, video.SiteContentId) || ContainsToken(previous.RecoveryPageUrl, video.SiteContentId)))
            return true;

        return previous.RecoveryPageUrl is not null &&
               string.Equals(video.PageUrl.AbsolutePath, previous.RecoveryPageUrl.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when both variants are googlevideo tracks for the same YouTube object/quality.
    /// Do not compare the <c>id=</c> query: that is a per-request playback session token and
    /// changes on every URL renew — matching it would discard .part and restart from zero.
    /// </summary>
    internal static bool SameYoutubePlayback(MediaVariant previous, MediaVariant next)
    {
        if (!previous.Tracks.All(t => IsYouTubePlayback(t.SourceUrl)) ||
            !next.Tracks.All(t => IsYouTubePlayback(t.SourceUrl)))
            return false;

        var left = previous.Tracks
            .Select(t => (
                t.Kind,
                Itag: QueryValue(t.SourceUrl, "itag"),
                Clen: QueryValue(t.SourceUrl, "clen") ?? t.ContentLength?.ToString()))
            .OrderBy(t => t.Kind)
            .ToArray();
        var right = next.Tracks
            .Select(t => (
                t.Kind,
                Itag: QueryValue(t.SourceUrl, "itag"),
                Clen: QueryValue(t.SourceUrl, "clen") ?? t.ContentLength?.ToString()))
            .OrderBy(t => t.Kind)
            .ToArray();
        if (left.Length == 0 || left.Length != right.Length)
            return false;
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i].Kind != right[i].Kind ||
                string.IsNullOrWhiteSpace(left[i].Itag) ||
                !string.Equals(left[i].Itag, right[i].Itag, StringComparison.Ordinal))
                return false;
            // When both sides publish clen, require the same object size (ignore tiny CDN drift).
            if (!string.IsNullOrWhiteSpace(left[i].Clen) &&
                !string.IsNullOrWhiteSpace(right[i].Clen) &&
                long.TryParse(left[i].Clen, out var leftLen) &&
                long.TryParse(right[i].Clen, out var rightLen) &&
                Math.Abs(leftLen - rightLen) > 64_000)
                return false;
        }

        return true;
    }

    internal static bool IsYouTubePlayback(Uri url) =>
        url.Host.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsToken(Uri? url, string token) =>
        url is not null && url.OriginalString.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static string? QueryValue(Uri url, string key)
    {
        foreach (var part in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            if (!part.AsSpan(0, eq).Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;
            return Uri.UnescapeDataString(part[(eq + 1)..]);
        }

        return null;
    }

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
        if (host.Contains("tiktokcdn", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("byteoversea", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("muscdn", StringComparison.OrdinalIgnoreCase))
            return 250;
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
