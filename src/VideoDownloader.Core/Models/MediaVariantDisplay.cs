namespace VideoDownloader.Core.Models;

/// <summary>Formats resolution/size text for variant dropdowns.</summary>
public static class MediaVariantDisplay
{
    /// <summary>
    /// Best-effort size for UI labels. Exact when every track reports length;
    /// otherwise sum known lengths or estimate from bitrate × duration.
    /// </summary>
    public static (long? Bytes, bool Approximate) ResolveDisplaySize(
        MediaVariant variant,
        double? durationSec)
    {
        // Playlist text / MPEG-TS slice lengths are not whole-file sizes.
        var trustworthy = variant.Tracks.Where(HasTrustworthyContentLength).ToArray();
        if (trustworthy.Length > 0 && trustworthy.Length == variant.Tracks.Count)
        {
            var exact = trustworthy.Sum(t => t.ContentLength!.Value);
            if (exact > 0)
                return (exact, false);
        }

        var known = trustworthy.Sum(t => t.ContentLength!.Value);
        if (known > 0)
            return (known, true);

        var bitrate = variant.Bandwidth
                      ?? variant.Tracks.Where(t => t.Bandwidth is > 0).Select(t => t.Bandwidth!.Value).DefaultIfEmpty(0).Sum();
        if (bitrate > 0 && durationSec is > 0.5)
            return ((long)(bitrate / 8.0 * durationSec.Value), true);

        return (null, false);
    }

    /// <summary>
    /// True when <see cref="MediaTrack.ContentLength"/> is a progressive object size, not playlist/segment bytes.
    /// </summary>
    public static bool HasTrustworthyContentLength(MediaTrack track)
    {
        if (track.ContentLength is null or <= 0)
            return false;

        if (string.Equals(track.Container, "hls", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(track.Container, "dash", StringComparison.OrdinalIgnoreCase))
            return false;

        var path = track.SourceUrl.AbsolutePath;
        if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var i = 0;
        while (size >= 1024 && i < units.Length - 1)
        {
            size /= 1024;
            i++;
        }

        return $"{size:F1} {units[i]}";
    }

    public static string BuildLabel(MediaVariant variant, double? durationSec = null)
    {
        var label = variant.VariantId;
        if (variant.Height is > 0)
        {
            var heightTag = $"{variant.Height}p";
            if (!label.Contains(heightTag, StringComparison.OrdinalIgnoreCase))
                label = string.IsNullOrWhiteSpace(label) || label is "default" or "stream" or "probe" or "视频"
                    ? heightTag
                    : $"{label} · {heightTag}";
        }

        var (size, approximate) = ResolveDisplaySize(variant, durationSec);
        if (size is > 0)
            label += approximate
                ? $" [~{FormatBytes(size.Value)}]"
                : $" [{FormatBytes(size.Value)}]";

        return label;
    }
}
