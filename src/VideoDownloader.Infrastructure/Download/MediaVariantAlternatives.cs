using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Detection.Sites.Bilibili;

namespace VideoDownloader.Infrastructure.Download;

/// <summary>
/// Persists sibling quality/CDN variants on a selected download so 403 recovery can
/// switch without rediscovery. Cap keeps SQLite meta bounded.
/// </summary>
internal static class MediaVariantAlternatives
{
    public const int MaxStored = 24;

    public static MediaVariant WithLadder(MediaVariant primary, IEnumerable<MediaVariant>? ladder)
    {
        var siblings = (ladder ?? [])
            .Concat(primary.Alternatives)
            .Where(v => v.Tracks.Count > 0)
            .Where(v => !SameSource(primary, v))
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

    private static bool SameSource(MediaVariant a, MediaVariant b) =>
        string.Equals(a.SourceUrl.AbsoluteUri, b.SourceUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase);

    private static string DedupKey(MediaVariant v)
    {
        if (BilibiliCdnPreference.IsMediaHost(v.SourceUrl))
        {
            // Keep both a durable and a fragile CDN for the same m4s so 403/RST recovery
            // can leave Akamai instead of collapsing to a single host.
            var tier = BilibiliCdnPreference.IsFragile(v.SourceUrl) ? "fragile" : "durable";
            return "obj:" + BilibiliCdnPreference.ObjectKey(v.SourceUrl) + "|" + tier;
        }

        return "url:" + string.Join('|', v.Tracks.Select(t => t.SourceUrl.AbsoluteUri));
    }
}
