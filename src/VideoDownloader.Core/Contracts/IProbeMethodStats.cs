using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Contracts;

/// <summary>
/// Per-detector probe-method success ledger.
/// Rates are NEVER shared across detectors (抖音 ≠ B站 ≠ YouTube ≠ TikTok).
/// Only real address-discovery methods belong here — not every path that merely sees media.
/// </summary>
public interface IProbeMethodStats
{
    /// <param name="detectorId">Exclusive detector id — use <see cref="SiteIds"/> values.</param>
    void Record(string detectorId, string method, bool success);

    double SuccessRate(string detectorId, string method);

    int Attempts(string detectorId, string method);

    int Successes(string detectorId, string method);

    /// <summary>Reorder methods within one detector only.</summary>
    IReadOnlyList<T> OrderBySuccessRate<T>(string detectorId, IReadOnlyList<T> items, Func<T, string> methodOf);

    /// <summary>Within this detector, whether yt-dlp historically beats that detector's network methods.</summary>
    bool PreferYtdlpFirst(string detectorId);

    string? LastWinningMethod(string detectorId);

    IReadOnlyList<ProbeMethodStatRow> Snapshot();

    void CommitRound(string runId, IReadOnlyList<ProbeMethodRoundEntry> entries, string? markdownDocPath = null, string? historyDir = null);
}

public sealed record ProbeMethodStatRow(
    string DetectorId,
    string DetectorName,
    string Method,
    int Attempts,
    int Successes,
    double Rate);

public sealed record ProbeMethodRoundEntry(
    string DetectorId,
    string? Address,
    string? WinningMethod,
    bool Pass,
    int? DiscoveryMs,
    IReadOnlyList<string>? InvalidProbes);

/// <summary>
/// Canonical address-discovery method names, scoped by detector in the ledger.
/// Do not list DOM/browser as peer "main" methods where they are only identity/fallback signals.
/// </summary>
public static class ProbeMethods
{
    // —— YouTube ——
    public const string YtDlpPotMweb = "ytdlp.pot_mweb";
    public const string YtDlpDefault = "ytdlp.default";
    public const string YtDlpWebEmbedded = "ytdlp.web_embedded";
    public const string NetworkMedia = "network_media";

    // —— Bilibili ——
    public const string PlayurlApi = "playurl_api";
    public const string NetworkPlayurl = "network_playurl";
    public const string YtDlp = "ytdlp";

    // —— Douyin ——
    public const string AwemeDetail = "aweme_detail";
    public const string VideoElement = "video_element";
    public const string RouterData = "router_data";
    public const string Album = "album";

    // —— TikTok ——
    public const string WebData = "web_data";

    public const string ScheduleYtdlpFirst = "schedule:ytdlp_first";
    public const string ScheduleNetworkFirst = "schedule:network_first";

    public static string YouTubeClientMethod(string? extractorArgs)
    {
        if (string.IsNullOrWhiteSpace(extractorArgs))
            return YtDlpDefault;
        if (extractorArgs.Contains("mweb", StringComparison.OrdinalIgnoreCase))
            return YtDlpPotMweb;
        if (extractorArgs.Contains("web_embedded", StringComparison.OrdinalIgnoreCase))
            return YtDlpWebEmbedded;
        return YtDlpDefault;
    }

    public static string? YouTubeExtractorArgs(string method) => method switch
    {
        YtDlpPotMweb => "youtube:player_client=mweb",
        YtDlpWebEmbedded => "youtube:player_client=web_embedded",
        YtDlpDefault => null,
        _ => null
    };
}

/// <summary>Exclusive detectors and the real address-discovery methods that belong to each.</summary>
public static class ProbeDetectors
{
    public static readonly string[] All =
    [
        SiteIds.YouTube,
        SiteIds.Bilibili,
        SiteIds.Douyin,
        SiteIds.TikTok
    ];

    public static string DisplayName(string detectorId) => detectorId switch
    {
        SiteIds.YouTube => "YouTube 探测器",
        SiteIds.Bilibili => "B站 探测器",
        SiteIds.Douyin => "抖音 探测器",
        SiteIds.TikTok => "TikTok 探测器",
        SiteIds.Generic => "通用探测器",
        _ => detectorId + " 探测器"
    };

    public static IReadOnlyList<string> MethodsOf(string detectorId) => detectorId switch
    {
        SiteIds.YouTube =>
        [
            ProbeMethods.YtDlpPotMweb,
            ProbeMethods.YtDlpDefault,
            ProbeMethods.YtDlpWebEmbedded,
            ProbeMethods.NetworkMedia
        ],
        SiteIds.Bilibili =>
        [
            ProbeMethods.PlayurlApi,
            ProbeMethods.NetworkPlayurl,
            ProbeMethods.YtDlp
        ],
        SiteIds.Douyin =>
        [
            ProbeMethods.AwemeDetail,
            ProbeMethods.NetworkMedia,
            ProbeMethods.VideoElement,
            ProbeMethods.RouterData,
            ProbeMethods.Album
        ],
        SiteIds.TikTok =>
        [
            ProbeMethods.YtDlp,
            ProbeMethods.WebData,
            ProbeMethods.NetworkMedia,
            ProbeMethods.VideoElement,
            ProbeMethods.Album
        ],
        _ => [ProbeMethods.NetworkMedia]
    };

    public static bool HasYtDlp(string detectorId) => detectorId switch
    {
        SiteIds.YouTube or SiteIds.Bilibili or SiteIds.TikTok => true,
        _ => false
    };
}
