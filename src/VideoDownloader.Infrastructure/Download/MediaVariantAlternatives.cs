using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Detection.Sites.Bilibili;

namespace VideoDownloader.Infrastructure.Download;

/// <summary>
/// Persists sibling quality/CDN variants on a selected download so 403 recovery can
/// switch without rediscovery. Cap keeps SQLite meta bounded.
/// </summary>
public static class MediaVariantAlternatives
{
    public const int MaxStored = 24;

    public static MediaVariant WithLadder(MediaVariant primary, IEnumerable<MediaVariant>? ladder)
    {
        var siblings = (ladder ?? [])
            .Concat(primary.Alternatives)
            .Where(v => v.Tracks.Count > 0)
            .Where(v => !SameVariant(primary, v))
            .Select(v => v with { Alternatives = [] })
            .GroupBy(DedupKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(v => BilibiliCdnPreference.IsMediaHost(v.SourceUrl)
                    ? BilibiliCdnPreference.Score(v.SourceUrl)
                    : MediaAddressRenewal.DurableHostScore(v))
                .ThenByDescending(v => v.TotalContentLength ?? v.Bandwidth ?? 0)
                .First())
            .OrderByDescending(v => v.Height ?? 0)
            .ThenByDescending(v => v.TotalContentLength ?? v.Bandwidth ?? 0)
            .Take(MaxStored)
            .ToArray();

        return primary with { Alternatives = siblings };
    }

    private static bool SameVariant(MediaVariant a, MediaVariant b) =>
        string.Equals(MediaVariantIdentity.DedupKey(a), MediaVariantIdentity.DedupKey(b),
            StringComparison.OrdinalIgnoreCase);

    private static string DedupKey(MediaVariant v)
    {
        if (MediaVariantIdentity.IsDashVariant(v))
            return MediaVariantIdentity.DedupKey(v);

        if (BilibiliCdnPreference.IsMediaHost(v.SourceUrl))
        {
            // Keep both a durable and a fragile CDN for the same m4s so 403/RST recovery
            // can leave Akamai instead of collapsing to a single host.
            var tier = BilibiliCdnPreference.IsFragile(v.SourceUrl) ? "fragile" : "durable";
            return "obj:" + BilibiliCdnPreference.ObjectKey(v.SourceUrl) + "|" + tier;
        }

        return MediaVariantIdentity.DedupKey(v);
    }
}
