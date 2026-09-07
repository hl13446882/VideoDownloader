using System.Text.RegularExpressions;

namespace VideoDownloader.Core.Manifests;

public static partial class ManifestParserUtil
{
    public static string? GetAttribute(string line, string key = "")
    {
        line = line.Trim();
        if (key == "")
            return line[(line.IndexOf(':') + 1)..];

        var index = line.IndexOf(key + "=\"", StringComparison.Ordinal);
        if (index > -1)
        {
            var startIndex = index + (key + "=\"").Length;
            var endIndex = startIndex + line[startIndex..].IndexOf('"');
            return line[startIndex..endIndex];
        }

        index = line.IndexOf(key + "=", StringComparison.Ordinal);
        if (index > -1)
        {
            var startIndex = index + (key + "=").Length;
            var endIndex = startIndex + line[startIndex..].IndexOf(',');
            return endIndex >= startIndex ? line[startIndex..endIndex] : line[startIndex..];
        }

        return null;
    }

    public static string CombineUrl(string baseUrl, string relativeUrl)
    {
        if (string.IsNullOrEmpty(baseUrl))
            return relativeUrl;

        var uri1 = new Uri(baseUrl);
        return new Uri(uri1, relativeUrl).ToString();
    }

    public static (int? Width, int? Height) ParseResolution(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution))
            return (null, null);

        var parts = resolution.Split('x', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return (null, null);

        return (int.TryParse(parts[0], out var w) ? w : null,
            int.TryParse(parts[1], out var h) ? h : null);
    }

    public static (string? VideoCodec, string? AudioCodec) ParseCodecs(string? codecs)
    {
        if (string.IsNullOrWhiteSpace(codecs))
            return (null, null);

        var items = codecs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? video = null;
        string? audio = null;
        foreach (var item in items)
        {
            if (item.StartsWith("avc", StringComparison.OrdinalIgnoreCase) ||
                item.StartsWith("hvc", StringComparison.OrdinalIgnoreCase) ||
                item.StartsWith("hev", StringComparison.OrdinalIgnoreCase) ||
                item.StartsWith("dvh", StringComparison.OrdinalIgnoreCase) ||
                item.StartsWith("av01", StringComparison.OrdinalIgnoreCase) ||
                item.StartsWith("vp", StringComparison.OrdinalIgnoreCase))
                video ??= item;
            else if (item.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase) ||
                     item.StartsWith("ec-3", StringComparison.OrdinalIgnoreCase) ||
                     item.StartsWith("ac-3", StringComparison.OrdinalIgnoreCase) ||
                     item.StartsWith("opus", StringComparison.OrdinalIgnoreCase) ||
                     item.StartsWith("vorbis", StringComparison.OrdinalIgnoreCase) ||
                     item.StartsWith("flac", StringComparison.OrdinalIgnoreCase))
                audio ??= item;
        }

        return (video, audio);
    }
}
