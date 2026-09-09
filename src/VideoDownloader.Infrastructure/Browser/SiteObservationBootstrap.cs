namespace VideoDownloader.Infrastructure.Browser;

/// <summary>
/// Document-created observation bootstrap. Routes by host so exclusive sites never
/// share album/observe helpers with Generic or each other.
/// </summary>
internal static class SiteObservationBootstrap
{
    internal static string Install { get; } =
        "(() => {\n" +
        "  if (window.__vdObserve) return;\n" +
        "  const host = location.hostname || '';\n" +
        "  if (/douyin\\.com|iesdouyin\\.com/i.test(host)) {\n" +
        DouyinObservationScript.Body +
        "    return;\n" +
        "  }\n" +
        "  if (/tiktok\\.com/i.test(host)) {\n" +
        TikTokObservationScript.Body +
        "    return;\n" +
        "  }\n" +
        "  if (/youtube\\.com|youtu\\.be|youtube-nocookie\\.com/i.test(host)) {\n" +
        YouTubeObservationScript.Body +
        "    return;\n" +
        "  }\n" +
        "  if (/bilibili\\.com|b23\\.tv/i.test(host)) {\n" +
        BilibiliObservationScript.Body +
        "    return;\n" +
        "  }\n" +
        GenericObservationScript.Body +
        "})();";
}
