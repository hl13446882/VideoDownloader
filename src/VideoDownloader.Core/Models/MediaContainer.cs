namespace VideoDownloader.Core.Models;

/// <summary>Canonical container names — routing and family detection must agree on hls/dash.</summary>
public static class MediaContainer
{
    public static string? Normalize(string? container, Uri? url = null)
    {
        var value = container?.Trim().ToLowerInvariant();
        if (value is "hls" or "m3u8" or "m3u" or
            "application/vnd.apple.mpegurl" or "application/x-mpegurl")
            return "hls";
        if (value is "dash" or "mpd" or "application/dash+xml")
            return "dash";

        if (url is not null)
        {
            var path = url.AbsolutePath;
            if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                return "hls";
            if (path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
                return "dash";
        }

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static bool IsManifest(string? container, Uri? url = null) =>
        Normalize(container, url) is "hls" or "dash";

    /// <summary>
    /// Canonicalizes track containers from manifest URLs and fills an empty variant container
    /// from its primary track. A concrete variant container (mkv/mp4) is preserved.
    /// </summary>
    public static MediaVariant NormalizeVariant(MediaVariant variant)
    {
        var tracks = variant.Tracks
            .Select(t =>
            {
                var normalized = Normalize(t.Container, t.SourceUrl);
                return string.Equals(normalized, t.Container, StringComparison.Ordinal)
                    ? t
                    : t with { Container = normalized };
            })
            .ToArray();
        var container = Normalize(variant.Container)
                        ?? tracks.FirstOrDefault(t =>
                            t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined)?.Container
                        ?? tracks.FirstOrDefault()?.Container;
        return variant with { Container = container, Tracks = tracks };
    }
}
