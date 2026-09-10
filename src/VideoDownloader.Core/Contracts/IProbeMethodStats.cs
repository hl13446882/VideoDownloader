using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Contracts;

/// <summary>
/// Per-detector probe-method success ledger.
/// Rates are NEVER shared across detectors (抖音 ≠ B站 ≠ YouTube ≠ TikTok).
/// </summary>
public interface IProbeMethodStats
{
    /// <param name="detectorId">Exclusive detector id — use <see cref="SiteIds"/> values (youtube/bilibili/douyin/tiktok).</param>
    void Record(string detectorId, string method, bool success);

    double SuccessRate(string detectorId, string method);

    int Attempts(string detectorId, string method);

    int Successes(string detectorId, string method);

    /// <summary>Reorder methods within one detector only.</summary>
    IReadOnlyList<T> OrderBySuccessRate<T>(string detectorId, IReadOnlyList<T> items, Func<T, string> methodOf);

    /// <summary>Within this detector, whether yt-dlp historically beats that detector's network/browser/dom methods.</summary>
    bool PreferYtdlpFirst(string detectorId);

    string? LastWinningMethod(string detectorId);

    IReadOnlyList<ProbeMethodStatRow> Snapshot();

    /// <summary>Rewrite the living per-detector markdown + append round JSON.</summary>
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

/// <summary>Canonical method names — always recorded under a specific detector id.</summary>
public static class ProbeMethods
{
    public const string YtDlp = "ytdlp";
    public const string NetworkCdn = "network_cdn";
    public const string BrowserPlay = "browser_play";
    public const string DomObservation = "dom_observation";
    public const string AlbumImages = "album_images";
    public const string ScheduleYtdlpFirst = "schedule:ytdlp_first";
    public const string ScheduleNetworkFirst = "schedule:network_first";

    public static string YtDlpClient(string? extractorArgs)
    {
        if (string.IsNullOrWhiteSpace(extractorArgs))
            return "ytdlp.client:default";
        const string prefix = "youtube:player_client=";
        var key = extractorArgs.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? extractorArgs[prefix.Length..]
            : extractorArgs;
        return "ytdlp.client:" + key;
    }

    public static string YtDlpUrl(string kind) => "ytdlp.url:" + kind;

    public static string ClassifyTrack(MediaEvidence evidence, bool browserObserved) =>
        evidence switch
        {
            MediaEvidence.BrowserObserved => BrowserPlay,
            MediaEvidence.DomObserved => DomObservation,
            _ when browserObserved => BrowserPlay,
            _ => NetworkCdn
        };
}

/// <summary>Exclusive detectors and the method sets that belong to each one.</summary>
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

    /// <summary>Methods that exist for this detector (used for docs + PreferYtdlpFirst).</summary>
    public static IReadOnlyList<string> MethodsOf(string detectorId) => detectorId switch
    {
        SiteIds.YouTube =>
        [
            ProbeMethods.YtDlp,
            ProbeMethods.NetworkCdn,
            ProbeMethods.BrowserPlay,
            ProbeMethods.DomObservation,
            ProbeMethods.YtDlpClient("android,web"),
            ProbeMethods.YtDlpClient("ios,web"),
            ProbeMethods.YtDlpClient("web"),
            ProbeMethods.YtDlpClient("tv_embedded"),
            ProbeMethods.YtDlpClient(null)
        ],
        SiteIds.Bilibili =>
        [
            ProbeMethods.YtDlp,
            ProbeMethods.NetworkCdn,
            ProbeMethods.BrowserPlay,
            ProbeMethods.DomObservation
        ],
        SiteIds.Douyin =>
        [
            // Douyin has no yt-dlp path — only network/DOM/album.
            ProbeMethods.NetworkCdn,
            ProbeMethods.BrowserPlay,
            ProbeMethods.DomObservation,
            ProbeMethods.AlbumImages
        ],
        SiteIds.TikTok =>
        [
            ProbeMethods.YtDlp,
            ProbeMethods.NetworkCdn,
            ProbeMethods.BrowserPlay,
            ProbeMethods.DomObservation,
            ProbeMethods.AlbumImages,
            ProbeMethods.YtDlpUrl("embed"),
            ProbeMethods.YtDlpUrl("canonical")
        ],
        _ =>
        [
            ProbeMethods.NetworkCdn,
            ProbeMethods.BrowserPlay,
            ProbeMethods.DomObservation
        ]
    };

    public static bool HasYtDlp(string detectorId) =>
        MethodsOf(detectorId).Contains(ProbeMethods.YtDlp, StringComparer.Ordinal);
}
