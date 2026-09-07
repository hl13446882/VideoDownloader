namespace VideoDownloader.Core.Manifests;

public static class DrmAnalyzer
{
    private static readonly string[] DrmKeyFormatMarkers =
    [
        "widevine",
        "playready",
        "fairplay",
        "clearkey",
        "primetime",
        "marlin"
    ];

    private static readonly string[] DrmSchemeMarkers =
    [
        "urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed", // Widevine
        "urn:uuid:9a04f079-9840-4286-ab92-e65be0885f95", // PlayReady
        "urn:uuid:94ce86fb-07ff-4f43-933b-b6972aa3dd4", // FairPlay
        "urn:mpeg:dash:mp4protection:2011"
    ];

    public static bool IsHlsDrmProtected(string manifestContent)
    {
        foreach (var line in manifestContent.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
                continue;

            if (IsDrmKeyLine(trimmed))
                return true;
        }

        return false;
    }

    public static bool IsDrmKeyLine(string keyLine)
    {
        var method = ManifestParserUtil.GetAttribute(keyLine, "METHOD");
        if (string.Equals(method, "NONE", StringComparison.OrdinalIgnoreCase))
            return false;

        var keyFormat = ManifestParserUtil.GetAttribute(keyLine, "KEYFORMAT");
        if (!string.IsNullOrWhiteSpace(keyFormat) &&
            DrmKeyFormatMarkers.Any(m => keyFormat.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return true;

        var keyFormatVersions = ManifestParserUtil.GetAttribute(keyLine, "KEYFORMATVERSIONS");
        if (!string.IsNullOrWhiteSpace(keyFormatVersions) &&
            DrmKeyFormatMarkers.Any(m => keyFormatVersions.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return true;

        var uri = ManifestParserUtil.GetAttribute(keyLine, "URI") ?? string.Empty;
        if (uri.Contains("widevine", StringComparison.OrdinalIgnoreCase) ||
            uri.Contains("playready", StringComparison.OrdinalIgnoreCase) ||
            uri.Contains("fairplay", StringComparison.OrdinalIgnoreCase))
            return true;

        // AES-128 / SAMPLE-AES alone are encryption signals, not license DRM.
        return false;
    }

    public static bool IsDashDrmProtected(string mpdContent)
    {
        if (!mpdContent.Contains("ContentProtection", StringComparison.OrdinalIgnoreCase))
            return false;

        var lower = mpdContent.ToLowerInvariant();
        if (DrmSchemeMarkers.Any(s => lower.Contains(s, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (lower.Contains("widevine") || lower.Contains("playready") || lower.Contains("fairplay"))
            return true;

        return mpdContent.Contains("ContentProtection", StringComparison.OrdinalIgnoreCase);
    }
}
