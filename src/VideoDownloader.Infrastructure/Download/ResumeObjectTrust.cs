using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Detection.Sites.Bilibili;

namespace VideoDownloader.Infrastructure.Download;

/// <summary>
/// Decides whether a renewed media URL still refers to the same downloadable object
/// so partial <c>.part</c> / track scratch can be kept.
/// </summary>
internal static class ResumeObjectTrust
{
    public const long DefaultSizeToleranceBytes = 64 * 1024;

    public static bool ShouldKeepPartialProgress(
        MediaVariant previous,
        MediaVariant renewed,
        long? expectedTotalBytes,
        string? storedETag = null,
        string? renewedETag = null,
        string? storedPrefixHash = null,
        string? renewedPrefixHash = null,
        long sizeToleranceBytes = DefaultSizeToleranceBytes)
    {
        if (PrefixHashesConflict(storedPrefixHash, renewedPrefixHash))
            return false;

        if (PrefixHashesMatch(storedPrefixHash, renewedPrefixHash))
            return true;

        if (ETagsMatch(storedETag, renewedETag))
            return true;

        var priorTotal = expectedTotalBytes
            ?? previous.TotalContentLength
            ?? previous.Tracks.FirstOrDefault(t => t.ContentLength is > 0)?.ContentLength;
        var newTotal = renewed.TotalContentLength
            ?? renewed.Tracks.FirstOrDefault(t => t.ContentLength is > 0)?.ContentLength;

        // Strong site-specific object identity: keep unless both totals are known and disagree.
        if (BilibiliCdnPreference.SameDashObjects(previous, renewed) ||
            MediaAddressRenewal.SameYoutubePlayback(previous, renewed))
        {
            if (priorTotal is > 0 && newTotal is > 0)
                return TotalsCompatible(priorTotal, newTotal, sizeToleranceBytes);
            return true;
        }

        // Generic path: content identity AND compatible size (no size-only Keep).
        if (!MediaAddressRenewal.SameContent(previous, renewed))
            return false;

        return TotalsCompatible(priorTotal, newTotal, sizeToleranceBytes);
    }

    internal static bool TotalsCompatible(long? priorTotal, long? newTotal, long toleranceBytes)
    {
        if (priorTotal is not > 0 || newTotal is not > 0)
            return false;
        return Math.Abs(priorTotal.Value - newTotal.Value) <= toleranceBytes;
    }

    private static bool ETagsMatch(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(NormalizeETag(left), NormalizeETag(right), StringComparison.Ordinal);

    private static string NormalizeETag(string value) =>
        value.Trim().Trim('"');

    private static bool PrefixHashesMatch(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool PrefixHashesConflict(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        !string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
