namespace VideoDownloader.Infrastructure.Detection;

/// <summary>
/// Normalizes media URLs so ABR/range/segment jitter does not look like a new media session.
/// Morphological only — no site-specific rules.
/// </summary>
public static class MediaUrlNormalizer
{
    private static readonly HashSet<string> VolatileQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        // Range/ABR transport noise only — keep format discriminators (itag/mime/id).
        "range", "rn", "sq", "rbuf", "bytes", "clen", "dur", "lmt",
        "ipv6", "n", "cmfc", "cmfy", "keepalive", "requiressl",
        "_", "__", "t", "timestamp", "ts", "cache", "x-tim",
        "mt", "mv", "ms", "mm", "mn", "pl", "nh", "txp"
    };

    /// <summary>
    /// ByteDance MSE adaptive fMP4 track paths (<c>/media-video-*</c>, <c>/media-audio-*</c>).
    /// Not progressive muxed VOD — ordinary download/remux must reject these.
    /// </summary>
    public static bool IsByteDanceMseTrack(Uri url)
    {
        var path = url.AbsolutePath;
        return path.Contains("/media-video-", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/media-audio-", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsByteDanceMseVideoTrack(Uri url) =>
        url.AbsolutePath.Contains("/media-video-", StringComparison.OrdinalIgnoreCase);

    public static bool IsLikelySegment(Uri url)
    {
        var path = url.AbsolutePath.ToLowerInvariant();
        if (path.EndsWith(".m3u8") || path.EndsWith(".mpd")) return false;
        if (IsByteDanceMseTrack(url))
            return true;
        if (path.Contains("/seg") ||
            path.Contains("segment") ||
            path.Contains("/chunk") ||
            path.Contains("/frag"))
            return true;

        // Short numeric / init fragment names — not DASH baseURLs that merely end with .m4s.
        var file = Path.GetFileName(path);
        if (file.StartsWith("init", StringComparison.OrdinalIgnoreCase) &&
            (file.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase) ||
             file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (System.Text.RegularExpressions.Regex.IsMatch(file, @"^\d{1,6}(?:_\d+)?\.(m4s|ts)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return true;

        // HLS MPEG-TS slices: 1000k_00000.ts, index0.ts, seg-12.ts — not whole-file progressive.
        if (file.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) &&
            System.Text.RegularExpressions.Regex.IsMatch(file, @"\d+\.ts$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Player/gateway shells often embed the real stream in <c>?url=</c> (e.g. /play/?url=…m3u8).
    /// Prefer the embedded absolute media address over probing the HTML wrapper.
    /// </summary>
    public static bool TryUnwrapEmbeddedMediaUrl(Uri url, out Uri mediaUrl)
    {
        mediaUrl = null!;
        if (url.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            url.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!TryReadQueryValue(url, "url", out var raw) &&
            !TryReadQueryValue(url, "src", out raw))
            return false;

        raw = Uri.UnescapeDataString(raw.Replace("+", "%20")).Trim().Trim('"');
        if (raw.StartsWith("//", StringComparison.Ordinal))
            raw = "https:" + raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var candidate))
            return false;
        if (candidate.Scheme is not ("http" or "https"))
            return false;

        var path = candidate.AbsolutePath;
        var looksManifest =
            path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase) ||
            candidate.AbsoluteUri.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            candidate.AbsoluteUri.Contains(".mpd", StringComparison.OrdinalIgnoreCase);
        if (!looksManifest)
            return false;

        mediaUrl = candidate;
        return !IsSameMedia(url, candidate);
    }

    private static bool TryReadQueryValue(Uri url, string key, out string value)
    {
        value = string.Empty;
        var query = url.Query;
        if (string.IsNullOrEmpty(query))
            return false;

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            var name = idx >= 0 ? part[..idx] : part;
            if (!name.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;
            value = idx >= 0 ? part[(idx + 1)..] : string.Empty;
            return value.Length > 0;
        }

        return false;
    }

    /// <summary>
    /// Coarse key for "which content is playing".
    /// Query strings are ignored — ABR / range / itag jitter must not look like a new session.
    /// Segments collapse to their directory.
    /// </summary>
    public static string SessionKey(Uri url)
    {
        var builder = new UriBuilder(url) { Fragment = string.Empty, Query = string.Empty };
        if (IsLikelySegment(url))
        {
            var path = builder.Path;
            var idx = path.LastIndexOf('/');
            builder.Path = idx > 0 ? path[..idx] : path;
        }

        return builder.Uri.AbsoluteUri.TrimEnd('/').ToLowerInvariant();
    }

    /// <summary>
    /// Audio-only URLs must not drive media-session switches (they flash-clear video results).
    /// </summary>
    public static bool IsAudioOnlyUrl(Uri url, string? mime = null)
    {
        if (mime is not null &&
            mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) &&
            !mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return true;

        var path = url.AbsolutePath;
        return path.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".aac", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".opus", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase);
    }

    public static string Normalize(Uri url)
    {
        var builder = new UriBuilder(url) { Fragment = string.Empty };
        if (string.IsNullOrEmpty(builder.Query))
            return builder.Uri.AbsoluteUri.TrimEnd('/');

        var query = builder.Query.TrimStart('?');
        var kept = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            var key = idx >= 0 ? part[..idx] : part;
            var value = idx >= 0 ? part[(idx + 1)..] : string.Empty;
            if (VolatileQueryKeys.Contains(key) || key.StartsWith("x-", StringComparison.OrdinalIgnoreCase))
                continue;
            kept[key] = value;
        }

        builder.Query = kept.Count == 0
            ? string.Empty
            : string.Join('&', kept.Select(kv => $"{kv.Key}={kv.Value}"));
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    public static bool IsSameMedia(Uri a, Uri b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    public static bool IsSameSession(Uri a, Uri b) =>
        string.Equals(SessionKey(a), SessionKey(b), StringComparison.OrdinalIgnoreCase);
}
