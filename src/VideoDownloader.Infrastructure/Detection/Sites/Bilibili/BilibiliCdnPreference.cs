using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Infrastructure.Detection.Sites.Bilibili;

/// <summary>
/// Bilibili-only CDN ranking. Domestic upos/bilivideo hosts are preferred over Akamai
/// (<c>os=akam</c> / <c>akamaized.net</c>) and overseas COS/HW (<c>mirrorcosov</c> /
/// <c>os=cosovbv</c>), which frequently reset large m4s transfers.
/// Not used by other exclusive detectors.
/// </summary>
internal static class BilibiliCdnPreference
{
    public static bool IsFragile(Uri url)
    {
        var host = url.Host;
        if (host.Contains("akamai", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("mirrorakam", StringComparison.OrdinalIgnoreCase) ||
            IsOverseasMirror(url))
            return true;
        return HasQueryToken(url, "os=akam");
    }

    public static int Score(Uri url)
    {
        if (IsFragile(url))
            return -100;

        var host = url.Host;
        if (host.Contains("bilivideo.com", StringComparison.OrdinalIgnoreCase))
        {
            if (host.Contains("mirrorbos", StringComparison.OrdinalIgnoreCase) || HasQueryToken(url, "os=bos"))
                return 90;
            // Check domestic COS after overseas (mirrorcosov contains "mirrorcos").
            if (host.Contains("mirrorcos", StringComparison.OrdinalIgnoreCase) || HasQueryToken(url, "os=cos"))
                return 85;
            if (host.Contains("mirrorhw", StringComparison.OrdinalIgnoreCase) || HasQueryToken(url, "os=hw"))
                return 80;
            if (host.Contains("mirrorws", StringComparison.OrdinalIgnoreCase) || HasQueryToken(url, "os=ws"))
                return 75;
            if (host.Contains("mirrorali", StringComparison.OrdinalIgnoreCase) || HasQueryToken(url, "os=ali"))
                return 70;
            return 60;
        }

        if (host.Contains("hdslb.com", StringComparison.OrdinalIgnoreCase))
            return 40;
        if (host.Contains("upos", StringComparison.OrdinalIgnoreCase))
            return 20;
        return 0;
    }

    /// <summary>Same DASH object across CDNs (e.g. <c>41747222317-1-30080.m4s</c>).</summary>
    public static string ObjectKey(Uri url)
    {
        var name = Path.GetFileName(url.AbsolutePath);
        return string.IsNullOrWhiteSpace(name) ? url.AbsolutePath : name;
    }

    public static bool IsMediaHost(Uri url)
    {
        var host = url.Host;
        return host.Contains("bilivideo", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("hdslb.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("akamaized.net", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("upos", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Bilibili CDNs report Content-Range totals that jitter by a few KB for the same m4s.
    /// Aligned 206 resumes must keep that prefix instead of restarting from zero.
    /// </summary>
    public static bool CanKeepAlignedResume(Uri url, long priorTotal, long newTotal)
    {
        if (!IsMediaHost(url))
            return false;
        if (priorTotal <= 0 || newTotal <= 0)
            return false;
        return Math.Abs(priorTotal - newTotal) <= 64 * 1024;
    }

    /// <summary>
    /// Same cid/qn DASH objects with a new signature or CDN. Keep <c>.part</c> after 403 URL renew.
    /// Previous may be video-only (browser capture) while renew returns video+audio — still match
    /// when every previous object key is present on the next variant.
    /// </summary>
    public static bool SameDashObjects(MediaVariant previous, MediaVariant next)
    {
        if (!IsMediaHost(previous.SourceUrl) || !IsMediaHost(next.SourceUrl))
            return false;
        if (previous.Tracks.Count == 0 || next.Tracks.Count == 0)
            return false;

        var available = next.Tracks
            .Select((t, i) => (Index: i, t.Kind, Key: ObjectKey(t.SourceUrl)))
            .ToList();

        foreach (var left in previous.Tracks)
        {
            var key = ObjectKey(left.SourceUrl);
            var match = available.FindIndex(r =>
                string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase) &&
                KindsCompatible(left.Kind, r.Kind));
            if (match < 0)
                return false;
            available.RemoveAt(match);
        }

        return true;
    }

    /// <summary>
    /// Rebuild <paramref name="previous"/>'s track layout with renewed URLs from <paramref name="next"/>
    /// so a video-only job does not suddenly become a mux job after 403 recovery.
    /// </summary>
    public static MediaVariant? AlignDashRenewal(MediaVariant previous, MediaVariant next)
    {
        if (!SameDashObjects(previous, next))
            return null;

        var unused = next.Tracks.ToList();
        var tracks = new List<MediaTrack>(previous.Tracks.Count);
        foreach (var left in previous.Tracks)
        {
            var key = ObjectKey(left.SourceUrl);
            var idx = unused.FindIndex(t =>
                string.Equals(ObjectKey(t.SourceUrl), key, StringComparison.OrdinalIgnoreCase) &&
                KindsCompatible(left.Kind, t.Kind));
            if (idx < 0)
                return null;
            var right = unused[idx];
            unused.RemoveAt(idx);
            tracks.Add(left with
            {
                SourceUrl = right.SourceUrl,
                RequestContext = right.RequestContext,
                ContentLength = right.ContentLength ?? left.ContentLength,
                Codec = right.Codec ?? left.Codec,
                Container = right.Container ?? left.Container,
                Bandwidth = right.Bandwidth ?? left.Bandwidth,
                BrowserObserved = false,
                IsValidated = false,
                Evidence = MediaEvidence.Heuristic
            });
        }

        return previous with
        {
            Tracks = tracks,
            Bandwidth = next.Bandwidth ?? previous.Bandwidth,
            Width = next.Width ?? previous.Width,
            Height = next.Height ?? previous.Height
        };
    }

    /// <summary>
    /// True when a renewed variant exposes a video/combined track whose size matches the
    /// partially downloaded object (CDN may change while qn/object bytes stay the same).
    /// </summary>
    public static bool SameDashSize(MediaVariant previous, MediaVariant next, long? expectedTotal)
    {
        if (!IsMediaHost(previous.SourceUrl) || !IsMediaHost(next.SourceUrl))
            return false;
        var target = ResolveSizeAnchor(previous, expectedTotal);
        if (target is not > 0)
            return false;

        return next.Tracks.Any(t =>
            t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined &&
            t.ContentLength is long len &&
            Math.Abs(len - target.Value) <= 64 * 1024 &&
            SameDashCid(previous.SourceUrl, t.SourceUrl));
    }

    /// <summary>
    /// Map previous track layout onto a size-matched renewed object when the exact m4s name
    /// is missing from the first ladder pick but the same cid+bytes still exist.
    /// </summary>
    public static MediaVariant? AlignDashRenewalBySize(MediaVariant previous, MediaVariant next, long? expectedTotal)
    {
        if (!SameDashSize(previous, next, expectedTotal))
            return null;

        var target = ResolveSizeAnchor(previous, expectedTotal)!.Value;
        var unused = next.Tracks.ToList();
        var tracks = new List<MediaTrack>(previous.Tracks.Count);
        foreach (var left in previous.Tracks)
        {
            var idx = unused.FindIndex(t =>
                KindsCompatible(left.Kind, t.Kind) &&
                t.ContentLength is long len &&
                Math.Abs(len - (left.ContentLength is > MediaResourceSizeFilter.MinProgressiveVideoBytes
                    ? left.ContentLength.Value
                    : target)) <= 64 * 1024 &&
                SameDashCid(left.SourceUrl, t.SourceUrl));
            if (idx < 0 && left.Kind is MediaTrackKind.Video or MediaTrackKind.Combined)
            {
                idx = unused.FindIndex(t =>
                    t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined &&
                    t.ContentLength is long len &&
                    Math.Abs(len - target) <= 64 * 1024 &&
                    SameDashCid(left.SourceUrl, t.SourceUrl));
            }

            if (idx < 0)
                return null;
            var right = unused[idx];
            unused.RemoveAt(idx);
            tracks.Add(left with
            {
                SourceUrl = right.SourceUrl,
                RequestContext = right.RequestContext,
                ContentLength = right.ContentLength ?? left.ContentLength,
                Codec = right.Codec ?? left.Codec,
                Container = right.Container ?? left.Container,
                Bandwidth = right.Bandwidth ?? left.Bandwidth,
                BrowserObserved = false,
                IsValidated = false,
                Evidence = MediaEvidence.Heuristic
            });
        }

        return previous with
        {
            Tracks = tracks,
            Bandwidth = next.Bandwidth ?? previous.Bandwidth,
            Width = next.Width ?? previous.Width,
            Height = next.Height ?? previous.Height
        };
    }

    private static long? ResolveSizeAnchor(MediaVariant previous, long? expectedTotal)
    {
        if (expectedTotal is > MediaResourceSizeFilter.MinProgressiveVideoBytes)
            return expectedTotal;
        var fromTracks = previous.Tracks
            .Where(t => t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined)
            .Select(t => t.ContentLength)
            .FirstOrDefault(l => l is > MediaResourceSizeFilter.MinProgressiveVideoBytes);
        return fromTracks ?? previous.TotalContentLength;
    }

    /// <summary>Same cid stem, e.g. <c>41791391545-1-</c> before the quality id.</summary>
    public static bool SameDashCid(Uri left, Uri right)
    {
        var a = ObjectKey(left);
        var b = ObjectKey(right);
        var ai = a.LastIndexOf('-');
        var bi = b.LastIndexOf('-');
        if (ai <= 0 || bi <= 0)
            return false;
        return string.Equals(a[..ai], b[..bi], StringComparison.OrdinalIgnoreCase);
    }

    private static bool KindsCompatible(MediaTrackKind left, MediaTrackKind right) =>
        left == right ||
        ((left is MediaTrackKind.Video or MediaTrackKind.Combined) &&
         (right is MediaTrackKind.Video or MediaTrackKind.Combined));

    /// <summary>
    /// Overseas COS/HW mirrors (<c>cosov</c> / <c>os=cosovbv</c>) share the "cos" substring
    /// with domestic COS and must not inherit that score.
    /// </summary>
    private static bool IsOverseasMirror(Uri url)
    {
        var host = url.Host;
        if (host.Contains("cosov", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("hwov", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("aliov", StringComparison.OrdinalIgnoreCase))
            return true;

        var os = GetQueryValue(url, "os");
        return os is not null && os.Contains("ov", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasQueryToken(Uri url, string token)
    {
        var query = url.Query;
        if (query.Length <= 1)
            return false;

        var rest = query.AsSpan(1);
        while (rest.Length > 0)
        {
            var amp = rest.IndexOf('&');
            var part = amp < 0 ? rest : rest[..amp];
            if (part.Equals(token, StringComparison.OrdinalIgnoreCase))
                return true;
            if (amp < 0)
                break;
            rest = rest[(amp + 1)..];
        }

        return false;
    }

    private static string? GetQueryValue(Uri url, string key)
    {
        var query = url.Query;
        if (query.Length <= 1)
            return null;

        var rest = query.AsSpan(1);
        while (rest.Length > 0)
        {
            var amp = rest.IndexOf('&');
            var part = amp < 0 ? rest : rest[..amp];
            var eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Equals(key, StringComparison.OrdinalIgnoreCase))
                return part[(eq + 1)..].ToString();
            if (amp < 0)
                break;
            rest = rest[(amp + 1)..];
        }

        return null;
    }
}
