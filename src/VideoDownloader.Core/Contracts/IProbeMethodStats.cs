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

    void RecordTimed(string detectorId, string method, bool success, TimeSpan elapsed) =>
        Record(detectorId, method, success);

    double SuccessRate(string detectorId, string method);

    int Attempts(string detectorId, string method);

    int Successes(string detectorId, string method);

    /// <summary>Legacy entry name; order by recent probability per unit of probe time.</summary>
    IReadOnlyList<T> OrderBySuccessCount<T>(string detectorId, IReadOnlyList<T> items, Func<T, string> methodOf);

    /// <summary>
    /// Callable methods ordered by historical success rate (untried methods use a neutral 50% prior).
    /// Ties keep <see cref="ProbeDetectors.CallableMethodsOf"/> order.
    /// Passive network observations run alongside the selected external client.
    /// </summary>
    IReadOnlyList<string> OrderedMethods(string detectorId);

    /// <summary>True when this detector's leading callable method is a yt-dlp client.</summary>
    bool PreferYtdlpFirst(string detectorId);

    /// <summary>
    /// This method's successes as a percent of all successes recorded for <paramref name="detectorId"/>.
    /// Zero when the detector has no successes yet.
    /// </summary>
    int MethodSharePercent(string detectorId, string method);

    /// <summary>Tell listeners a method is starting, with its own historical success percentage.</summary>
    void ReportMethod(string detectorId, string method);

    event EventHandler<ProbeMethodNotice>? MethodStarted;

    string? LastWinningMethod(string detectorId);

    IReadOnlyList<ProbeMethodStatRow> Snapshot();

    void CommitRound(string runId, IReadOnlyList<ProbeMethodRoundEntry> entries, string? markdownDocPath = null, string? historyDir = null);
}

public sealed record ProbeMethodNotice(string DetectorId, string Method, int SuccessPercent);

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

    // —— Generic ——
    public const string BrowserPlay = "browser_play";
    public const string Morphology = "morphology";
    public const string DomMorphology = "dom_morphology";
    public const string Manifest = "manifest";

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
        SiteIds.Generic =>
        [
            ProbeMethods.YtDlp,
            ProbeMethods.BrowserPlay,
            ProbeMethods.Morphology,
            ProbeMethods.DomMorphology,
            ProbeMethods.Manifest
        ],
        _ => [ProbeMethods.NetworkMedia]
    };

    /// <summary>
    /// Methods a detector may invoke or adopt, in cold-start order.
    /// Album stays a content-mode branch. <see cref="ProbeMethods.PlayurlApi"/> stays out until implemented.
    /// YouTube's list is the three clients; network media is always appended by the stats store.
    /// </summary>
    public static IReadOnlyList<string> CallableMethodsOf(string detectorId) => detectorId switch
    {
        SiteIds.YouTube =>
        [
            ProbeMethods.YtDlpPotMweb,
            ProbeMethods.YtDlpDefault,
            ProbeMethods.YtDlpWebEmbedded
        ],
        SiteIds.Bilibili =>
        [
            ProbeMethods.YtDlp,
            ProbeMethods.NetworkPlayurl
        ],
        SiteIds.Douyin =>
        [
            ProbeMethods.AwemeDetail,
            ProbeMethods.NetworkMedia,
            ProbeMethods.VideoElement,
            ProbeMethods.RouterData
        ],
        SiteIds.TikTok =>
        [
            ProbeMethods.YtDlp,
            ProbeMethods.WebData,
            ProbeMethods.NetworkMedia,
            ProbeMethods.VideoElement
        ],
        SiteIds.Generic => MethodsOf(SiteIds.Generic),
        _ when detectorId.StartsWith(SiteIds.Generic + "@", StringComparison.OrdinalIgnoreCase) => MethodsOf(SiteIds.Generic),
        _ => [ProbeMethods.NetworkMedia]
    };

    public static bool IsYtDlpMethod(string? method) =>
        method is ProbeMethods.YtDlp
            or ProbeMethods.YtDlpPotMweb
            or ProbeMethods.YtDlpDefault
            or ProbeMethods.YtDlpWebEmbedded;

    public static bool HasYtDlp(string detectorId) =>
        CallableMethodsOf(detectorId).Any(IsYtDlpMethod);
}

/// <summary>Expected-speed order inside one detector. Unknown sites retain the cold-start list.</summary>
public static class ProbeMethodRanking
{
    public static string Scope(string siteId, Uri page) => siteId == SiteIds.Generic
        ? SiteIds.Generic + "@" + page.IdnHost.ToLowerInvariant()
        : siteId;

    public static IReadOnlyList<string> Order(IProbeMethodStats? stats, string detectorId)
    {
        if (stats is not null)
        {
            var ordered = stats.OrderedMethods(detectorId);
            if (ordered is { Count: > 0 })
                return ordered;
        }

        return ColdStart(detectorId);
    }

    public static IReadOnlyList<string> ColdStart(string detectorId)
    {
        var callable = ProbeDetectors.CallableMethodsOf(detectorId);
        if (!string.Equals(detectorId, SiteIds.YouTube, StringComparison.OrdinalIgnoreCase))
            return callable;
        return callable.Concat([ProbeMethods.NetworkMedia]).ToArray();
    }

    public static bool Outranks(IReadOnlyList<string> ordered, string? left, string? right)
    {
        var leftIndex = IndexOf(ordered, left);
        var rightIndex = IndexOf(ordered, right);
        if (leftIndex < 0)
            return false;
        if (rightIndex < 0)
            return true;
        return leftIndex < rightIndex;
    }

    /// <summary>Highest-ranked method that appears in <paramref name="present"/>.</summary>
    public static string? BestPresent(IReadOnlyList<string> ordered, IEnumerable<string?> present)
    {
        var available = new HashSet<string>(
            present.Where(method => !string.IsNullOrWhiteSpace(method))!,
            StringComparer.OrdinalIgnoreCase);
        foreach (var method in ordered)
        {
            if (available.Contains(method))
                return method;
        }

        return available.FirstOrDefault();
    }

    private static int IndexOf(IReadOnlyList<string> ordered, string? method)
    {
        if (string.IsNullOrWhiteSpace(method))
            return -1;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (string.Equals(ordered[i], method, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }
}
