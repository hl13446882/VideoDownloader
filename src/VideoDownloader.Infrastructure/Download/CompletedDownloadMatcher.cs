using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Detection;
using VideoDownloader.Infrastructure.Detection.Sites.Bilibili;

namespace VideoDownloader.Infrastructure.Download;

/// <summary>
/// Detects when an incoming download targets the same durable object as an already-completed job
/// (stable URL key / ContentIdentity / site object keys + compatible size). Not size-only.
/// </summary>
internal static class CompletedDownloadMatcher
{
    public static bool MatchesCompletedObject(
        MediaVariant stored,
        MediaVariant incoming,
        long? storedExpectedTotal,
        long? onDiskBytes)
    {
        var incomingTotal = ResolveTotal(incoming);
        if (incomingTotal is not > 0)
            return false;

        var priorTotal = storedExpectedTotal
            ?? ResolveTotal(stored)
            ?? (onDiskBytes is > 0 ? onDiskBytes : null);
        if (!ResumeObjectTrust.TotalsCompatible(
                priorTotal,
                incomingTotal,
                ResumeObjectTrust.DefaultSizeToleranceBytes))
            return false;

        if (onDiskBytes is > 0 &&
            !ResumeObjectTrust.TotalsCompatible(
                onDiskBytes,
                incomingTotal,
                ResumeObjectTrust.DefaultSizeToleranceBytes))
            return false;

        return HasStableObjectIdentity(stored, incoming);
    }

    internal static bool HasStableObjectIdentity(MediaVariant stored, MediaVariant incoming)
    {
        if (BilibiliCdnPreference.SameDashObjects(stored, incoming))
            return true;

        if (MediaAddressRenewal.SameYoutubePlayback(stored, incoming))
            return true;

        if (stored.ContentIdentity is { Length: > 0 } left &&
            incoming.ContentIdentity is { Length: > 0 } right &&
            string.Equals(left, right, StringComparison.Ordinal))
            return true;

        return SameStableTrackUrls(stored, incoming);
    }

    private static bool SameStableTrackUrls(MediaVariant a, MediaVariant b)
    {
        if (a.Tracks.Count == 0 || a.Tracks.Count != b.Tracks.Count)
            return false;

        var left = a.Tracks.OrderBy(t => t.Kind).ToArray();
        var right = b.Tracks.OrderBy(t => t.Kind).ToArray();
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i].Kind != right[i].Kind)
                return false;

            // googlevideo path alone is not a durable quality key — require SameYoutubePlayback.
            if (MediaAddressRenewal.IsYouTubePlayback(left[i].SourceUrl) ||
                MediaAddressRenewal.IsYouTubePlayback(right[i].SourceUrl))
                return false;

            if (MediaUrlNormalizer.IsSameMedia(left[i].SourceUrl, right[i].SourceUrl))
                continue;

            if (!SameHostAndPath(left[i].SourceUrl, right[i].SourceUrl))
                return false;
        }

        return true;
    }

    private static bool SameHostAndPath(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.AbsolutePath.TrimEnd('/'), b.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static long? ResolveTotal(MediaVariant variant) =>
        variant.TotalContentLength is > 0
            ? variant.TotalContentLength
            : variant.Tracks.FirstOrDefault(t => t.ContentLength is > 0)?.ContentLength;
}
