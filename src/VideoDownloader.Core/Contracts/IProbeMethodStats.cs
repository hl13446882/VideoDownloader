using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Contracts;

/// <summary>
/// Rolling success rates for exclusive-detector probe methods.
/// Used to reorder probing (highest probability first) and to write the per-round stats document.
/// </summary>
public interface IProbeMethodStats
{
    void Record(string siteId, string method, bool success);

    double SuccessRate(string siteId, string method);

    int Attempts(string siteId, string method);

    int Successes(string siteId, string method);

    /// <summary>Stable order: highest success rate first; ties keep input order.</summary>
    IReadOnlyList<T> OrderBySuccessRate<T>(string siteId, IReadOnlyList<T> items, Func<T, string> methodOf);

    /// <summary>True when yt-dlp historically beats network/browser/dom for this site.</summary>
    bool PreferYtdlpFirst(string siteId);

    string? LastWinningMethod(string siteId);

    IReadOnlyList<ProbeMethodStatRow> Snapshot();

    /// <summary>Append one acceptance/test round into the living markdown + JSON history.</summary>
    void CommitRound(string runId, IReadOnlyList<ProbeMethodRoundEntry> entries, string? markdownDocPath = null, string? historyDir = null);
}

public sealed record ProbeMethodStatRow(
    string SiteId,
    string Method,
    int Attempts,
    int Successes,
    double Rate);

public sealed record ProbeMethodRoundEntry(
    string SiteId,
    string? Address,
    string? WinningMethod,
    bool Pass,
    int? DiscoveryMs,
    IReadOnlyList<string>? InvalidProbes);

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
