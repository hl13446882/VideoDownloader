using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Errors;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Download;

internal static class MediaAddressRenewal
{
    internal static async Task<MediaVariant> ResolveAsync(Uri page, MediaVariant previous, RequestContext context,
        IEnumerable<IExternalSiteResolver> resolvers, CancellationToken ct)
    {
        // A mutable feed page cannot identify a historical download safely.
        if (!HasStableContentAddress(page))
            throw new DownloadException(ErrorCodes.ContextExpired, "The feed has no stable video address; rediscover the intended video.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        foreach (var resolver in resolvers.Where(r => r.IsAvailable))
        {
            var videos = await resolver.ResolveAsync(page, context, timeout.Token);
            var match = videos.Where(v => !v.IsDrmProtected && v.PageUrl == page)
                .SelectMany(v => v.Variants)
                .FirstOrDefault(v => v.Height == previous.Height &&
                    v.Tracks.Select(t => t.Kind).Order().SequenceEqual(previous.Tracks.Select(t => t.Kind).Order()) &&
                    !v.Tracks.Select(t => t.SourceUrl).SequenceEqual(previous.Tracks.Select(t => t.SourceUrl)));
            if (match is not null) return match;
        }
        throw new DownloadException(ErrorCodes.ContextExpired, "No renewed media address was returned for this video and quality.");
    }

    internal static bool HasStableContentAddress(Uri page)
    {
        var keys = page.Query.TrimStart('?').Split('&').Select(p => p.Split('=')[0]);
        return keys.Any(k => k is "v" or "id" or "video_id" or "modal_id" or "aweme_id" or "item_id" or "bvid" or "aid") ||
            page.AbsolutePath.Contains("/video/",StringComparison.OrdinalIgnoreCase) ||
            page.AbsolutePath.Contains("/shorts/",StringComparison.OrdinalIgnoreCase) ||
            Path.HasExtension(page.AbsolutePath);
    }
}
