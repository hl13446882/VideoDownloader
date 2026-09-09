namespace VideoDownloader.Infrastructure.Browser;

/// <summary>Generic/Other observation — no Douyin/TikTok album helpers, no Bilibili/YouTube exclusives.</summary>
internal static class GenericObservationScript
{
    /// <summary>Reuse the legacy generic-capable script body for Other sites only.</summary>
    internal static string Body => ExtractBody(VideoObservationScript.Install);

    private static string ExtractBody(string install)
    {
        // VideoObservationScript.Install is (() => { ... })(); — strip outer wrapper for embedding.
        const string prefix = "(() => {";
        const string suffix = "})();";
        var trimmed = install.Trim();
        if (trimmed.StartsWith(prefix, StringComparison.Ordinal) &&
            trimmed.EndsWith(suffix, StringComparison.Ordinal))
        {
            return trimmed[prefix.Length..^suffix.Length];
        }

        return install;
    }
}
