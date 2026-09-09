using VideoDownloader.Core.Contracts;
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
        foreach (var alternate in previous.Alternatives.Take(4))
        {
            if (validate is null || previous.ContentIdentity is null || alternate.ContentIdentity != previous.ContentIdentity || !Compatible(previous, alternate)) continue;
            try
            {
                await validate(alternate, timeout.Token);
                return alternate with { RecoveryPageUrl = previous.RecoveryPageUrl, Alternatives = [] };
            }
            catch (DownloadException ex) { errors.Add("alternate:" + ex.ErrorCode); }
            catch (HttpRequestException) { errors.Add("alternate:network"); }
        }
        page = previous.RecoveryPageUrl ?? page;
        // Feed roots (?recommend=1) are not stable; rebuild a detail URL from content identity.
        if (!HasStableContentAddress(page))
            page = RecoveryAddress(page, previous.ContentIdentity) ?? page;
        if (!HasStableContentAddress(page))
            throw new DownloadException(ErrorCodes.ContextExpired, "The feed has no stable video address; rediscover the intended video.");
        foreach (var resolver in resolvers.Where(r => r.IsAvailable).Take(3))
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
                    (validate is not null || !v.Tracks.Select(t => t.SourceUrl).SequenceEqual(previous.Tracks.Select(t => t.SourceUrl)))).Take(4);
            foreach (var match in matches)
                try
                {
                    if (validate is not null) await validate(match, timeout.Token);
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

    internal static bool Compatible(MediaVariant a, MediaVariant b) => a.Height == b.Height &&
        a.Tracks.Select(t => t.Kind).Order().SequenceEqual(b.Tracks.Select(t => t.Kind).Order());

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
}
