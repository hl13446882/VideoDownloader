using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Download;
using VideoDownloader.Core.Errors;
using VideoDownloader.Core.Models;
using VideoDownloader.Core.Naming;
using VideoDownloader.Core.Sites;
using VideoDownloader.Infrastructure.Browser;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Detection;
using VideoDownloader.Infrastructure.Diagnostics;
using VideoDownloader.Infrastructure.Download;
using VideoDownloader.Infrastructure.LocalLibrary;
using VideoDownloader.Infrastructure.Persistence;
using VideoDownloader.UI.Localization;

namespace VideoDownloader.UI.ViewModels;

public sealed partial class VariantItemViewModel : ObservableObject
{
    public required MediaVariant Variant { get; init; }
    public required string Label { get; init; }
    public required DetectedVideo Parent { get; init; }
}

public sealed partial class DetectedVideoViewModel : ObservableObject
{
    private LocalizationService? _loc;

    public DetectedVideo Video { get; private set; } = null!;
    public ObservableCollection<VariantItemViewModel> AllVariants { get; } = new();
    public ObservableCollection<VariantItemViewModel> Variants { get; } = new();
    public ObservableCollection<string> ModeOptions { get; } = new();

    [ObservableProperty]
    private VariantItemViewModel? _selectedVariant;

    [ObservableProperty]
    private string? _selectedMode = "视频";

    public void BindLocalization(LocalizationService loc)
    {
        if (ReferenceEquals(_loc, loc))
            return;
        _loc = loc;
        RefreshLocalizedModes();
    }

    public void RefreshLocalizedModes()
    {
        if (_loc is null)
            return;
        var audio = IsAudioMode(SelectedMode);
        ModeOptions.Clear();
        ModeOptions.Add(_loc.ModeVideo);
        ModeOptions.Add(_loc.ModeAudio);
        SelectedMode = audio ? _loc.ModeAudio : _loc.ModeVideo;
        OnPropertyChanged(nameof(StatusText));
    }

    public void Update(DetectedVideo video)
    {
        Video = video;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SiteId));
        OnPropertyChanged(nameof(Family));
        OnPropertyChanged(nameof(IsDrm));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(ResolutionText));
    }

    public string Title => Video.DisplayTitle;
    public string SiteId => Video.SiteId;
    public string Family => Video.Family.ToString();
    public bool IsDrm => Video.IsDrmProtected;
    public bool CanDownload => !Video.IsDrmProtected && !Video.IsLive && SelectedVariant is not null;

    public string DurationText
    {
        get
        {
            var sec = Video.DurationSec;
            if (sec is null or <= 0)
                return string.Empty;
            var total = (int)sec.Value;
            var h = total / 3600;
            var m = (total % 3600) / 60;
            var s = total % 60;
            return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m}:{s:D2}";
        }
    }

    public string ResolutionText
    {
        get
        {
            var h = SelectedVariant?.Variant.Height
                    ?? Video.Variants.Where(v => v.Height is > 0)
                        .MaxBy(v => v.Height)?.Height;
            return h is > 0 ? $"{h}p" : string.Empty;
        }
    }

    public string InfoText
    {
        get
        {
            var parts = new[] { DurationText, ResolutionText }
                .Where(s => !string.IsNullOrEmpty(s));
            return string.Join("  ·  ", parts);
        }
    }

    public string StatusText
    {
        get
        {
            if (Video.IsDrmProtected)
                return _loc?.T("detect.drm") ?? "受 DRM 保护，不支持下载";
            if (Video.Variants.Count == 0)
                return _loc?.T("detect.parsing") ?? "解析中...";
            if (!string.IsNullOrWhiteSpace(Video.StatusHint))
                return Video.StatusHint;
            return Video.ProbeSource == ProbeSource.GenericFallback
                ? (_loc?.T("detect.generic") ?? "已使用通用探测")
                : string.Empty;
        }
    }

    partial void OnSelectedVariantChanged(VariantItemViewModel? value)
    {
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(ResolutionText));
    }

    partial void OnSelectedModeChanged(string? value) => ApplyModeFilter();

    public void ReplaceVariants(DetectedVideo video)
    {
        var previousMode = SelectedMode;
        var previousVariantId = SelectedVariant?.Variant.VariantId;
        var previousUrl = SelectedVariant?.Variant.SourceUrl.AbsoluteUri;

        var built = MediaVariantRanking.Rank(
                video.Variants.Where(v => !MediaResourceSizeFilter.ShouldExcludeFromDropdown(v)))
            .Select(variant => FormatVariantItem(variant, video))
            .GroupBy(v => v.Label, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToArray();

        // Detach selection first so WPF ComboBox drops cached historical items.
        SelectedVariant = null;
        Variants.Clear();
        AllVariants.Clear();

        foreach (var item in built)
            AllVariants.Add(item);

        var videoLabel = _loc?.ModeVideo ?? "视频";
        var audioLabel = _loc?.ModeAudio ?? "音轨";
        var hasVideo = AllVariants.Any(v => MatchesMode(v.Variant, videoLabel));
        var hasAudio = AllVariants.Any(v => MatchesMode(v.Variant, audioLabel));
        var nextMode = IsAudioMode(previousMode)
            ? (hasAudio ? audioLabel : hasVideo ? videoLabel : audioLabel)
            : (hasVideo ? videoLabel : hasAudio ? audioLabel : videoLabel);

        // Set mode without double-filtering when unchanged; always rebuild Variants from AllVariants.
        if (!string.Equals(SelectedMode, nextMode, StringComparison.Ordinal))
            SelectedMode = nextMode;
        else
            ApplyModeFilter();

        // Rank already put largest first; keep prior selection when still present.
        SelectedVariant =
            Variants.FirstOrDefault(v => v.Variant.VariantId == previousVariantId) ??
            Variants.FirstOrDefault(v => v.Variant.SourceUrl.AbsoluteUri == previousUrl) ??
            Variants.FirstOrDefault();
    }

    private static VariantItemViewModel FormatVariantItem(MediaVariant variant, DetectedVideo parent) =>
        new()
        {
            Variant = variant,
            Label = MediaVariantDisplay.BuildLabel(variant, parent.DurationSec),
            Parent = parent
        };

    public void ApplyModeFilter()
    {
        var mode = string.IsNullOrWhiteSpace(SelectedMode) ? "视频" : SelectedMode;
        var previousId = SelectedVariant?.Variant.VariantId;
        var previousUrl = SelectedVariant?.Variant.SourceUrl.AbsoluteUri;

        SelectedVariant = null;
        Variants.Clear();
        foreach (var item in AllVariants.Where(v => MatchesMode(v.Variant, mode)))
            Variants.Add(item);

        // AllVariants is size-ranked; first match is the largest for this mode.
        SelectedVariant =
            Variants.FirstOrDefault(v => v.Variant.VariantId == previousId) ??
            Variants.FirstOrDefault(v => v.Variant.SourceUrl.AbsoluteUri == previousUrl) ??
            Variants.FirstOrDefault();
        OnPropertyChanged(nameof(CanDownload));
    }

    public static bool IsAudioMode(string? mode) =>
        mode is "音轨" or "Audio";

    public static bool MatchesMode(MediaVariant variant, string mode)
    {
        var audioOnly = variant.Tracks.Count > 0 &&
                        variant.Tracks.All(t => t.Kind == MediaTrackKind.Audio);
        return IsAudioMode(mode) ? audioOnly : !audioOnly;
    }

    public static string MapMode(MediaVariant variant) =>
        MatchesMode(variant, "音轨") ? "音轨" : "视频";
}

public sealed partial class QueueGroupViewModel : ObservableObject
{
    public QueueGroupViewModel(string domain, int count, bool isExpanded)
    {
        Domain = domain;
        Count = count;
        IsExpanded = isExpanded;
    }

    public string Domain { get; }
    public int Count { get; }
    public bool IsExpanded { get; }
    public string Glyph => IsExpanded ? "▾" : "▸";
    public string CountText => Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed partial class AutoSessionRowViewModel : ObservableObject
{
    public AutoSessionRowViewModel(AutoSession session, int itemCount, int completedCount, bool isExpanded, LocalizationService loc)
    {
        Session = session;
        ItemCount = itemCount;
        CompletedCount = completedCount;
        IsExpanded = isExpanded;
        _loc = loc;
    }

    private readonly LocalizationService _loc;
    public AutoSession Session { get; }
    public Guid SessionId => Session.Id;
    public string Name => Session.Name;
    public int ItemCount { get; }
    public int CompletedCount { get; }
    public bool IsExpanded { get; }
    public string Glyph => IsExpanded ? "▾" : "▸";
    public string CountText => CompletedCount.ToString(CultureInfo.InvariantCulture) + "/" +
                               ItemCount.ToString(CultureInfo.InvariantCulture);
    public string StatusText => Session.Status switch
    {
        AutoSessionStatus.Running => _loc.T("auto.sessionRunning"),
        AutoSessionStatus.Paused => _loc.T("auto.sessionPaused"),
        AutoSessionStatus.Completed => _loc.T("auto.sessionCompleted"),
        AutoSessionStatus.Failed => _loc.T("auto.sessionFailed"),
        _ => _loc.T("auto.sessionStopped")
    };
}

public sealed partial class AutoQueueItemRowViewModel : ObservableObject
{
    public AutoQueueItemRowViewModel(AutoQueueItem item, DownloadJob? job, LocalizationService loc)
    {
        Item = item;
        Job = job;
        _loc = loc;
    }

    private readonly LocalizationService _loc;
    public AutoQueueItem Item { get; }
    public DownloadJob? Job { get; private set; }
    public Guid ItemId => Item.Id;
    public Guid SessionId => Item.SessionId;
    public string DisplayName => string.IsNullOrWhiteSpace(Item.Caption)
        ? ("#" + Item.Seq.ToString(CultureInfo.InvariantCulture))
        : ("#" + Item.Seq.ToString(CultureInfo.InvariantCulture) + "  " + Item.Caption);
    public AutoQueueItemStatus EffectiveStatus => Job is null ? Item.Status : AutoQueueItemState.FromDownload(Job.Status, Item.Status);
    public string StatusText => EffectiveStatus switch
    {
        AutoQueueItemStatus.Queued => _loc.T("auto.itemQueued"),
        AutoQueueItemStatus.Enqueued => _loc.T("auto.itemEnqueued"),
        AutoQueueItemStatus.Downloading => _loc.T("auto.itemDownloading"),
        AutoQueueItemStatus.Completed => _loc.T("auto.itemCompleted"),
        AutoQueueItemStatus.Failed => _loc.T("auto.itemFailed"),
        AutoQueueItemStatus.Skipped => _loc.T("auto.itemSkipped"),
        _ => _loc.T("auto.itemCancelled")
    };
    public double ProgressPercent
    {
        get
        {
            if (Job is null || Job.TotalBytes is null or <= 0) return 0;
            return Math.Clamp(100.0 * Job.DownloadedBytes / Job.TotalBytes.Value, 0, 100);
        }
    }
    public bool ShowProgress => Job is not null &&
                                EffectiveStatus is AutoQueueItemStatus.Enqueued or AutoQueueItemStatus.Downloading;
    public bool IsCompleted => EffectiveStatus == AutoQueueItemStatus.Completed;

    public void BindJob(DownloadJob? job)
    {
        Job = job;
        OnPropertyChanged(nameof(EffectiveStatus));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(StatusText));
    }
}

public sealed partial class DownloadJobViewModel : ObservableObject
{
    private readonly LocalizationService _loc;
    public DownloadJob Job { get; }

    public DownloadJobViewModel(DownloadJob job, LocalizationService loc)
    {
        Job = job;
        _loc = loc;
    }

    public bool IsGrouped { get; set; }

    public string DisplayName => Job.DisplayName;
    public string DisplayNameEllipsized => Job.DisplayName;
    public string ExtensionLabel => Path.GetExtension(Job.TargetPath);
    public string GroupDomain => DownloadSiteFolder.Resolve(Job.PageUrl);

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editStem = string.Empty;

    partial void OnEditStemChanged(string value)
    {
        var filtered = DownloadFileNameBuilder.FilterLiveInput(value);
        if (!string.Equals(filtered, value, StringComparison.Ordinal))
            EditStem = filtered;
    }

    public void BeginEdit()
    {
        EditStem = Job.DisplayName;
        IsEditing = true;
    }

    public void CancelEdit()
    {
        IsEditing = false;
        EditStem = Job.DisplayName;
    }

    public bool IsFailed => Job.Status == DownloadStatus.Failed;
    public bool IsCompleted => Job.Status == DownloadStatus.Completed;
    public bool IsFileMissing => Job.Status == DownloadStatus.Completed && !File.Exists(Job.TargetPath);

    public string Status
    {
        get
        {
            if (Job.Status == DownloadStatus.Completed && !File.Exists(Job.TargetPath))
                return _loc.T("job.fileDeleted");
            if (Job.Status == DownloadStatus.Failed)
            {
                var reason = ErrorText(Job.LastErrorCode);
                return string.IsNullOrWhiteSpace(reason)
                    ? _loc.T("job.failed")
                    : _loc.T("job.failed") + " · " + reason;
            }

            return Job.Status switch
            {
                DownloadStatus.Removed => _loc.T("job.removed"),
                DownloadStatus.Completed => _loc.T("job.completed"),
                DownloadStatus.Muxing => _loc.T("job.muxing"),
                DownloadStatus.Downloading => _loc.T("job.downloading"),
                DownloadStatus.Pending => _loc.T("job.pending"),
                DownloadStatus.Preparing => _loc.T("job.preparing"),
                DownloadStatus.Paused => _loc.T("job.paused"),
                DownloadStatus.Cancelled => _loc.T("job.cancelled"),
                _ => _loc.T("job.failed")
            };
        }
    }

    public double ProgressPercent => Job.TotalBytes is > 0
        ? Math.Min(100, Job.DownloadedBytes * 100.0 / Job.TotalBytes.Value)
        : 0;

    public bool IsProgressIndeterminate =>
        Job.TotalBytes is null &&
        Job.Status is DownloadStatus.Downloading or DownloadStatus.Preparing or DownloadStatus.Muxing;

    public string ProgressText => Job.Status == DownloadStatus.Completed
        ? $"100% ({FormatBytes(Job.DownloadedBytes)})"
        : Job.TotalBytes is > 0
            ? $"{ProgressPercent:F0}% ({FormatBytes(Job.DownloadedBytes)} / {FormatBytes(Job.TotalBytes!.Value)})"
            : $"{FormatBytes(Job.DownloadedBytes)}";

    public void Refresh()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(DisplayNameEllipsized));
        OnPropertyChanged(nameof(ExtensionLabel));
        OnPropertyChanged(nameof(GroupDomain));
        OnPropertyChanged(nameof(IsGrouped));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsFileMissing));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        OnPropertyChanged(nameof(ProgressText));
    }

    private string ErrorText(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return string.Empty;
        var key = "error." + code;
        var mapped = _loc.T(key);
        return string.Equals(mapped, key, StringComparison.Ordinal) ? code : mapped;
    }

    private static string FormatBytes(long bytes)
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
}

public sealed partial class BrowserTabViewModel : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public WebView2Host Host { get; }
    public WebView2? WebView { get; set; }
    public string? PendingNavigationUrl { get; set; }

    [ObservableProperty]
    private string _title = "New Tab";

    [ObservableProperty]
    private string _address = $"http://127.0.0.1:{LocalLibraryHost.PreferredPort}/?group=time";

    [ObservableProperty]
    private bool _isInitialized;

    /// <summary>True when a media element in this tab is playing with sound.</summary>
    [ObservableProperty]
    private bool _isAudiblePlaying;

    /// <summary>
    /// Fixed pixel width when the strip must shrink under the 90% budget. Null = content width (MaxWidth 200).
    /// </summary>
    [ObservableProperty]
    private double? _fixedWidth;

    public BrowserTabViewModel(WebView2Host host) => Host = host;

    partial void OnTitleChanged(string value) => TitleChanged?.Invoke(this, EventArgs.Empty);

    public event EventHandler? TitleChanged;
}

public sealed class AddressPreset
{
    public AddressPreset(string name, string url)
    {
        Name = name;
        Url = url;
    }

    public string Name { get; }
    public string Url { get; }

    /// <summary>Editable ComboBox writes this into the address text when an item is chosen.</summary>
    public override string ToString() => Url;
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly BrowserHostLocator _hostLocator;
    private readonly IMediaDetectionPipeline _pipeline;
    private readonly IDownloadEngine _downloadEngine;
    private readonly IAutoSessionRepository _autoSessions;
    private readonly AutoDownloadScheduler _autoScheduler;
    private readonly IMediaAggregator _aggregator;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly LocalizationService _loc;
    private readonly AppOptions _options;
    private readonly UserSettingsStore _settingsStore;
    private readonly LocalLibraryHost _localLibrary;
    private readonly HashSet<string> _collapsedQueueGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _collapsedAutoSessions = new();
    private bool _queueGrouped;
    private int _queueGroupingMode;
    private bool ShowAutoTaskGroups => _queueGroupingMode == 2;
    private Guid? _autoDbSessionId;
    private AutoSessionRowViewModel? _selectedAutoSession;
    private AutoQueueItemRowViewModel? _selectedAutoItem;
    private readonly Dictionary<Guid, DetectedVideoViewModel> _videoMap = new();
    private readonly Dictionary<Guid, DetectedVideo> _pendingProbeVideos = new();
    private readonly object _pageSync = new();
    private CancellationTokenSource _probeCts = new();
    private readonly PageProbePublications _probePublications = new();
    private IPageProbePublications PublishedProbes => _probePublications.Reader;
    private Guid? _autoConsumedProbeSessionId;
    private string? _currentPageIdentity;
    private string? _lastNavigatedPageUrl;
    private long _pageGeneration;
    private readonly FeedDetectionDisplayState _feedDisplayState = new();
    private bool _detectionRunning;
    private bool _probeMethodVisible;
    private bool _forceReplaceResults;
    private string? _statusKey;
    private object[]? _statusArgs;
    private CancellationTokenSource? _statusDismissCts;
    private int _statusGeneration;

    private static readonly HashSet<string> StickyStatusKeys = new(StringComparer.Ordinal)
    {
        "status.probeWaiting",
        "status.probeRunning",
        "status.probeMethod",
        "status.probeManual",
        "status.probeFound",
        "status.autoRunning",
        "status.autoRunningCount",
        "status.autoRunningUnlimited",
        "status.autoWaitingSlot",
        "status.autoDelayDownload",
        "status.autoDelaySwitch",
        "status.autoDelayAfterProbe",
        "status.autoNoAddress",
        "status.autoSameContent",
        "update.checking",
        "update.foundVersion",
        "update.downloadingFile",
        "update.restarting",
        "status.importRunning",
    };

    /// <summary>Douyin feed→detail boost already attempted for this aweme id (avoid loops).</summary>
    private string? _douyinDetailBoostId;

    private CancellationTokenSource? _autoCts;
    /// <summary>Tab this auto session belongs to; switching tabs is a manual takeover.</summary>
    private BrowserTabViewModel? _autoTab;
    /// <summary>Until when a navigation is presumed to be this session's own feed advance.</summary>
    private DateTimeOffset _autoExpectedNavigationUntil;
    private int _autoIdleMinutes = 5;
    private int _autoMaxVideos;
    private int _autoProcessedCount;
    /// <summary>Fixed end of the current auto-mode session (start + duration). MaxValue when unlimited.</summary>
    private DateTime _autoSessionEndUtc;
    private DateTimeOffset _autoItemStartedAt;
    /// <summary>
    /// Last auto-enqueued item fingerprint: full page URL + caption. Same pair must not download again
    /// until either side changes (guards failed feed switches).
    /// </summary>
    private string? _autoLastEnqueuedFingerprint;
    private string? _autoFailedFingerprint;

    [ObservableProperty]
    private bool _isAutoMode;

    public string AutoModeButtonLabel => IsAutoMode ? _loc.NavAutoStop : _loc.NavAuto;
    public string QueuePanelHeader => ShowAutoTaskGroups ? _loc.T("side.autoTasks") : _loc.SideQueue;
    public bool CanToggleQueueGrouping => true;

    partial void OnIsAutoModeChanged(bool value)
    {
        OnPropertyChanged(nameof(AutoModeButtonLabel));
        OnPropertyChanged(nameof(QueuePanelHeader));
        OnPropertyChanged(nameof(CanToggleQueueGrouping));
        ToggleQueueGroupingCommand.NotifyCanExecuteChanged();
        _ = RefreshAutoQueueUiAsync();
        if (!value)
            RebuildQueueRows();
    }

    public ObservableCollection<AddressPreset> AddressPresets { get; } = new();

    [ObservableProperty]
    private string _addressBar = $"http://127.0.0.1:{LocalLibraryHost.PreferredPort}/?group=time";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private BrowserTabViewModel? _selectedTab;

    [ObservableProperty]
    private DetectedVideoViewModel? _selectedDetectedVideo;

    [ObservableProperty]
    private DownloadJobViewModel? _selectedDownloadJob;

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public LocalizationService L => _loc;

    [ObservableProperty]
    private bool _canGoBack;

    [ObservableProperty]
    private bool _canGoForward;

    /// <summary>Whether the edge sidebar is expanded.</summary>
    [ObservableProperty]
    private bool _isSidePanelVisible;

    [ObservableProperty]
    private bool _canProbeCurrentPage;

    partial void OnCanProbeCurrentPageChanged(bool value)
    {
        RefreshCommand.NotifyCanExecuteChanged();
        DownloadSelectedVideoCommand.NotifyCanExecuteChanged();
    }

    /// <summary>True when the local player asked the WPF host for true window fullscreen.</summary>
    [ObservableProperty]
    private bool _isAppFullscreen;

    public bool IsAppChromeVisible => !IsAppFullscreen;

    public Thickness WindowContentMargin =>
        IsAppFullscreen ? new Thickness(0) : new Thickness(8);

    public Thickness BrowserPaneMargin =>
        IsSidePanelVisible ? new Thickness(0, 0, 8, 0) : new Thickness(0);

    partial void OnIsSidePanelVisibleChanged(bool value) =>
        OnPropertyChanged(nameof(BrowserPaneMargin));

    partial void OnIsAppFullscreenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAppChromeVisible));
        OnPropertyChanged(nameof(WindowContentMargin));
    }

    public void SetAppFullscreen(bool active)
    {
        if (IsAppFullscreen == active)
            return;
        IsAppFullscreen = active;
        var tab = SelectedTab;
        if (tab?.IsInitialized == true)
            _ = tab.Host.NotifyLocalPlayerFullscreenAsync(active);
    }

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));

    public void SetStatus(string message, bool sticky = false)
    {
        CancelStatusDismiss();
        _statusKey = null;
        _statusArgs = null;
        StatusMessage = message ?? string.Empty;
        if (!sticky && !string.IsNullOrWhiteSpace(StatusMessage))
            ScheduleStatusDismiss();
    }

    public void SetStatusKey(string key, params object[] args)
    {
        CancelStatusDismiss();
        _statusKey = key;
        _statusArgs = args;
        StatusMessage = args.Length == 0 ? _loc.T(key) : _loc.Format(key, args);
        if (!StickyStatusKeys.Contains(key))
            ScheduleStatusDismiss();
    }

    private void SetProbeRunningStatus()
    {
        if (_probeMethodVisible)
            return;
        SetStatusKey("status.probeRunning");
    }

    private void OnProbeMethodStarted(object? sender, ProbeMethodNotice notice)
    {
        var generation = _pageGeneration;
        var apply = () =>
        {
            if (!_detectionRunning || generation != _pageGeneration || _feedDisplayState.IsSuspended)
                return;
            _probeMethodVisible = true;
            SetStatusKey("status.probeMethod", notice.Method, notice.SuccessPercent);
        };
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            apply();
        else
            dispatcher.BeginInvoke(apply);
    }

    public void ClearStatus()
    {
        CancelStatusDismiss();
        _statusKey = null;
        _statusArgs = null;
        if (!string.IsNullOrWhiteSpace(StatusMessage))
            StatusMessage = string.Empty;
    }

    private void ScheduleStatusDismiss()
    {
        var generation = Interlocked.Increment(ref _statusGeneration);
        var cts = new CancellationTokenSource();
        _statusDismissCts = cts;
        _ = DismissStatusAfterAsync(generation, cts.Token);
    }

    private async Task DismissStatusAfterAsync(int generation, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
                return;
            await dispatcher.InvokeAsync(() =>
            {
                if (generation != _statusGeneration)
                    return;
                ClearStatus();
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CancelStatusDismiss()
    {
        try { _statusDismissCts?.Cancel(); } catch { /* ignore */ }
        try { _statusDismissCts?.Dispose(); } catch { /* ignore */ }
        _statusDismissCts = null;
        Interlocked.Increment(ref _statusGeneration);
    }

    public ObservableCollection<BrowserTabViewModel> Tabs { get; } = new();
    public ObservableCollection<DetectedVideoViewModel> DetectedVideos { get; } = new();
    public ObservableCollection<DownloadJobViewModel> DownloadJobs { get; } = new();
    public ObservableCollection<object> QueueRows { get; } = new();

    public bool IsQueueGrouped => _queueGrouped;
    public string QueueGroupToggleLabel => _loc.T(_queueGroupingMode switch { 1 => "btn.groupSite", 2 => "btn.groupAuto", _ => "btn.groupNone" });

    /// <summary>Queue multi-selection (click toggles). Batch ops require every selected row to support them.</summary>
    public IReadOnlyList<DownloadJobViewModel> SelectedDownloadJobs => _selectedDownloadJobs;

    /// <summary>Invoked after queue rebuild so the ListBox can restore multi-selection.</summary>
    public Action<IReadOnlyList<Guid>>? RestoreQueueSelection { get; set; }

    /// <summary>Invoked after an auto-task rebuild so the selected task or item stays selected.</summary>
    public Action<object?>? RestoreAutoRowSelection { get; set; }

    /// <summary>While true, queue selection changes come from a rebuild and must not clear the user's pick.</summary>
    public bool QueueSelectionSuspended { get; private set; }

    private readonly List<DownloadJobViewModel> _selectedDownloadJobs = new();
    private readonly List<Guid> _selectedDownloadJobIds = new();

    private IReadOnlyList<DownloadJobViewModel> EffectiveSelectedJobs =>
        _selectedDownloadJobs.Count > 0
            ? _selectedDownloadJobs
            : SelectedDownloadJob is null
                ? Array.Empty<DownloadJobViewModel>()
                : new[] { SelectedDownloadJob };

    public bool CanPauseSelected =>
        (_selectedAutoSession is { Session.Status: AutoSessionStatus.Running }) ||
        (_selectedAutoItem?.Item.DownloadJobId is not null &&
         _selectedAutoItem.Job?.Status is DownloadStatus.Downloading) ||
        (EffectiveSelectedJobs.Count > 0 &&
         EffectiveSelectedJobs.All(j => j.Job.Status is DownloadStatus.Downloading));

    public bool CanResumeSelected =>
        (_selectedAutoSession is { Session.Status: AutoSessionStatus.Paused or AutoSessionStatus.Stopped or AutoSessionStatus.Failed }) ||
        (_selectedAutoItem?.Item.DownloadJobId is not null &&
         _selectedAutoItem.Job?.Status is DownloadStatus.Paused or DownloadStatus.Failed) ||
        (EffectiveSelectedJobs.Count > 0 &&
         EffectiveSelectedJobs.All(j => j.Job.Status is DownloadStatus.Paused or DownloadStatus.Failed));

    public bool CanCancelSelected =>
        (_selectedAutoSession is { Session.Status: AutoSessionStatus.Running or AutoSessionStatus.Paused }) ||
        (_selectedAutoItem is not null &&
         _selectedAutoItem.Item.Status is not (
             AutoQueueItemStatus.Completed or AutoQueueItemStatus.Cancelled or AutoQueueItemStatus.Skipped)) ||
        (EffectiveSelectedJobs.Count > 0 &&
         EffectiveSelectedJobs.All(j => j.Job.Status is not (
             DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Removed or DownloadStatus.Cancelled)));

    public bool CanRemoveSelected =>
        (_selectedAutoSession is not null) ||
        (_selectedAutoItem is not null) ||
        (EffectiveSelectedJobs.Count > 0 &&
         EffectiveSelectedJobs.All(j => j.Job.Status is DownloadStatus.Completed or DownloadStatus.Failed));

    public bool CanRenameSelected =>
        EffectiveSelectedJobs.Count == 1 &&
        EffectiveSelectedJobs[0].Job.Status is not (
            DownloadStatus.Cancelled or DownloadStatus.Removed);

    public bool CanPlaySelected =>
        EffectiveSelectedJobs.Count == 1 &&
        EffectiveSelectedJobs[0].Job.Status == DownloadStatus.Completed &&
        !string.IsNullOrWhiteSpace(EffectiveSelectedJobs[0].Job.TargetPath) &&
        File.Exists(EffectiveSelectedJobs[0].Job.TargetPath);

    public bool CanOpenSelectedPage =>
        EffectiveSelectedJobs.Count == 1 &&
        EffectiveSelectedJobs[0].Job.PageUrl is not null;

    public bool CanOpenSelectedFolder =>
        EffectiveSelectedJobs.Count == 1 &&
        TryResolveOpenFolderPath(EffectiveSelectedJobs[0].Job, out _);

    public MainViewModel(
        IServiceProvider services,
        BrowserHostLocator hostLocator,
        IMediaDetectionPipeline pipeline,
        IDownloadEngine downloadEngine,
        IMediaAggregator aggregator,
        SettingsViewModel settingsViewModel,
        LocalizationService loc,
        LocalLibraryHost localLibrary)
    {
        _services = services;
        _hostLocator = hostLocator;
        _pipeline = pipeline;
        _downloadEngine = downloadEngine;
        _autoSessions = services.GetRequiredService<IAutoSessionRepository>();
        _autoScheduler = services.GetRequiredService<AutoDownloadScheduler>();
        _autoScheduler.StateChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(
            new Action(() => { _ = RefreshAutoQueueUiAsync(); }));
        _aggregator = aggregator;
        _settingsViewModel = settingsViewModel;
        _loc = loc;
        _localLibrary = localLibrary;
        _options = services.GetRequiredService<AppOptions>();
        _settingsStore = services.GetRequiredService<UserSettingsStore>();
        _queueGroupingMode = _options.Ui.QueueGroupingMode ?? (_options.Ui.QueueGrouped ? 1 : 0);
        if (_queueGroupingMode is < 0 or > 2) _queueGroupingMode = 0;
        _queueGrouped = _queueGroupingMode != 0;

        _pipeline.VideoDetected += OnVideoDetected;
        _pipeline.PageProbed += OnPageProbed;
        if (services.GetService<IProbeMethodStats>() is { } probeStats)
            probeStats.MethodStarted += OnProbeMethodStarted;
        _loc.LanguageChanged += OnLanguageChanged;
        _settingsViewModel.DownloadsMigrated += (_, _) => RefreshDownloadJobs();
        RebuildAddressPresets();
    }

    private void RebuildAddressPresets()
    {
        AddressPresets.Clear();
        AddressPresets.Add(new AddressPreset(_loc.T("preset.localVideos"), _localLibrary.GalleryUrl));
        AddressPresets.Add(new AddressPreset(_loc.T("preset.google"), "https://www.google.com/"));
        AddressPresets.Add(new AddressPreset(_loc.T("preset.youtube"), "https://www.youtube.com/"));
        AddressPresets.Add(new AddressPreset(_loc.T("preset.douyin"), "https://www.douyin.com/"));
        AddressPresets.Add(new AddressPreset(_loc.T("preset.tiktok"), "https://www.tiktok.com/"));
        AddressPresets.Add(new AddressPreset(_loc.T("preset.bilibili"), "https://www.bilibili.com/"));

        foreach (var bookmark in _options.Ui.AddressBookmarks)
        {
            if (string.IsNullOrWhiteSpace(bookmark.Name) || string.IsNullOrWhiteSpace(bookmark.Url))
                continue;
            AddressPresets.Add(new AddressPreset(bookmark.Name.Trim(), bookmark.Url.Trim()));
        }
    }

    [RelayCommand]
    private void FavoriteAddress()
    {
        var raw = !string.IsNullOrWhiteSpace(SelectedTab?.Address)
            ? SelectedTab!.Address
            : AddressBar;
        if (!TryNormalizeAddress(raw, out var url, out var error))
        {
            SetStatus(error);
            return;
        }

        var defaultName = SuggestFavoriteName(url, SelectedTab?.Title);
        var dialog = new TextInputDialog(
            _loc.T("favorite.title"),
            _loc.T("favorite.prompt"),
            defaultName)
        {
            Owner = Application.Current?.MainWindow
        };
        if (dialog.ShowDialog() != true)
            return;

        var name = dialog.ResultText?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            SetStatusKey("favorite.nameRequired");
            return;
        }

        if (name.Length > 64)
            name = name[..64];

        _options.Ui.AddressBookmarks ??= new List<SavedAddressPreset>();
        var existing = _options.Ui.AddressBookmarks.FirstOrDefault(
            x => string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase));
        var updated = existing is not null;
        if (existing is not null)
            existing.Name = name;
        else
            _options.Ui.AddressBookmarks.Add(new SavedAddressPreset { Name = name, Url = url });

        try { _settingsStore.Save(_options); }
        catch { /* settings write is best-effort */ }

        RebuildAddressPresets();
        SetStatusKey(updated ? "status.favoriteUpdated" : "status.favoriteSaved", name);
    }

    private static string SuggestFavoriteName(string url, string? tabTitle)
    {
        var title = (tabTitle ?? string.Empty).Trim();
        if (title.Length > 0 &&
            !string.Equals(title, "新标签页", StringComparison.Ordinal) &&
            !string.Equals(title, "New Tab", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(title, "New tab", StringComparison.OrdinalIgnoreCase))
        {
            return title.Length <= 64 ? title : title[..64];
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
            return uri.Host;

        return url.Length <= 64 ? url : url[..64];
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_statusKey is not null)
            StatusMessage = _statusArgs is { Length: > 0 }
                ? _loc.Format(_statusKey, _statusArgs)
                : _loc.T(_statusKey);

        RebuildAddressPresets();
        foreach (var tab in Tabs)
        {
            if (string.IsNullOrWhiteSpace(tab.Title) ||
                tab.Title is "新标签页" or "New Tab" or "New tab")
                tab.Title = _loc.T("tab.new");
        }

        foreach (var job in DownloadJobs)
            job.Refresh();
        foreach (var video in DetectedVideos)
            video.RefreshLocalizedModes();
        OnPropertyChanged(nameof(L));
        OnPropertyChanged(nameof(QueueGroupToggleLabel));
        OnPropertyChanged(nameof(AutoModeButtonLabel));
    }

    public async Task InitializeAsync()
    {
        await _autoScheduler.InitializeAsync();
        RefreshDownloadJobs();
        ClearStatus();
        await AddTabAsync(DefaultHomeUrl, select: true);
    }

    private string DefaultHomeUrl =>
        !string.IsNullOrWhiteSpace(_localLibrary.GalleryUrl)
            ? _localLibrary.GalleryUrl
            : $"http://127.0.0.1:{LocalLibraryHost.PreferredPort}/?group=time";

    private int _importBusy;

    [RelayCommand]
    private async Task GoLocalLibraryAsync()
    {
        StopAutoMode(announce: false, resumeDetection: false);
        AddressBar = DefaultHomeUrl;
        await NavigateAsync();
    }

    private async Task BeginLocalFolderImportAsync(BrowserTabViewModel tab)
    {
        if (Interlocked.CompareExchange(ref _importBusy, 1, 0) != 0)
        {
            SetStatusKey("status.importBusy");
            return;
        }

        try
        {
            var owner = Application.Current?.MainWindow;
            var folderDialog = new OpenFolderDialog
            {
                Title = _loc.T("import.pickFolderTitle")
            };
            if (folderDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(folderDialog.FolderName))
                return;

            var nameDialog = new TextInputDialog(
                _loc.T("import.nameTitle"),
                _loc.T("import.namePrompt"),
                _loc.T("import.defaultFolderName"))
            {
                Owner = owner
            };
            if (nameDialog.ShowDialog() != true)
                return;

            var folderName = string.IsNullOrWhiteSpace(nameDialog.ResultText)
                ? _loc.T("import.defaultFolderName")
                : nameDialog.ResultText.Trim();

            var importer = _services.GetRequiredService<LocalFolderImportService>();
            SetStatusKey("status.importRunning", 0, "?");

            var progress = new Progress<LocalFolderImportProgress>(p =>
            {
                var done = Math.Min(p.Completed + 1, Math.Max(p.Total, 1));
                SetStatusKey("status.importRunning", done, Math.Max(p.Total, 1));
            });

            var result = await importer
                .ImportAsync(folderDialog.FolderName, folderName, progress)
                .ConfigureAwait(true);

            RefreshDownloadJobs();
            try
            {
                await tab.Host.NotifyLocalLibraryImportDoneAsync().ConfigureAwait(true);
            }
            catch
            {
                // Gallery refresh is best-effort.
            }

            SetStatusKey("status.importDone", result.Imported, result.Failed, result.FolderName);
            MessageBox.Show(
                _loc.Format("import.successBody", result.Imported, result.Skipped, result.Failed, result.FolderName),
                _loc.T("import.successTitle"),
                MessageBoxButton.OK,
                result.Imported > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            SetStatusKey("status.importFailed", ex.Message);
            MessageBox.Show(
                _loc.Format("status.importFailed", ex.Message),
                _loc.T("import.successTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _importBusy, 0);
        }
    }

    public async Task AttachWebViewAsync(BrowserTabViewModel tab, WebView2 webView)
    {
        if (tab.IsInitialized && ReferenceEquals(tab.WebView, webView))
            return;

        tab.WebView = webView;
        if (!tab.IsInitialized)
        {
            await tab.Host.InitializeAsync(webView);
            tab.Host.PageIdentityChanged += (_, e) => OnTabPageIdentityChanged(tab, e);
            tab.Host.NavigationStarted += (_, url) =>
            {
                // Document navigation → same singleton full re-probe entry as manual Probe.
                if (ReferenceEquals(SelectedTab, tab) && Uri.TryCreate(url, UriKind.Absolute, out var page))
                {
                    // A navigation outside this auto session's own feed advance is a manual takeover.
                    if (IsAutoMode && _autoTab is not null &&
                        (!ReferenceEquals(tab, _autoTab) || DateTimeOffset.UtcNow > _autoExpectedNavigationUntil))
                        StopAutoMode(announce: true, resumeDetection: false);
                    HangProbe.Mark("vm.navStarted", $"{page.Host}{page.AbsolutePath}");
                    if (IsAppFullscreen && !LocalLibraryHost.IsLocalPlayerUrl(page))
                        SetAppFullscreen(false);
                    RestartDetectionForPageChange(page, null, mediaSessionKey: null, allowSuspended: true);
                }
            };
            tab.Host.LocalPlayerFullscreenRequested += (_, active) =>
            {
                if (!ReferenceEquals(SelectedTab, tab))
                    return;
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null || dispatcher.CheckAccess())
                    SetAppFullscreen(active);
                else
                    dispatcher.Invoke(() => SetAppFullscreen(active));
            };
            tab.Host.LocalFolderImportRequested += (_, _) =>
            {
                if (!ReferenceEquals(SelectedTab, tab))
                    return;
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null || dispatcher.CheckAccess())
                    _ = BeginLocalFolderImportAsync(tab);
                else
                    dispatcher.Invoke(() => _ = BeginLocalFolderImportAsync(tab));
            };
            tab.Host.TabAudibleChanged += (_, playing) =>
            {
                var dispatcher = Application.Current?.Dispatcher;
                void Apply()
                {
                    tab.IsAudiblePlaying = playing;
                    RecalculateTabWidths();
                }
                if (dispatcher is null || dispatcher.CheckAccess())
                    Apply();
                else
                    dispatcher.Invoke(Apply);
            };
            tab.Host.NavigationStarted += (_, _) =>
            {
                var dispatcher = Application.Current?.Dispatcher;
                void Clear()
                {
                    tab.IsAudiblePlaying = false;
                    RecalculateTabWidths();
                }
                if (dispatcher is null || dispatcher.CheckAccess())
                    Clear();
                else
                    dispatcher.Invoke(Clear);
            };
            tab.TitleChanged += (_, _) => RecalculateTabWidths();
            tab.Host.MediaSessionChanging += (_, _) =>
            {
                if (!ReferenceEquals(SelectedTab, tab)) return;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var page = tab.Host.CurrentPageUrl;
                    if (page is not null && SiteHosts.MatchesAny(page, "youtube.com", "youtube-nocookie.com", "youtu.be"))
                    {
                        var current = ExtractStableContentKeyFromIdentity(_currentPageIdentity);
                        var incoming = ExtractStableContentKey(null, page);
                        var previous = ExtractStableContentKey(tab.Host.CurrentMediaSessionKey, null);
                        if (_detectionRunning && incoming is not null && incoming == current && previous != incoming)
                            return;
                    }
                    SuspendDetectedResults(tab);
                });
            };
            tab.Host.MediaSessionChanged += (_, e) =>
            {
                if (!ReferenceEquals(SelectedTab, tab))
                    return;
                _ = Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(SelectedTab, tab) ||
                        !IsSameDocumentPage(tab.Host.CurrentPageUrl, e.PageUrl))
                        return;
                    HangProbe.Mark("vm.mediaSession", $"{e.PageUrl.Host}{e.PageUrl.AbsolutePath} key={e.MediaSessionKey}");
                    lock (_pageSync)
                    {
                        // Duplicate identity for the session already under probe — skip only.
                        // YouTube playlist AbsoluteUri can change (list/index) without a new watch id.
                        if (!e.ForceRestart &&
                            _detectionRunning &&
                            (string.Equals(_currentPageIdentity, BuildPageIdentity(e.PageUrl, e.MediaSessionKey), StringComparison.Ordinal) ||
                             IsSameYouTubeIdentity(_currentPageIdentity, e.PageUrl, e.MediaSessionKey)))
                            return;
                    }
                    // Content switch → same singleton full re-probe as manual Probe.
                    RestartDetectionForPageChange(
                        e.PageUrl,
                        e.PageTitle,
                        mediaSessionKey: e.MediaSessionKey,
                        forceRestart: e.ForceRestart);
                }, System.Windows.Threading.DispatcherPriority.Send);
            };
            tab.Host.OpenInNewTabRequested += (_, url) =>
            {
                _ = Application.Current.Dispatcher.InvokeAsync(async () => await AddTabAsync(url, select: true));
            };
            tab.Host.NavigationStateChanged += (_, _) =>
            {
                if (!ReferenceEquals(SelectedTab, tab))
                    return;
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null || dispatcher.CheckAccess())
                    UpdateNavigationState(tab);
                else
                    dispatcher.InvokeAsync(() => UpdateNavigationState(tab));
            };
            tab.IsInitialized = true;
        }

        tab.Host.CaptureEnabled = ReferenceEquals(SelectedTab, tab);
        if (ReferenceEquals(SelectedTab, tab))
        {
            _hostLocator.Active = tab.Host;
            UpdateNavigationState(tab);
        }

        var target = tab.PendingNavigationUrl ?? tab.Address;
        tab.PendingNavigationUrl = null;
        if (!string.IsNullOrWhiteSpace(target))
            await tab.Host.NavigateAsync(target);
    }

    partial void OnSelectedTabChanged(BrowserTabViewModel? oldValue, BrowserTabViewModel? newValue)
    {
        // Auto is bound to the tab it started on; switching tabs is a manual takeover.
        if (IsAutoMode && !ReferenceEquals(newValue, _autoTab))
            StopAutoMode(announce: true, resumeDetection: false);

        ClearDetectedVideos();
        if (oldValue is not null)
            oldValue.Host.CaptureEnabled = false;

        if (newValue is null)
        {
            CanGoBack = false;
            CanGoForward = false;
            RefreshSidePanelVisibility(null);
            if (IsAppFullscreen)
                SetAppFullscreen(false);
            return;
        }

        newValue.Host.CaptureEnabled = true;
        _hostLocator.Active = newValue.Host;
        AddressBar = newValue.Address;
        UpdateNavigationState(newValue);
        var page = newValue.Host.CurrentPageUrl
                   ?? (Uri.TryCreate(newValue.Address, UriKind.Absolute, out var uri) ? uri : null);
        RefreshSidePanelVisibility(page);
        if (page is not null)
        {
            HangProbe.Mark("vm.tabChanged", $"{page.Host}{page.AbsolutePath}");
            RestartDetectionForPageChange(page, newValue.Title, mediaSessionKey: newValue.Host.CurrentMediaSessionKey);
        }
        else
            ClearDetectedVideos();
    }

    private void UpdateNavigationState(BrowserTabViewModel? tab)
    {
        CanGoBack = tab?.Host.CanGoBack == true;
        CanGoForward = tab?.Host.CanGoForward == true;
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
        var page = tab?.Host.CurrentPageUrl
                   ?? (Uri.TryCreate(tab?.Address ?? AddressBar, UriKind.Absolute, out var uri) ? uri : null);
        RefreshSidePanelVisibility(page);
    }

    private void RefreshSidePanelVisibility(Uri? pageUrl)
    {
        CanProbeCurrentPage = pageUrl is not null && !LocalLibraryHost.IsLocalLibraryHost(pageUrl);
    }

    private bool CanExecuteGoBack => CanGoBack;
    private bool CanExecuteGoForward => CanGoForward;

    [RelayCommand(CanExecute = nameof(CanExecuteGoBack))]
    private void GoBack()
    {
        SelectedTab?.Host.GoBack();
        // HistoryChanged/NavigationCompleted will refresh CanGo*; do not read immediately.
    }

    [RelayCommand(CanExecute = nameof(CanExecuteGoForward))]
    private void GoForward()
    {
        SelectedTab?.Host.GoForward();
    }

    [RelayCommand]
    private void ToggleLanguage() => _loc.ToggleLanguage();

    partial void OnSelectedDownloadJobChanged(DownloadJobViewModel? value) => NotifyQueueCommands();

    public void SetSelectedDownloadJobs(IEnumerable<DownloadJobViewModel> jobs, DownloadJobViewModel? primary)
    {
        _selectedDownloadJobs.Clear();
        _selectedDownloadJobs.AddRange(jobs);
        _selectedDownloadJobIds.Clear();
        _selectedDownloadJobIds.AddRange(_selectedDownloadJobs.Select(j => j.Job.Id));

        // Always refresh Can* — multi-select can change while primary stays the same.
        if (!ReferenceEquals(SelectedDownloadJob, primary))
            SelectedDownloadJob = primary;
        NotifyQueueCommands();
    }

    public void ClearQueueSelection()
    {
        _selectedDownloadJobs.Clear();
        _selectedDownloadJobIds.Clear();
        _selectedAutoSession = null;
        _selectedAutoItem = null;
        if (SelectedDownloadJob is not null)
            SelectedDownloadJob = null;
        else
            NotifyQueueCommands();
    }

    public void SelectOnlyQueueJob(DownloadJobViewModel job)
    {
        SetSelectedDownloadJobs([job], job);
        RestoreQueueSelection?.Invoke([job.Job.Id]);
    }

    public void ActivateQueueJob(DownloadJobViewModel job)
    {
        SelectOnlyQueueJob(job);
        if (CanPlaySelected)
            _ = PlaySelectedDownloadAsync();
    }

    public void ToggleQueueGroup(string domain)
    {
        if (!_collapsedQueueGroups.Add(domain))
            _collapsedQueueGroups.Remove(domain);
        RebuildQueueRows();
        if (_selectedDownloadJobIds.Count > 0)
            RestoreQueueSelection?.Invoke(_selectedDownloadJobIds);
    }

    [RelayCommand]
    private async Task AddTabAsync()
    {
        await AddTabAsync(DefaultHomeUrl, select: true);
    }

    public async Task AddTabAsync(string url, bool select)
    {
        var host = _services.GetRequiredService<WebView2Host>();
        var tab = new BrowserTabViewModel(host)
        {
            Address = url,
            Title = _loc.T("tab.new")
        };
        Tabs.Add(tab);
        RecalculateTabWidths();
        if (select)
            SelectedTab = tab;
        await Task.CompletedTask;
    }

    /// <summary>Browser pane width used to size the tab strip (set from MainWindow).</summary>
    public double BrowserPaneWidth { get; private set; } = 800;

    public void NotifyBrowserPaneWidth(double width)
    {
        if (width <= 0)
            return;
        if (Math.Abs(BrowserPaneWidth - width) < 0.5)
            return;
        BrowserPaneWidth = width;
        RecalculateTabWidths();
    }

    public void RecalculateTabWidths()
    {
        const double maxTab = 200d;
        // ContentPresenter 10+6, close 16+margin 4, optional speaker 14+margin 6, sep 1 (non-first).
        const double contentMargin = 16d;
        const double closeChrome = 20d;
        const double speakerChrome = 20d;
        const double sep = 1d;
        const double minSqueeze = 48d;

        var count = Tabs.Count;
        if (count == 0)
            return;

        if (count == 1)
        {
            Tabs[0].FixedWidth = null;
            return;
        }

        var budget = Math.Max(minSqueeze * count, BrowserPaneWidth * 0.9);
        var desired = new double[count];
        var sum = 0d;
        for (var i = 0; i < count; i++)
        {
            var tab = Tabs[i];
            var chrome = contentMargin + closeChrome + (i == 0 ? 0d : sep);
            if (tab.IsAudiblePlaying)
                chrome += speakerChrome;
            desired[i] = Math.Min(maxTab, MeasureTabTitleWidth(tab.Title) + chrome);
            sum += desired[i];
        }

        // Under budget: keep content-sized tabs (do not stretch to fill 90%).
        if (sum <= budget)
        {
            foreach (var tab in Tabs)
                tab.FixedWidth = null;
            return;
        }

        // Over budget: shrink proportionally from content widths; still ≤200 each.
        var scale = budget / sum;
        for (var i = 0; i < count; i++)
            Tabs[i].FixedWidth = Math.Max(minSqueeze, Math.Min(maxTab, desired[i] * scale));
    }

    private static double MeasureTabTitleWidth(string? title)
    {
        var text = string.IsNullOrEmpty(title) ? " " : title;
        var pixelsPerDip = 1.0;
        try
        {
            if (Application.Current?.MainWindow is { } window)
                pixelsPerDip = VisualTreeHelper.GetDpi(window).PixelsPerDip;
        }
        catch
        {
            // Headless / design-time: fall back to 1.0.
        }

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(
                SystemFonts.MessageFontFamily,
                FontStyles.Normal,
                FontWeights.Normal,
                FontStretches.Normal),
            SystemFonts.MessageFontSize,
            Brushes.Black,
            pixelsPerDip);
        return formatted.Width;
    }

    [RelayCommand]
    private async Task CloseTabAsync(BrowserTabViewModel? tab)
    {
        tab ??= SelectedTab;
        if (tab is null)
            return;

        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        RecalculateTabWidths();
        tab.Host.CaptureEnabled = false;
        try
        {
            await tab.Host.DisposeAsync();
        }
        catch
        {
            // ignore
        }

        if (Tabs.Count == 0)
        {
            await AddTabAsync(DefaultHomeUrl, select: true);
            return;
        }

        SelectedTab = Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
    }

    [RelayCommand]
    private async Task NavigateAsync()
    {
        try
        {
            if (!TryNormalizeAddress(AddressBar, out var url, out var error))
            {
                SetStatus(error);
                return;
            }

            var pageUrl = new Uri(url);
            AddressBar = url;

            if (SelectedTab is null)
                await AddTabAsync(url, select: true);

            var tab = SelectedTab;
            if (tab is null)
            {
                SetStatusKey("status.noTab");
                return;
            }

            tab.Address = url;
            ClearStatus();

            if (LocalLibraryHost.IsLocalLibraryHost(pageUrl))
                EnterLocalLibraryMode(pageUrl);
            else
                RefreshSidePanelVisibility(pageUrl);

            if (tab.IsInitialized)
                await tab.Host.NavigateAsync(url);
            else
            {
                tab.PendingNavigationUrl = url;
                if (!LocalLibraryHost.IsLocalLibraryHost(pageUrl))
                    RestartDetectionForPageChange(pageUrl, null, mediaSessionKey: null);
            }
        }
        catch (Exception ex)
        {
            SetStatusKey("status.navFailed", ex.Message);
        }
    }

    internal bool TryNormalizeAddress(string? raw, out string url, out string error)
    {
        url = string.Empty;
        error = string.Empty;
        var text = (raw ?? string.Empty).Trim().Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(text))
        {
            error = _loc.T("status.emptyUrl");
            return false;
        }

        // Collapse whitespace accidentally pasted into the bar.
        text = string.Join("", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (text.StartsWith("//", StringComparison.Ordinal))
            text = "https:" + text;

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            if (text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("127.", StringComparison.Ordinal) ||
                text.StartsWith("[", StringComparison.Ordinal))
                text = "http://" + text;
            else
                text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            error = _loc.T("status.invalidUrl");
            return false;
        }

        // Any 127.0.0.1 / localhost navigation targets the local video library.
        if (LocalLibraryHost.IsLocalLibraryHost(uri))
        {
            if (!_localLibrary.IsRunning || _localLibrary.BaseUri is null)
            {
                error = _loc.T("status.localLibraryUnavailable");
                return false;
            }

            var path = uri.AbsolutePath;
            url = path.StartsWith("/play/", StringComparison.OrdinalIgnoreCase)
                ? new Uri(_localLibrary.BaseUri, path.TrimStart('/')).AbsoluteUri
                : _localLibrary.GalleryUrl;
            return true;
        }

        url = uri.AbsoluteUri;
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanProbeCurrentPage))]
    private void Refresh()
    {
        StopAutoMode(announce: false, resumeDetection: false);
        ProbeCurrentPage();
    }

    private void ProbeCurrentPage()
    {
        var tab = SelectedTab;
        var pageUrl = tab?.Host.CurrentPageUrl
                      ?? (Uri.TryCreate(AddressBar, UriKind.Absolute, out var uri) ? uri : null);
        if (pageUrl is null)
        {
            SetStatusKey("status.probeNoPage");
            return;
        }
        if (LocalLibraryHost.IsLocalLibraryHost(pageUrl))
        {
            SetStatusKey("status.localLibraryNoProbe");
            return;
        }
        _feedDisplayState.BeginSession();
        ClearStatus();
        SetStatusKey("status.probeWaiting");
        RestartDetectionForPageChange(pageUrl, tab?.Title, tab?.Host.CurrentMediaSessionKey, forceRestart: true);
    }

    /// <summary>
    /// Unified singleton detection entry for every site: wipe UI, destroy the routed detector
    /// session, BeginSession once, fully re-probe. Manual Probe and automatic navigation /
    /// SPA / media-session switches all use this path.
    /// </summary>
    private void RestartDetectionForPageChange(
        Uri pageUrl,
        string? pageTitle,
        string? mediaSessionKey,
        bool forceRestart = false,
        bool allowSuspended = false)
    {
        HangProbe.Mark("vm.restart", $"{pageUrl.Host}{pageUrl.AbsolutePath} det={_detectionRunning}");

        if (LocalLibraryHost.IsLocalLibraryHost(pageUrl))
        {
            EnterLocalLibraryMode(pageUrl);
            return;
        }

        // A real navigation back to the same page/content (retry after a failed feed switch) must
        // revive detection; only late events from the retired session need the suspension gate.
        if (!forceRestart && !allowSuspended &&
            !_feedDisplayState.CanBeginSession(BuildPageIdentity(pageUrl).Split('\n')[0],
                ExtractStableContentKey(mediaSessionKey, pageUrl), mediaSessionKey))
            return;

        lock (_pageSync)
        {
            if (!forceRestart && !_feedDisplayState.IsSuspended &&
                (_detectionRunning || _pipeline.IsCompleted && !allowSuspended))
            {
                var incomingStable = ExtractStableContentKey(mediaSessionKey, pageUrl);
                var currentStable = ExtractStableContentKeyFromIdentity(_currentPageIdentity);
                if (incomingStable is not null && currentStable is not null &&
                    string.Equals(incomingStable, currentStable, StringComparison.Ordinal))
                {
                    HangProbe.Mark("vm.restart.blocked", $"stable={incomingStable}");
                    return;
                }
            }
        }

        RefreshSidePanelVisibility(pageUrl);

        var enriched = EnrichPageUrlWithContentId(pageUrl, mediaSessionKey);
        StartPageDetectionSession(enriched, pageTitle, mediaSessionKey);
    }

    private void EnterLocalLibraryMode(Uri pageUrl)
    {
        StopAutoMode(announce: false, resumeDetection: false);
        lock (_pageSync)
        {
            _probeCts.Cancel();
            _probePublications.Invalidate();
            _detectionRunning = false;
            _currentPageIdentity = BuildPageIdentity(pageUrl);
            _lastNavigatedPageUrl = _currentPageIdentity;
        }

        _pendingProbeVideos.Clear();
        DetectedVideos.Clear();
        _videoMap.Clear();
        SelectedDetectedVideo = null;
        RefreshSidePanelVisibility(pageUrl);
    }

    /// <summary>
    /// Feed roots often lack modal_id/video id in the address bar. When media-session already
    /// knows the content id, stamp it onto the page URI so exclusive BeginSession binds correctly.
    /// Douyin/TikTok digit stamping is unchanged; YouTube/Bilibili enrichment is additive only.
    /// </summary>
    private static Uri EnrichPageUrlWithContentId(Uri pageUrl, string? mediaSessionKey)
    {
        if (ExtractStableContentKey(null, pageUrl) is not null)
            return pageUrl;
        var stable = ExtractStableContentKey(mediaSessionKey, null);
        if (stable is null)
            return pageUrl;

        var host = pageUrl.Host;

        // Existing Douyin / TikTok digit enrichment — do not alter.
        var digits = System.Text.RegularExpressions.Regex.Match(stable, @"^content:(\d{10,})$");
        if (digits.Success)
        {
            var id = digits.Groups[1].Value;
            if (SiteHosts.MatchesAny(host, "douyin.com", "iesdouyin.com"))
            {
                var builder = new UriBuilder(pageUrl);
                var query = builder.Query.TrimStart('?');
                var parts = string.IsNullOrWhiteSpace(query)
                    ? new List<string>()
                    : query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                        .Where(p => !p.StartsWith("modal_id=", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                parts.Add("modal_id=" + id);
                builder.Query = string.Join('&', parts);
                return builder.Uri;
            }

            if (SiteHosts.Matches(host, "tiktok.com") &&
                !pageUrl.AbsolutePath.Contains("/video/", StringComparison.OrdinalIgnoreCase) &&
                !pageUrl.AbsolutePath.Contains("/embed/", StringComparison.OrdinalIgnoreCase))
            {
                // Keep the live feed URL. Bare /video/{id} 404s; embed would leave the feed.
                // Content id stays on mediaSessionKey / BuildPageIdentity stable key.
                var builder = new UriBuilder(pageUrl);
                var query = builder.Query.TrimStart('?');
                var parts = string.IsNullOrWhiteSpace(query)
                    ? new List<string>()
                    : query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                        .Where(p => !p.StartsWith("item_id=", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                parts.Add("item_id=" + id);
                builder.Query = string.Join('&', parts);
                return builder.Uri;
            }
        }

        if (stable.StartsWith("content:youtube:", StringComparison.OrdinalIgnoreCase) &&
            SiteHosts.MatchesAny(host, "youtube.com", "youtube-nocookie.com", "youtu.be"))
        {
            var videoId = stable["content:youtube:".Length..];
            if (videoId.Length > 0)
                return new Uri($"https://www.youtube.com/watch?v={videoId}");
        }

        if (stable.StartsWith("content:bilibili:", StringComparison.OrdinalIgnoreCase) &&
            SiteHosts.MatchesAny(host, "bilibili.com", "b23.tv"))
        {
            var bvid = stable["content:bilibili:".Length..];
            if (bvid.Length > 0)
                return new Uri($"https://www.bilibili.com/video/{bvid}");
        }

        return pageUrl;
    }

    private void ClearDetectedVideos()
    {
        _feedDisplayState.BeginSession();
        lock (_pageSync)
        {
            _pageGeneration++;
            _probeCts.Cancel();
            _probePublications.Invalidate();
            _detectionRunning = false;
        }
        _pendingProbeVideos.Clear();
        DetectedVideos.Clear();
        _videoMap.Clear();
        SelectedDetectedVideo = null;
        ResetDetectionPipeline();
    }

    private void ResetDetectionPipeline()
    {
        _aggregator.Clear();
        _pipeline.Clear();
    }

    private void HardResetDetectionPipeline()
    {
        _aggregator.Clear();
        if (_pipeline is RoutedMediaDetectionPipeline routed)
            routed.HardClear();
        else
            _pipeline.Clear();
    }

    /// <summary>
    /// One stable page session per document or confirmed player-content switch. Network activity
    /// may enrich the current result, but never schedules additional timed re-probes.
    /// </summary>
    private void StartPageDetectionSession(Uri pageUrl, string? pageTitle, string? mediaSessionKey)
    {
        long generation;
        CancellationToken token;
        lock (_pageSync)
        {
            _currentPageIdentity = BuildPageIdentity(pageUrl, mediaSessionKey);
            _lastNavigatedPageUrl = BuildPageIdentity(pageUrl);
            _forceReplaceResults = true;
            _detectionRunning = true;
            generation = ++_pageGeneration;
            _probeCts.Cancel();
            _probePublications.Invalidate();
            _probeCts.Dispose();
            _probeCts = new CancellationTokenSource();
            _probePublications.Begin();
            token = _probeCts.Token;
        }

        _feedDisplayState.BeginSession();
        _pendingProbeVideos.Clear();
        DetectedVideos.Clear();
        _videoMap.Clear();
        SelectedDetectedVideo = null;
        _aggregator.Clear();
        if (_pipeline is RoutedMediaDetectionPipeline routed)
            routed.Reenter(pageUrl);
        else
            HardResetDetectionPipeline();

        SelectedTab?.Host.ResetDetectionSession();
        _ = RunStableDetectionSessionAsync(pageUrl, pageTitle, token, generation);
    }

    private async Task RunStableDetectionSessionAsync(
        Uri pageUrl,
        string? pageTitle,
        CancellationToken token,
        long generation)
    {
        var completion = _probePublications.Completion;
        var sessionId = _pipeline.SessionId;
        try
        {
            HangProbe.Mark("vm.session.begin", pageUrl.AbsoluteUri);
            _probeMethodVisible = false;
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (generation == _pageGeneration && !token.IsCancellationRequested)
                    SetStatusKey("status.probeWaiting");
            });

            var siteId = new VideoDownloader.Core.Sites.SiteDetectionRouter().Resolve(pageUrl) switch
            {
                SiteKind.YouTube => SiteIds.YouTube,
                SiteKind.Bilibili => SiteIds.Bilibili,
                SiteKind.Douyin => SiteIds.Douyin,
                SiteKind.TikTok => SiteIds.TikTok,
                _ => SiteIds.Generic
            };
            var stats = _services.GetService<IProbeMethodStats>();
            var scope = ProbeMethodRanking.Scope(siteId, pageUrl);
            var preferExternal = stats?.PreferYtdlpFirst(scope) ?? siteId != SiteIds.Douyin;
            var isAlbum = siteId == SiteIds.Douyin && pageUrl.AbsolutePath.Contains("/note/", StringComparison.OrdinalIgnoreCase);
            bool Current() => generation == _pageGeneration && !token.IsCancellationRequested;
            bool Ready() => Current() && _pendingProbeVideos.Values.Any(DetectionReadiness.IsUsable);
            bool Stop() => !Current() || _pendingProbeVideos.Values.Any(v => v.IsLive);

            // Observe first without external processes; then race one extraction with continuing
            // browser observations. No second full round and no restarting the same singleton.
            await DetectionProbeScheduler.RunAsync(
                observe: ct => RunPagePassAsync(pageUrl, pageTitle, ct, generation, runExternal: false),
                extract: siteId == SiteIds.Douyin ? null :
                    ct => RunPagePassAsync(pageUrl, pageTitle, ct, generation, runExternal: true),
                hasUsableResult: Ready,
                shouldStop: Stop,
                externalDelay: TimeSpan.FromMilliseconds(siteId == SiteIds.YouTube || preferExternal ? 0 : 700),
                budget: TimeSpan.FromSeconds(isAlbum ? 35 : siteId == SiteIds.YouTube ? 100 : 20),
                ct: token,
                trailingObservation: siteId == SiteIds.YouTube ? TimeSpan.Zero : null);
            if (!Current()) return;

            // Join final validation before the only public result. A timeout is not completion.
            await _pipeline.CompleteDiscoveryAsync(token);
            if (!Current()) return;
            if (siteId == SiteIds.Douyin && !Ready() &&
                await TryBoostDouyinDetailAsync(pageUrl, token)) return;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (generation != _pageGeneration || sessionId != _pipeline.SessionId || token.IsCancellationRequested)
                    return;
                var videos = PublishFinalProbe(sessionId);
                if (videos.Length > 0)
                {
                    SetStatusKey("status.probeDone", videos.Length);
                    completion.TrySetResult(BuildPublishedProbeResult(sessionId, videos));
                    return;
                }

                var pipeline = _pipeline as VideoDownloader.Infrastructure.Detection.UnifiedMediaPipeline;
                var hint = pipeline?.LastValidationError ?? pipeline?.LastExternalError;
                if (_pipeline is RoutedMediaDetectionPipeline routedHint &&
                    !string.IsNullOrWhiteSpace(routedHint.ActiveExclusiveFailureReason))
                    hint ??= routedHint.ActiveExclusiveFailureReason;
                if (string.IsNullOrWhiteSpace(hint))
                    SetStatusKey("status.probeEmpty");
                else
                    SetStatusKey("status.probeEmptyExt", hint);
                completion.TrySetResult(BuildPublishedProbeResult(sessionId, videos, hint));
            }, System.Windows.Threading.DispatcherPriority.Background);
            HangProbe.Mark("vm.session.end", $"videos={DetectedVideos.Count}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            HangProbe.Mark("vm.session.canceled");
        }
        catch (Exception ex)
        {
            HangProbe.Mark("vm.session.fail", ex.GetType().Name + " " + ex.Message);
            await Application.Current.Dispatcher.InvokeAsync(
                () =>
                {
                    if (generation != _pageGeneration || sessionId != _pipeline.SessionId) return;
                    SetStatusKey("status.probeFailed", ex.Message);
                    completion.TrySetResult(BuildPublishedProbeResult(sessionId, PublishFinalProbe(sessionId), ex.Message));
                },
                System.Windows.Threading.DispatcherPriority.Background);
        }
        finally
        {
            completion.TrySetCanceled(); // Retired/navigation-boost sessions cannot complete a later page.
            lock (_pageSync)
            {
                if (generation == _pageGeneration)
                    _detectionRunning = false;
            }
        }
    }

    /// <summary>
    /// Navigate feed → /video/{awemeId} once so discovery can collect durable progressive.
    /// Returns true when navigation was kicked off (caller must not report probeEmpty).
    /// </summary>
    private async Task<bool> TryBoostDouyinDetailAsync(Uri pageUrl, CancellationToken token)
    {
        var contentId = (_pipeline as RoutedMediaDetectionPipeline)?.ActiveExclusiveContentId
                        ?? TryExtractDouyinAwemeId(pageUrl);
        if (string.IsNullOrWhiteSpace(contentId) || !contentId.All(char.IsDigit))
            return false;

        if (string.Equals(_douyinDetailBoostId, contentId, StringComparison.Ordinal))
        {
            HangProbe.Mark("vm.douyin.detailBoost.skip", "already-boosted");
            return false;
        }

        var detailPath = "/video/" + contentId;
        if (pageUrl.AbsolutePath.Contains(detailPath, StringComparison.OrdinalIgnoreCase))
        {
            HangProbe.Mark("vm.douyin.detailBoost.skip", "already-on-detail");
            return false;
        }

        var host = SelectedTab?.Host;
        if (host is null)
            return false;

        _douyinDetailBoostId = contentId;
        var detail = new Uri("https://www.douyin.com/video/" + Uri.EscapeDataString(contentId));
        HangProbe.Mark("vm.douyin.detailBoost", contentId);
        await Application.Current.Dispatcher.InvokeAsync(() => SetProbeRunningStatus());
        try
        {
            await host.NavigateAsync(detail.AbsoluteUri, token);
        }
        catch (Exception ex)
        {
            HangProbe.Mark("vm.douyin.detailBoost.fail", ex.GetType().Name);
            _douyinDetailBoostId = null;
            return false;
        }

        // PageIdentityChanged starts a fresh detection session on the detail URL.
        return true;
    }

    private static string? TryExtractDouyinAwemeId(Uri pageUrl)
    {
        var path = pageUrl.AbsolutePath;
        var videoIdx = path.IndexOf("/video/", StringComparison.OrdinalIgnoreCase);
        if (videoIdx >= 0)
        {
            var start = videoIdx + "/video/".Length;
            var end = start;
            while (end < path.Length && char.IsDigit(path[end]))
                end++;
            if (end > start)
                return path[start..end];
        }

        foreach (var part in pageUrl.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = part[..eq];
            if (!key.Equals("modal_id", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("aweme_id", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("item_id", StringComparison.OrdinalIgnoreCase))
                continue;
            var value = Uri.UnescapeDataString(part[(eq + 1)..]);
            if (value.Length > 0 && value.All(char.IsDigit))
                return value;
        }

        return null;
    }

    private async Task RunPagePassAsync(
        Uri pageUrl,
        string? pageTitle,
        CancellationToken token,
        long generation,
        bool runExternal)
    {
        if (generation != _pageGeneration || token.IsCancellationRequested)
            return;

        await Application.Current.Dispatcher.InvokeAsync(
            async () =>
            {
                if (generation != _pageGeneration || token.IsCancellationRequested)
                    return;

                var tab = SelectedTab;
                // Refresh cookies/headers right before page methods (critical for yt-dlp / ffprobe).
                if (tab?.IsInitialized == true)
                {
                    try
                    {
                        HangProbe.Mark("vm.pagePass.refresh.begin");
                        // Force WebView cookie jar for yt-dlp even when CaptureCookies is off
                        // (YouTube/Bilibili bot checks). Does not change Douyin detector code.
                        if (runExternal)
                        {
                            if (SiteHosts.MatchesAny(pageUrl, "youtube.com", "youtube-nocookie.com", "youtu.be"))
                            {
                                await tab.Host.WaitForYouTubePlayerAsync(pageUrl, token);
                                // Ingest the matching player, including its live flag, before extraction.
                                await tab.Host.ProbeCurrentPageAsync(token);
                            }
                            await tab.Host.RefreshContextSnapshotAsync(token, forceCookies: true);
                        }
                        if (!runExternal || !SiteHosts.MatchesAny(pageUrl, "youtube.com", "youtube-nocookie.com", "youtu.be"))
                        {
                            HangProbe.Mark("vm.pagePass.probe1.begin");
                            await tab.Host.ProbeCurrentPageAsync(token);
                            HangProbe.Mark("vm.pagePass.probe1.end");
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        HangProbe.Mark("vm.pagePass.probe1.fail", ex.GetType().Name);
                        // Cookie/DOM probe failures must not abort the whole detection session.
                        SetStatusKey("status.contextWarn", ex.Message);
                        if (runExternal && SiteHosts.MatchesAny(pageUrl, "youtube.com", "youtube-nocookie.com", "youtu.be"))
                            return;
                    }
                }

                token.ThrowIfCancellationRequested();
                if (generation != _pageGeneration || !ReferenceEquals(tab, SelectedTab)) return;
                var context = tab?.Host.CaptureCurrentContext(pageUrl, pageUrl)
                              ?? _hostLocator.Active?.CaptureCurrentContext(pageUrl, pageUrl)
                              ?? RequestContext.CreateEmpty();

                // ProbeCurrentPageAsync already ingested DOM JSON with runExternal:false.
                // Run external (yt-dlp) exactly when requested for this pass.
                if (runExternal)
                {
                    HangProbe.Mark("vm.pagePass.pipelineExternal.begin");
                    await _pipeline.ProbePageAsync(pageUrl, pageTitle, null, context, token, runExternal: true);
                    HangProbe.Mark("vm.pagePass.pipelineExternal.end");
                }

                // Late playAddr / network video often arrives after yt-dlp returns audio-only.
                // One more DOM ingest before sealing discovery keeps app and verifier aligned.
                if (runExternal && tab?.IsInitialized == true)
                {
                    try
                    {
                        HangProbe.Mark("vm.pagePass.probe2.begin");
                        await tab.Host.ProbeCurrentPageAsync(token);
                        HangProbe.Mark("vm.pagePass.probe2.end");
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                    catch { /* enrichment only */ }
                }
            },
            System.Windows.Threading.DispatcherPriority.Background).Task.Unwrap();
        token.ThrowIfCancellationRequested();

    }

    [RelayCommand]
    private void OpenSettings()
    {
        var dialog = new SettingsWindow
        {
            DataContext = _settingsViewModel,
            Owner = Application.Current.MainWindow
        };
        dialog.ShowDialog();
    }

    [RelayCommand]
    private async Task ToggleAutoMode()
    {
        if (IsAutoMode)
        {
            await StopAutoModeAsync(announce: true);
            return;
        }

        var pageUrl = SelectedTab?.Host.CurrentPageUrl
                      ?? (Uri.TryCreate(AddressBar, UriKind.Absolute, out var uri) ? uri : null);
        if (pageUrl is null)
        {
            SetStatusKey("status.probeNoPage");
            return;
        }

        if (LocalLibraryHost.IsLocalLibraryHost(pageUrl))
        {
            SetStatusKey("status.autoLocalDenied");
            return;
        }

        var initialMinutes = _options.Ui.AutoModeIdleMinutes >= 0 ? _options.Ui.AutoModeIdleMinutes : 5;
        var initialCount = _options.Ui.AutoModeMaxVideos >= 0 ? _options.Ui.AutoModeMaxVideos : 0;
        var dlg = new AutoModeDelayWindow(_loc, initialMinutes, initialCount)
        {
            Owner = Application.Current.MainWindow
        };
        if (dlg.ShowDialog() != true)
            return;

        _autoIdleMinutes = dlg.AcceptedMinutes;
        _autoMaxVideos = dlg.AcceptedCount;
        _autoProcessedCount = 0;
        _options.Ui.AutoModeIdleMinutes = _autoIdleMinutes;
        _options.Ui.AutoModeMaxVideos = _autoMaxVideos;
        try { _settingsStore.Save(_options); }
        catch { /* ignore persist errors */ }

        await StartNewAutoSessionAsync(pageUrl);
    }

    private async Task StartNewAutoSessionAsync(Uri pageUrl)
    {
        await PauseRunningAutoSessionAsync(pauseJobs: true);
        RefreshDownloadJobs();

        var now = DateTimeOffset.Now;
        var session = new AutoSession
        {
            Id = Guid.NewGuid(),
            Name = await AllocateAutoSessionNameAsync(now),
            Status = AutoSessionStatus.Running,
            Site = ResolveAutoSiteId(pageUrl),
            MaxVideos = _autoMaxVideos,
            IdleMinutes = _autoIdleMinutes,
            EnqueuedCount = 0,
            TabHint = pageUrl.AbsoluteUri,
            StartedAt = now.ToUniversalTime(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await _autoSessions.SaveSessionAsync(session);
        _autoDbSessionId = session.Id;
        _collapsedAutoSessions.Remove(session.Id);

        var cts = new CancellationTokenSource();
        _autoCts?.Cancel();
        _autoCts?.Dispose();
        _autoCts = cts;
        _autoSessionEndUtc = _autoIdleMinutes > 0
            ? DateTime.UtcNow.AddMinutes(_autoIdleMinutes)
            : DateTime.MaxValue;
        _autoLastEnqueuedFingerprint = null;
        _autoFailedFingerprint = null;
        _autoItemStartedAt = DateTimeOffset.UtcNow;
        _autoTab = SelectedTab;
        _autoExpectedNavigationUntil = default;
        IsAutoMode = true;
        _autoScheduler.SetActiveSession(session.Id);
        SetStatusKey("status.autoStarted", FormatAutoStartedLimits());
        await RefreshAutoQueueUiAsync();
        _autoConsumedProbeSessionId = null;
        ProbeCurrentPage();
        _ = RunAutoModeLoopAsync(cts.Token);
    }

    private async Task<string> AllocateAutoSessionNameAsync(DateTimeOffset localNow)
    {
        var baseName = localNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var existing = await _autoSessions.GetSessionsAsync();
        var names = existing.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        if (!names.Contains(baseName))
            return baseName;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = baseName + "-" + i.ToString(CultureInfo.InvariantCulture);
            if (!names.Contains(candidate))
                return candidate;
        }
        return baseName + "-" + Guid.NewGuid().ToString("N")[..6];
    }

    private static string? ResolveAutoSiteId(Uri pageUrl)
    {
        if (SiteHosts.MatchesAny(pageUrl, "youtube.com", "youtube-nocookie.com", "youtu.be"))
            return SiteIds.YouTube;
        if (SiteHosts.MatchesAny(pageUrl, "douyin.com", "iesdouyin.com"))
            return SiteIds.Douyin;
        if (SiteHosts.MatchesAny(pageUrl, "tiktok.com"))
            return SiteIds.TikTok;
        if (SiteHosts.MatchesAny(pageUrl, "bilibili.com", "b23.tv"))
            return SiteIds.Bilibili;
        return null;
    }

    /// <summary>
    /// Auto start/resume issues exactly the same page-probe request as the manual Probe button.
    /// </summary>

    private string FormatAutoStartedLimits()
    {
        if (_autoIdleMinutes > 0 && _autoMaxVideos > 0)
            return _loc.Format("status.autoStartedLimits", _autoIdleMinutes, _autoMaxVideos);
        if (_autoIdleMinutes > 0)
            return _loc.Format("status.autoStartedTimeOnly", _autoIdleMinutes);
        if (_autoMaxVideos > 0)
            return _loc.Format("status.autoStartedCountOnly", _autoMaxVideos);
        return _loc.T("status.autoStartedUnlimited");
    }

    private bool IsAutoTimeExpired() =>
        _autoIdleMinutes > 0 && DateTime.UtcNow >= _autoSessionEndUtc;

    private bool IsAutoCountReached() =>
        _autoMaxVideos > 0 && _autoProcessedCount >= _autoMaxVideos;

    private bool IsAutoLimitReached() => IsAutoTimeExpired() || IsAutoCountReached();

    private void RefreshAutoRunningStatus()
    {
        if (_autoIdleMinutes > 0)
            SetStatusKey("status.autoRunning", RemainingSessionMinutes(_autoSessionEndUtc), _autoProcessedCount);
        else if (_autoMaxVideos > 0)
            SetStatusKey("status.autoRunningCount", _autoProcessedCount, _autoMaxVideos);
        else
            SetStatusKey("status.autoRunningUnlimited", _autoProcessedCount);
    }

    private void RefreshAutoWaitingSlotStatus()
    {
        var max = Math.Max(1, _downloadEngine.MaxDownloadSlots);
        var occupied = Math.Clamp(max - _downloadEngine.FreeDownloadSlots, 0, max);
        SetStatusKey("status.autoWaitingSlot", occupied, max);
    }

    public void StopAutoMode(bool announce, bool resumeDetection = true) =>
        _ = StopAutoModeAsync(announce, resumeDetection);

    public async Task StopAutoModeAsync(bool announce, bool resumeDetection = true)
    {
        var wasActive = IsAutoMode || _autoCts is not null || _autoDbSessionId is not null;
        if (!wasActive)
            return;

        try { _autoCts?.Cancel(); }
        catch { /* ignore */ }
        _autoCts?.Dispose();
        _autoCts = null;
        _autoScheduler.Stop();
        if (_autoDbSessionId is Guid sid)
            await PauseAutoSessionCoreAsync(sid, pauseJobs: true, markStopped: false);
        _autoDbSessionId = null;
        IsAutoMode = false;
        _autoTab = null;
        _autoExpectedNavigationUntil = default;
        _autoLastEnqueuedFingerprint = null;
        _autoFailedFingerprint = null;
        _autoProcessedCount = 0;
        if (announce)
            SetStatusKey("status.autoStopped");
        await RefreshAutoQueueUiAsync();

        // Ending an automatic consumer never changes the common probe lifecycle.
    }

    private async Task PauseRunningAutoSessionAsync(bool pauseJobs)
    {
        // Stop chaining first so an in-flight tick cannot start another task after we pause.
        _autoScheduler.Stop();
        foreach (var running in await _autoSessions.GetSessionsAsync())
        {
            if (running.Status != AutoSessionStatus.Running)
                continue;
            await PauseAutoSessionCoreAsync(running.Id, pauseJobs: false, markStopped: false);
        }

        if (!pauseJobs)
            return;

        await _autoScheduler.PauseAllListedDownloadsAsync();
        foreach (var running in await _autoSessions.GetSessionsAsync())
        {
            if (running.Status != AutoSessionStatus.Running)
                continue;
            running.Status = AutoSessionStatus.Paused;
            running.UpdatedAt = DateTimeOffset.UtcNow;
            await _autoSessions.SaveSessionAsync(running);
        }
    }

    private async Task PauseAutoSessionCoreAsync(Guid sessionId, bool pauseJobs, bool markStopped)
    {
        var session = await _autoSessions.GetSessionAsync(sessionId);
        if (session is null)
            return;
        if (session.Status == AutoSessionStatus.Running)
        {
            try { _autoCts?.Cancel(); }
            catch { /* ignore */ }
            if (_autoScheduler.ActiveSessionId == sessionId)
                _autoScheduler.Stop();
        }

        if (pauseJobs)
        {
            foreach (var jobId in await _autoSessions.GetActiveJobIdsAsync(sessionId))
            {
                try { await _downloadEngine.PauseAsync(jobId); }
                catch { /* ignore */ }
            }
        }

        session.Status = markStopped ? AutoSessionStatus.Stopped : AutoSessionStatus.Paused;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        if (markStopped)
            session.EndedAt = DateTimeOffset.UtcNow;
        await _autoSessions.SaveSessionAsync(session);
    }

    public async Task ToggleAutoSessionAsync(Guid sessionId)
    {
        if (!IsAutoMode)
        {
            var downloadSession = await _autoSessions.GetSessionAsync(sessionId);
            if (downloadSession is null) return;
            if (downloadSession.Status == AutoSessionStatus.Running)
            {
                await PauseAutoSessionCoreAsync(sessionId, pauseJobs: true, markStopped: false);
                SetStatusKey("status.autoSessionPaused", downloadSession.Name);
            }
            else
            {
                await _autoScheduler.ActivateDownloadSessionAsync(sessionId);
                SetStatusKey("status.autoSessionResumed", downloadSession.Name);
            }
            RefreshDownloadJobs();
            await RefreshAutoQueueUiAsync();
            return;
        }

        var session = await _autoSessions.GetSessionAsync(sessionId);
        if (session is null || session.Status == AutoSessionStatus.Completed)
            return;

        if (session.Status == AutoSessionStatus.Running)
        {
            await PauseAutoSessionCoreAsync(sessionId, pauseJobs: true, markStopped: false);
            try { _autoCts?.Cancel(); }
            catch { /* ignore */ }
            _autoCts?.Dispose();
            _autoCts = null;
            if (_autoDbSessionId == sessionId)
                _autoDbSessionId = null;
            SetStatusKey("status.autoSessionPaused", session.Name);
            RefreshDownloadJobs();
            await RefreshAutoQueueUiAsync();
            return;
        }

        // Start / resume this session; pause any other running session and its jobs.
        await PauseRunningAutoSessionAsync(pauseJobs: true);

        session = await _autoSessions.GetSessionAsync(sessionId);
        if (session is null)
            return;
        session.Status = AutoSessionStatus.Running;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        session.EndedAt = null;
        await _autoSessions.SaveSessionAsync(session);

        foreach (var jobId in await _autoSessions.GetActiveJobIdsAsync(sessionId))
        {
            try { await _downloadEngine.ResumeAsync(jobId); }
            catch { /* ignore */ }
        }

        _autoDbSessionId = sessionId;
        _autoIdleMinutes = session.IdleMinutes;
        _autoMaxVideos = session.MaxVideos;
        _autoProcessedCount = session.EnqueuedCount;
        _autoSessionEndUtc = _autoIdleMinutes > 0
            ? DateTime.UtcNow.AddMinutes(_autoIdleMinutes)
            : DateTime.MaxValue;
        _autoItemStartedAt = DateTimeOffset.UtcNow;
        _autoTab = SelectedTab;
        _collapsedAutoSessions.Remove(sessionId);

        var cts = new CancellationTokenSource();
        _autoCts?.Cancel();
        _autoCts?.Dispose();
        _autoCts = cts;
        _autoScheduler.SetActiveSession(sessionId);
        SetStatusKey("status.autoSessionResumed", session.Name);
        RefreshDownloadJobs();
        await RefreshAutoQueueUiAsync();
        _autoConsumedProbeSessionId = null;
        ProbeCurrentPage();
        _ = RunAutoModeLoopAsync(cts.Token);
    }

    public void ToggleAutoSessionExpanded(Guid sessionId)
    {
        if (!_collapsedAutoSessions.Add(sessionId))
            _collapsedAutoSessions.Remove(sessionId);
        _ = RefreshAutoQueueUiAsync();
    }

    private void SuspendDetectedResults(BrowserTabViewModel tab)
    {
        if (_feedDisplayState.IsSuspended) return;
        var page = tab.Host.CurrentPageUrl;
        _feedDisplayState.Suspend(page is null ? string.Empty : BuildPageIdentity(page).Split('\n')[0],
            ExtractStableContentKey(tab.Host.CurrentMediaSessionKey, page) ??
            ExtractStableContentKeyFromIdentity(_currentPageIdentity), tab.Host.CurrentMediaSessionKey);
        lock (_pageSync)
        {
            _pageGeneration++;
            _probeCts.Cancel();
            _probePublications.Invalidate();
            _detectionRunning = false;
        }
        _pendingProbeVideos.Clear();
        DetectedVideos.Clear();
        _videoMap.Clear();
        SelectedDetectedVideo = null;
        HardResetDetectionPipeline();
        tab.Host.ResetDetectionSession();
    }

    private static readonly TimeSpan ManualSwitchTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ManualSwitchPoll = TimeSpan.FromMilliseconds(200);

    public async Task SwitchToNextVideoAsync(CancellationToken ct = default)
    {
        // Manual next is a takeover; auto must not also advance this item.
        StopAutoMode(announce: false, resumeDetection: false);
        var tab = SelectedTab;
        if (tab?.Host is null || !tab.IsInitialized)
            return;
        if (tab.Host.CurrentPageUrl is { } page && LocalLibraryHost.IsLocalLibraryHost(page))
            return;

        var before = CaptureManualPlaybackSnapshot(tab);
        SetStatusKey("status.nextSwitching");
        try
        {
            // Keep the current cards until the page confirms the switch; a rejected key must
            // not leave the panel empty.
            await SendFeedNextCoreAsync(ct, target: tab);
            if (await WaitForManualContentSwitchAsync(before, tab, ct))
                return;
            if (!ReferenceEquals(SelectedTab, tab))
                return;

            // A page can drop the first synthetic key while focus moves; retry once.
            await SendFeedNextCoreAsync(ct, target: tab);
            if (await WaitForManualContentSwitchAsync(before, tab, ct))
                return;
            RecoverManualNextFailure(tab);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { RecoverManualNextFailure(tab); }
    }

    private void RecoverManualNextFailure(BrowserTabViewModel tab)
    {
        if (!ReferenceEquals(SelectedTab, tab))
            return;
        // An auto takeover may already have cleared the panel; never leave it empty.
        if (DetectedVideos.Count == 0)
            Refresh();
        SetStatusKey("status.nextSwitchFailed");
    }

    private async Task SendFeedNextCoreAsync(CancellationToken ct,
        BrowserTabViewModel? target = null)
    {
        var tab = target ?? SelectedTab;
        if (tab?.Host is null || !tab.IsInitialized)
            return;
        try
        {
            ct.ThrowIfCancellationRequested();
            await tab.Host.SendFeedNextAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            // Page may reject synthetic keys; the caller decides how to recover.
        }
    }

    private readonly record struct ManualPlaybackSnapshot(string? Identity, string? PageUrl);

    private static ManualPlaybackSnapshot CaptureManualPlaybackSnapshot(BrowserTabViewModel tab)
    {
        var page = tab.Host.CurrentPageUrl?.AbsoluteUri;
        var key = tab.Host.CurrentMediaSessionKey;
        return new ManualPlaybackSnapshot(
            string.IsNullOrEmpty(key) ? null : (page ?? string.Empty) + "\n" + key,
            page);
    }

    private static bool HasManualPlaybackChanged(ManualPlaybackSnapshot before, BrowserTabViewModel tab)
    {
        var current = CaptureManualPlaybackSnapshot(tab);
        if (!string.Equals(before.PageUrl, current.PageUrl, StringComparison.Ordinal))
            return true;
        return AutoModeAdvanceGate.HasPlaybackChanged(before.Identity, current.Identity, false, false);
    }

    private async Task<bool> WaitForManualContentSwitchAsync(ManualPlaybackSnapshot before,
        BrowserTabViewModel tab, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + ManualSwitchTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (ConfirmManualContentSwitch(before, tab))
                return true;
            await Task.Delay(ManualSwitchPoll, ct);
        }
        return ConfirmManualContentSwitch(before, tab);
    }

    private bool ConfirmManualContentSwitch(ManualPlaybackSnapshot before, BrowserTabViewModel tab)
    {
        if (!HasManualPlaybackChanged(before, tab))
            return false;

        return true;
    }

    private async Task RunAutoModeLoopAsync(CancellationToken token)
    {
        var sessionEnd = _autoSessionEndUtc;
        try
        {
            while (!token.IsCancellationRequested && IsAutoMode && !IsAutoLimitReached())
            {
                var probe = await TryAutoEnqueueUntilAsync(sessionEnd, token);
                if (token.IsCancellationRequested || !IsAutoMode)
                    break;
                if (IsAutoTimeExpired())
                {
                    await EndAutoModeTimeoutAsync(token);
                    break;
                }

                if (probe.SkipLive || probe.AdvanceNoAddress || probe.Queued)
                {
                    if (probe.SkipLive)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                            SetStatusKey("status.autoSkipLive"));
                    }

                    if (probe.Queued)
                    {
                        _autoProcessedCount++;
                        await Application.Current.Dispatcher.InvokeAsync(RefreshAutoRunningStatus);
                        await RefreshAutoQueueUiAsync();
                        if (IsAutoCountReached())
                        {
                            await EndAutoModeCountAsync(token);
                            break;
                        }
                    }

                    if (!await AdvanceFeedAsync(sessionEnd, token, previousWasLive: probe.SkipLive))
                    {
                        await EndAutoModeAfterAdvanceFailAsync(token);
                        break;
                    }

                    continue;
                }

                if (IsAutoTimeExpired())
                {
                    await EndAutoModeTimeoutAsync(token);
                    break;
                }
            }

            if (IsAutoMode && !token.IsCancellationRequested && IsAutoLimitReached())
                await EndAutoModeByLimitAsync(token);
        }
        catch (OperationCanceledException)
        {
            // normal stop
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || _autoCts?.Token != token)
                    return;
                StopAutoMode(announce: false);
                SetStatusKey("status.probeFailed", ex.Message);
            });
        }
        finally
        {
            if (token.IsCancellationRequested || _autoCts?.Token == token)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (_autoCts?.Token == token)
                    {
                        _autoCts.Dispose();
                        _autoCts = null;
                    }
                });
            }
        }
    }

    private async Task<bool> AdvanceFeedAsync(DateTime sessionEndUtc, CancellationToken token,
        bool previousWasLive = false)
    {
        var beforeIdentity = CaptureAutoPlaybackIdentity();
        _autoItemStartedAt = DateTimeOffset.UtcNow;
        // Feed-next may navigate; a navigation inside this window belongs to auto, not the user.
        _autoExpectedNavigationUntil = DateTimeOffset.UtcNow + AutoModeAdvanceGate.AddressTimeout;
        try
        {
            await SendFeedNextCoreAsync(token);
            return await WaitForContentSwitchAsync(beforeIdentity, previousWasLive, sessionEndUtc, token);
        }
        finally
        {
            _autoExpectedNavigationUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        }
    }

    private Task EndAutoModeTimeoutAsync(CancellationToken token) =>
        EndAutoModeWithStatusAsync("status.autoStoppedTimeout", token);

    private Task EndAutoModeCountAsync(CancellationToken token) =>
        EndAutoModeWithStatusAsync("status.autoStoppedCount", token);

    private Task EndAutoModeByLimitAsync(CancellationToken token) =>
        IsAutoCountReached() ? EndAutoModeCountAsync(token) : EndAutoModeTimeoutAsync(token);

    private Task EndAutoModeAfterAdvanceFailAsync(CancellationToken token) =>
        IsAutoTimeExpired() ? EndAutoModeTimeoutAsync(token) : EndAutoModeWithStatusAsync("status.autoFeedStuck", token);

    private async Task EndAutoModeWithStatusAsync(string statusKey, CancellationToken token)
    {
        if (token.IsCancellationRequested || _autoCts?.Token != token)
            return;

        if (_autoDbSessionId is Guid sid)
        {
            var session = await _autoSessions.GetSessionAsync(sid);
            if (session is not null)
            {
                session.Status = statusKey is "status.autoStoppedTimeout" or "status.autoStoppedCount"
                    ? AutoSessionStatus.Completed
                    : AutoSessionStatus.Paused;
                session.EndedAt = DateTimeOffset.UtcNow;
                session.UpdatedAt = DateTimeOffset.UtcNow;
                await _autoSessions.SaveSessionAsync(session);
            }
        }

        _autoScheduler.FinishProducer();
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (_autoCts?.Token != token)
                return;
            _autoDbSessionId = null;
            IsAutoMode = false;
            _autoCts?.Dispose();
            _autoCts = null;
            _autoTab = null;
            _autoExpectedNavigationUntil = default;
            _autoFailedFingerprint = null;
            SetStatusKey(statusKey);
        });
        await RefreshAutoQueueUiAsync();
    }

    private static int RemainingSessionMinutes(DateTime sessionEndUtc)
    {
        if (sessionEndUtc >= DateTime.MaxValue - TimeSpan.FromDays(1))
            return 0;
        return Math.Max(0, (int)Math.Ceiling((sessionEndUtc - DateTime.UtcNow).TotalMinutes));
    }

    /// <summary>
    /// Consumes the common page probe's delivered result, persists its address, then delays
    /// five seconds before the caller advances the feed. Downloads are handled by AutoDownloadScheduler.
    /// </summary>
    private async Task<AutoProbeResult> TryAutoEnqueueUntilAsync(DateTime sessionEndUtc, CancellationToken token)
    {
        var persisted = false;
        var subscription = new PublishedProbeSubscription(PublishedProbes);
        while (!token.IsCancellationRequested && DateTime.UtcNow < sessionEndUtc)
        {
            var publication = await subscription.WaitAsync(sessionEndUtc, token);
            if (publication is null) break;
            if (!ReferenceEquals(PublishedProbes.Current, publication)) continue;
            if (publication.SessionId == _autoConsumedProbeSessionId)
            {
                await Task.Delay(200, token);
                continue;
            }
            var result = publication;
            var candidate = result.Addresses.FirstOrDefault();
            if (result.Videos.Any(v => v.IsLive))
            {
                _autoConsumedProbeSessionId = result.SessionId;
                return new AutoProbeResult(Queued: false, SkipLive: true);
            }
            if (result.Videos.Count == 0 || !IsAutoCandidateDownloadable(candidate))
            {
                _autoConsumedProbeSessionId = result.SessionId;
                return new AutoProbeResult(Queued: false, SkipLive: false, AdvanceNoAddress: true);
            }

            if (!persisted && IsAutoCandidateDownloadable(candidate) && _autoDbSessionId is Guid sessionId)
            {
                var saved = await TryPersistAutoCandidateAsync(sessionId, candidate!, token);
                if (saved)
                {
                    persisted = true;
                    _autoConsumedProbeSessionId = result.SessionId;
                    _autoLastEnqueuedFingerprint = BuildAutoQueueFingerprint(candidate!);
                    _autoFailedFingerprint = null;
                    await DelayAfterAutoProbeAsync(token);
                    return new AutoProbeResult(Queued: true, SkipLive: false);
                }

                var fingerprint = BuildAutoQueueFingerprint(candidate!);
                if (IsSameAsLastAutoEnqueue(fingerprint))
                {
                    _autoConsumedProbeSessionId = result.SessionId;
                    // Same content already queued this session — advance after the standard delay.
                    await DelayAfterAutoProbeAsync(token);
                    return new AutoProbeResult(Queued: false, SkipLive: false, AdvanceNoAddress: true);
                }

                _autoFailedFingerprint = fingerprint;
            }

            await Task.Delay(200, token);
        }
        return new AutoProbeResult(Queued: false, SkipLive: false);
    }

    private async Task DelayAfterAutoProbeAsync(CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow + AutoModeAdvanceGate.DownloadSwitchDelay;
        while (DateTimeOffset.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            var seconds = Math.Max(1, (int)Math.Ceiling((deadline - DateTimeOffset.UtcNow).TotalSeconds));
            await Application.Current.Dispatcher.InvokeAsync(() => SetStatusKey("status.autoDelayAfterProbe", seconds));
            var remain = deadline - DateTimeOffset.UtcNow;
            if (remain <= TimeSpan.Zero)
                break;
            await Task.Delay(remain < TimeSpan.FromMilliseconds(200) ? remain : TimeSpan.FromMilliseconds(200), token);
        }
    }

    private async Task<bool> TryPersistAutoCandidateAsync(
        Guid sessionId, PublishedVideoAddress current, CancellationToken token)
    {
        if (token.IsCancellationRequested || _autoCts?.Token != token || !IsAutoMode)
            return false;
        if (!IsAutoCandidateDownloadable(current))
            return false;

        var fingerprint = BuildAutoQueueFingerprint(current);
        if (IsSameAsLastAutoEnqueue(fingerprint) ||
            (!string.IsNullOrEmpty(fingerprint) &&
             string.Equals(fingerprint, _autoFailedFingerprint, StringComparison.Ordinal)))
            return false;

        // Pack the full probed ladder into Alternatives so slot-limited downloads keep every resolution.
        var selected = current.Variant;
        var packed = MediaVariantAlternatives.WithLadder(selected, current.Video.Variants);
        var (meta, secret) = DownloadJobMapper.SerializeVariant(packed);
        var now = DateTimeOffset.UtcNow;
        var item = new AutoQueueItem
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            PageUrl = current.Video.PageUrl.AbsoluteUri,
            ContentId = current.Video.SiteContentId,
            Caption = current.Video.DisplayTitle,
            Author = AuthorNameResolver.FromDetectedVideo(current.Video),
            SourceUrl = packed.SourceUrl.AbsoluteUri,
            VariantMetaJson = meta,
            VariantSecret = secret,
            SelectedVariantId = packed.VariantId,
            Fingerprint = fingerprint,
            Status = AutoQueueItemStatus.Queued,
            ProbedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        var saved = await _autoSessions.TryAddQueuedItemAsync(item, token);
        return saved is not null;
    }

    private static string? BuildAutoQueueFingerprint(PublishedVideoAddress item)
    {
        if (!string.IsNullOrWhiteSpace(item.Video.SiteContentId))
            return item.Video.SiteId + ":" + item.Video.SiteContentId.Trim();
        return BuildAutoContentFingerprint(item);
    }

    private readonly record struct AutoProbeResult(bool Queued, bool SkipLive, bool AdvanceNoAddress = false);

    private async Task<bool> WaitForContentSwitchAsync(string? beforeIdentity, bool previousWasLive,
        DateTime sessionEndUtc, CancellationToken token)
    {
        var deadline = _autoItemStartedAt + AutoModeAdvanceGate.AddressTimeout;
        while (!token.IsCancellationRequested && DateTime.UtcNow < sessionEndUtc && DateTimeOffset.UtcNow < deadline)
        {
            var now = CaptureAutoPlaybackIdentity();
            if (AutoModeAdvanceGate.HasPlaybackChanged(beforeIdentity, now, false, false))
            {
                _autoItemStartedAt = DateTimeOffset.UtcNow;
                return true;
            }

            // A slow detector is not permission to send another Next key. Only the 60-second gate may retry.
            var seconds = Math.Max(1, (int)Math.Ceiling((deadline - DateTimeOffset.UtcNow).TotalSeconds));
            await Application.Current.Dispatcher.InvokeAsync(() => SetStatusKey("status.autoNoAddress", seconds));
            await Task.Delay(200, token);
        }

        // The key was ignored (or the page kept playing the same work): report failure instead of
        // pretending the switch succeeded and looping the same content forever.
        return false;
    }

    private string? CaptureAutoPlaybackIdentity()
    {
        if (SelectedTab is not { } tab)
            return null;
        var snapshot = CaptureManualPlaybackSnapshot(tab);
        return snapshot.Identity ?? snapshot.PageUrl;
    }
    private static string? BuildAutoContentFingerprint(PublishedVideoAddress item) =>
        BuildAutoContentFingerprint(item.Video.PageUrl.AbsoluteUri, item.Video.DisplayTitle);

    private static string? BuildAutoContentFingerprint(string? pageUrl, string? caption)
    {
        var url = (pageUrl ?? string.Empty).Trim();
        var text = NormalizeAutoCaption(caption);
        if (url.Length == 0 && text.Length == 0)
            return null;
        return url + "\n" + text;
    }

    private static string NormalizeAutoCaption(string? caption)
    {
        if (string.IsNullOrWhiteSpace(caption))
            return string.Empty;
        var text = caption.Trim();
        if (text is "New Tab" or "about:blank")
            return string.Empty;
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
    }

    private bool IsSameAsLastAutoEnqueue(string? fingerprint) =>
        !string.IsNullOrEmpty(fingerprint) &&
        !string.IsNullOrEmpty(_autoLastEnqueuedFingerprint) &&
        string.Equals(fingerprint, _autoLastEnqueuedFingerprint, StringComparison.Ordinal);

    private static bool IsAutoCandidateDownloadable(PublishedVideoAddress? item) =>
        item is { Video.IsDrmProtected: false, Video.IsLive: false, Variant: not null };

    public async Task StartDownloadAsync(DetectedVideo video, MediaVariant variant) =>
        await TryStartDownloadQuietAsync(video, variant, showErrors: true);

    private async Task<Guid?> TryStartDownloadQuietAsync(
        DetectedVideo video,
        MediaVariant variant,
        bool showErrors = false,
        CancellationToken cancellationToken = default)
    {
        if (video.IsDrmProtected || video.IsLive)
            return null;

        var name = DownloadFileNameBuilder.Build(video, variant);
        try
        {
            var id = await _downloadEngine.EnqueueAsync(
                variant,
                name,
                video.PageUrl,
                video.DisplayTitle,
                video.DurationSec,
                video.Variants,
                AuthorNameResolver.FromDetectedVideo(video),
                cancellationToken);
            _collapsedQueueGroups.Remove(DownloadSiteFolder.Resolve(video.PageUrl));
            RefreshDownloadJobs();
            var created = DownloadJobs.FirstOrDefault(j => j.Job.Id == id);
            if (created is not null)
                SelectOnlyQueueJob(created);
            if (created?.Job.Status == DownloadStatus.Completed)
                SetStatusKey("status.alreadyDownloaded", created.Job.DisplayName);
            else
                SetStatusKey("status.enqueued", name);
            return id;
        }
        catch (DownloadException ex)
        {
            var reason = _loc.T("error." + ex.ErrorCode);
            if (string.Equals(reason, "error." + ex.ErrorCode, StringComparison.Ordinal))
                reason = ex.Message;
            SetStatusKey("status.enqueueFailed", reason);
            if (showErrors)
                MessageBox.Show(reason, _loc.DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDownloadSelectedVideo))]
    private async Task DownloadSelectedVideoAsync()
    {
        var videoVm = SelectedDetectedVideo;
        var variant = videoVm?.SelectedVariant?.Variant;
        if (videoVm is null || variant is null || videoVm.IsDrm)
            return;

        await StartDownloadAsync(videoVm.Video, variant);
    }

    private bool CanDownloadSelectedVideo =>
        CanProbeCurrentPage && SelectedDetectedVideo is { IsDrm: false, SelectedVariant: not null };

    partial void OnSelectedDetectedVideoChanged(DetectedVideoViewModel? oldValue, DetectedVideoViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.PropertyChanged -= OnDetectedVideoPropertyChanged;
        if (newValue is not null)
            newValue.PropertyChanged += OnDetectedVideoPropertyChanged;
        DownloadSelectedVideoCommand.NotifyCanExecuteChanged();
    }

    private void OnDetectedVideoPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DetectedVideoViewModel.SelectedVariant) or nameof(DetectedVideoViewModel.CanDownload))
            DownloadSelectedVideoCommand.NotifyCanExecuteChanged();
    }

    public void RefreshDownloadJobs()
    {
        var selectedIds = _selectedDownloadJobIds.Count > 0
            ? _selectedDownloadJobIds.ToList()
            : SelectedDownloadJob is null
                ? new List<Guid>()
                : new List<Guid> { SelectedDownloadJob.Job.Id };
        var primaryId = SelectedDownloadJob?.Job.Id;

        SyncJobViewModels(_downloadEngine.GetActiveJobs());
        RebuildQueueRows();

        _selectedDownloadJobs.Clear();
        foreach (var id in selectedIds)
        {
            var match = DownloadJobs.FirstOrDefault(j => j.Job.Id == id);
            if (match is not null)
                _selectedDownloadJobs.Add(match);
        }

        _selectedDownloadJobIds.Clear();
        _selectedDownloadJobIds.AddRange(_selectedDownloadJobs.Select(j => j.Job.Id));

        SelectedDownloadJob = primaryId is Guid pid
            ? DownloadJobs.FirstOrDefault(j => j.Job.Id == pid) ?? _selectedDownloadJobs.LastOrDefault()
            : _selectedDownloadJobs.LastOrDefault();

        NotifyQueueCommands();
        if (_selectedDownloadJobIds.Count > 0)
            RestoreQueueSelection?.Invoke(_selectedDownloadJobIds);
    }

    private string? _autoJobStateSignature;

    public void TickDownloads()
    {
        var jobs = _downloadEngine.GetActiveJobs();
        var orderChanged = DownloadJobs.Count != jobs.Count ||
                           !DownloadJobs.Select(vm => vm.Job.Id).SequenceEqual(jobs.Select(j => j.Id));
        if (orderChanged)
        {
            RefreshDownloadJobs();
            if (ShowAutoTaskGroups)
                _ = RefreshAutoQueueUiAsync();
            return;
        }

        foreach (var job in DownloadJobs)
            job.Refresh();
        if (ShowAutoTaskGroups)
        {
            RefreshAutoItemProgressInPlace(jobs);
            var signature = string.Join(";", jobs.Select(j => $"{j.Id}:{j.Status}"));
            if (_autoJobStateSignature != signature)
            {
                _autoJobStateSignature = signature;
                _ = RefreshAutoQueueUiAsync();
            }
        }
        NotifyQueueCommands();
    }

    private void RefreshAutoItemProgressInPlace(IReadOnlyList<DownloadJob> jobs)
    {
        var map = jobs.ToDictionary(j => j.Id);
        foreach (var row in QueueRows.OfType<AutoQueueItemRowViewModel>())
        {
            if (row.Item.DownloadJobId is Guid id && map.TryGetValue(id, out var job))
                row.BindJob(job);
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleQueueGrouping))]
    private void ToggleQueueGrouping()
    {
        _queueGroupingMode = (_queueGroupingMode + 1) % 3;
        _queueGrouped = _queueGroupingMode != 0;
        _options.Ui.QueueGroupingMode = _queueGroupingMode;
        _options.Ui.QueueGrouped = _queueGrouped;
        _settingsStore.Save(_options);
        OnPropertyChanged(nameof(QueuePanelHeader));
        OnPropertyChanged(nameof(IsQueueGrouped));
        OnPropertyChanged(nameof(QueueGroupToggleLabel));
        RebuildQueueRows();
        if (_selectedDownloadJobIds.Count > 0)
            RestoreQueueSelection?.Invoke(_selectedDownloadJobIds);
        NotifyQueueCommands();
    }

    private async Task RefreshAutoQueueUiAsync()
    {
        if (!ShowAutoTaskGroups)
            return;
        try
        {
            var sessions = await _autoSessions.GetSessionsAsync();
            var jobs = _downloadEngine.GetActiveJobs().ToDictionary(j => j.Id);
            var rows = new List<object>();
            var sessionJobIds = new HashSet<Guid>();
            foreach (var session in sessions)
            {
                var items = await _autoSessions.GetItemsAsync(session.Id);
                foreach (var item in items)
                    if (item.DownloadJobId is Guid jobId) sessionJobIds.Add(jobId);
                var completed = items.Count(i => i.DownloadJobId is Guid id && jobs.TryGetValue(id, out var job)
                    ? job.Status == DownloadStatus.Completed
                    : i.Status == AutoQueueItemStatus.Completed);
                var expanded = !_collapsedAutoSessions.Contains(session.Id);
                rows.Add(new AutoSessionRowViewModel(session, items.Count, completed, expanded, _loc));
                if (!expanded)
                    continue;
                foreach (var item in items)
                {
                    jobs.TryGetValue(item.DownloadJobId ?? Guid.Empty, out var job);
                    rows.Add(new AutoQueueItemRowViewModel(item, job, _loc));
                }
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!ShowAutoTaskGroups)
                    return;
                foreach (var job in DownloadJobs.Where(j => j.Job.Status != DownloadStatus.Completed && !sessionJobIds.Contains(j.Job.Id)))
                {
                    job.IsGrouped = false;
                    rows.Add(job);
                }

                var keepSessionId = _selectedAutoSession?.SessionId;
                var keepItemId = _selectedAutoItem?.ItemId;
                var keepJobIds = _selectedDownloadJobIds.ToList();
                QueueSelectionSuspended = true;
                try
                {
                    QueueRows.Clear();
                    foreach (var row in rows)
                        QueueRows.Add(row);
                }
                finally
                {
                    QueueSelectionSuspended = false;
                }

                object? selected = null;
                if (keepItemId is Guid itemId)
                    selected = rows.OfType<AutoQueueItemRowViewModel>().FirstOrDefault(r => r.ItemId == itemId);
                else if (keepSessionId is Guid sessionId)
                    selected = rows.OfType<AutoSessionRowViewModel>().FirstOrDefault(r => r.SessionId == sessionId);

                if (selected is AutoQueueItemRowViewModel itemRow)
                {
                    _selectedAutoItem = itemRow;
                    _selectedAutoSession = null;
                }
                else if (selected is AutoSessionRowViewModel sessionRow)
                {
                    _selectedAutoSession = sessionRow;
                    _selectedAutoItem = null;
                }
                else if (keepSessionId is not null || keepItemId is not null)
                {
                    _selectedAutoSession = null;
                    _selectedAutoItem = null;
                }

                NotifyQueueCommands();
                if (selected is not null)
                    RestoreAutoRowSelection?.Invoke(selected);
                else if (keepJobIds.Count > 0)
                    RestoreQueueSelection?.Invoke(keepJobIds);
                else if (keepSessionId is not null || keepItemId is not null)
                    RestoreAutoRowSelection?.Invoke(null);
            });
        }
        catch
        {
            // UI refresh is best-effort.
        }
    }

    private void SyncJobViewModels(IReadOnlyList<DownloadJob> jobs)
    {
        var existing = DownloadJobs.ToDictionary(j => j.Job.Id);
        DownloadJobs.Clear();
        foreach (var job in jobs)
        {
            if (existing.TryGetValue(job.Id, out var vm))
            {
                vm.IsGrouped = _queueGrouped;
                vm.Refresh();
                DownloadJobs.Add(vm);
            }
            else
            {
                DownloadJobs.Add(new DownloadJobViewModel(job, _loc) { IsGrouped = _queueGrouped });
            }
        }
    }

    private void RebuildQueueRows()
    {
        if (ShowAutoTaskGroups)
        {
            _ = RefreshAutoQueueUiAsync();
            return;
        }

        QueueRows.Clear();
        if (!_queueGrouped)
        {
            foreach (var job in DownloadJobs)
            {
                job.IsGrouped = false;
                job.Refresh();
                QueueRows.Add(job);
            }
            return;
        }

        foreach (var group in DownloadJobs.GroupBy(j => j.GroupDomain, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var expanded = !_collapsedQueueGroups.Contains(group.Key);
            QueueRows.Add(new QueueGroupViewModel(group.Key, group.Count(), expanded));
            if (!expanded)
                continue;
            foreach (var job in group)
            {
                job.IsGrouped = true;
                job.Refresh();
                QueueRows.Add(job);
            }
        }
    }

    public void SetSelectedAutoRows(AutoSessionRowViewModel? session, AutoQueueItemRowViewModel? item)
    {
        _selectedAutoSession = session;
        _selectedAutoItem = item;
        NotifyQueueCommands();
    }

    [RelayCommand(CanExecute = nameof(CanPauseSelected))]
    private async Task PauseSelectedAsync()
    {
        if (_selectedAutoSession is { } session)
        {
            if (session.Session.Status == AutoSessionStatus.Running)
                await ToggleAutoSessionAsync(session.SessionId);
            return;
        }

        if (_selectedAutoItem?.Item.DownloadJobId is Guid autoJobId)
        {
            await _downloadEngine.PauseAsync(autoJobId);
            RefreshDownloadJobs();
            await RefreshAutoQueueUiAsync();
            return;
        }

        var targets = EffectiveSelectedJobs.ToArray();
        if (targets.Length == 0) return;
        foreach (var vm in targets)
            await _downloadEngine.PauseAsync(vm.Job.Id);
        RefreshDownloadJobs();
    }

    [RelayCommand(CanExecute = nameof(CanResumeSelected))]
    private async Task ResumeSelectedAsync()
    {
        if (_selectedAutoSession is { } session)
        {
            if (session.Session.Status is AutoSessionStatus.Paused or AutoSessionStatus.Stopped or AutoSessionStatus.Failed)
                await ToggleAutoSessionAsync(session.SessionId);
            return;
        }

        if (_selectedAutoItem?.Item.DownloadJobId is Guid autoJobId)
        {
            await _downloadEngine.ResumeAsync(autoJobId);
            RefreshDownloadJobs();
            await RefreshAutoQueueUiAsync();
            return;
        }

        var targets = EffectiveSelectedJobs.ToArray();
        if (targets.Length == 0) return;
        foreach (var vm in targets)
            await _downloadEngine.ResumeAsync(vm.Job.Id);
        RefreshDownloadJobs();
    }

    [RelayCommand(CanExecute = nameof(CanCancelSelected))]
    private async Task CancelSelectedAsync()
    {
        if (_selectedAutoSession is { } session)
        {
            await PauseAutoSessionCoreAsync(session.SessionId, pauseJobs: true, markStopped: true);
            if (_autoDbSessionId == session.SessionId)
            {
                try { _autoCts?.Cancel(); } catch { /* ignore */ }
                _autoCts?.Dispose();
                _autoCts = null;
                _autoDbSessionId = null;
                _autoScheduler.Stop();
            }
            SetStatusKey("status.autoSessionPaused", session.Name);
            await RefreshAutoQueueUiAsync();
            return;
        }

        if (_selectedAutoItem is { } autoItem)
        {
            if (autoItem.Item.DownloadJobId is Guid jobId)
                await _downloadEngine.CancelAsync(jobId);
            autoItem.Item.Status = AutoQueueItemStatus.Cancelled;
            await _autoSessions.SaveItemAsync(autoItem.Item);
            SetStatusKey("status.cancelled");
            RefreshDownloadJobs();
            await RefreshAutoQueueUiAsync();
            return;
        }

        var targets = EffectiveSelectedJobs.ToArray();
        if (targets.Length == 0) return;
        foreach (var vm in targets)
            await _downloadEngine.CancelAsync(vm.Job.Id);
        SetStatusKey("status.cancelled");
        RefreshDownloadJobs();
    }

    /// <summary>
    /// Drops the task. Finished videos stay in the library. Unfinished downloads and their files are removed.
    /// </summary>
    private async Task RemoveAutoSessionAsync(AutoSessionRowViewModel session)
    {
        if (_autoDbSessionId == session.SessionId)
            await StopAutoModeAsync(announce: false);
        else if (session.Session.Status == AutoSessionStatus.Running ||
                 _autoScheduler.ActiveSessionId == session.SessionId)
        {
            await PauseAutoSessionCoreAsync(session.SessionId, pauseJobs: false, markStopped: true);
            if (_autoScheduler.ActiveSessionId == session.SessionId)
                _autoScheduler.Stop();
        }

        var jobs = _downloadEngine.GetActiveJobs().ToDictionary(j => j.Id);
        foreach (var item in await _autoSessions.GetItemsAsync(session.SessionId))
        {
            if (IsCompletedAutoItem(item, jobs))
                continue;
            if (item.DownloadJobId is Guid jobId && jobs.TryGetValue(jobId, out var job))
                await DiscardIncompleteAutoDownloadAsync(job);
        }

        await _autoSessions.DeleteSessionAsync(session.SessionId);
        if (_selectedAutoSession?.SessionId == session.SessionId)
            _selectedAutoSession = null;
        SetStatusKey("status.autoSessionRemoved", session.Name);
        RefreshDownloadJobs();
        await RefreshAutoQueueUiAsync();
    }

    private static bool IsCompletedAutoItem(AutoQueueItem item, IReadOnlyDictionary<Guid, DownloadJob> jobs)
    {
        if (item.DownloadJobId is Guid jobId && jobs.TryGetValue(jobId, out var job))
            return job.Status == DownloadStatus.Completed;
        return item.Status == AutoQueueItemStatus.Completed;
    }

    private async Task DiscardIncompleteAutoDownloadAsync(DownloadJob job)
    {
        if (job.Status is DownloadStatus.Completed or DownloadStatus.Removed)
            return;

        var target = job.TargetPath;
        var jobId = job.Id;
        if (job.Status is DownloadStatus.Failed or DownloadStatus.Cancelled)
            await _downloadEngine.RemoveAsync(jobId, deleteFile: false);
        else
            await _downloadEngine.CancelAsync(jobId);

        DeleteIncompleteDownloadFiles(target, jobId);
    }

    private void DeleteIncompleteDownloadFiles(string? target, Guid jobId)
    {
        if (string.IsNullOrWhiteSpace(target))
            return;

        var keptByCompletedJob = _downloadEngine.GetActiveJobs().Any(j =>
            j.Id != jobId &&
            j.Status == DownloadStatus.Completed &&
            string.Equals(j.TargetPath, target, StringComparison.OrdinalIgnoreCase));
        if (!keptByCompletedJob)
            TryDeleteDownloadFile(target);

        TryDeleteDownloadFile(target + ".part");
        var saveDir = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(saveDir))
            return;

        var partsDir = Path.Combine(saveDir, ".parts", jobId.ToString("N"));
        try
        {
            if (Directory.Exists(partsDir))
                Directory.Delete(partsDir, recursive: true);
        }
        catch
        {
            // A file can still be locked by a closing writer.
        }
    }

    private static void TryDeleteDownloadFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore locked or already-removed files
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    private async Task RemoveSelectedAsync()
    {
        if (_selectedAutoSession is { } session)
        {
            await RemoveAutoSessionAsync(session);
            return;
        }

        if (_selectedAutoItem is { } autoItem)
        {
            if (autoItem.Item.DownloadJobId is Guid jobId)
                await _downloadEngine.RemoveAsync(jobId, deleteFile: false);
            await _autoSessions.DeleteItemAsync(autoItem.ItemId);
            _selectedAutoItem = null;
            SetStatusKey("status.removedRecord");
            RefreshDownloadJobs();
            await RefreshAutoQueueUiAsync();
            return;
        }

        var targets = EffectiveSelectedJobs.ToArray();
        if (targets.Length == 0) return;

        var deleteFile = false;
        var hasCompletedFile = targets.Any(vm =>
            vm.Job.Status == DownloadStatus.Completed && File.Exists(vm.Job.TargetPath));
        if (hasCompletedFile)
        {
            var choice = MessageBox.Show(
                _loc.T("dialog.removeBody"),
                _loc.T("dialog.removeTitle"),
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel)
                return;
            deleteFile = choice == MessageBoxResult.Yes;
        }

        foreach (var vm in targets)
        {
            var removeFile = deleteFile &&
                             vm.Job.Status == DownloadStatus.Completed &&
                             File.Exists(vm.Job.TargetPath);
            await _downloadEngine.RemoveAsync(vm.Job.Id, removeFile);
        }

        SetStatusKey(deleteFile ? "status.removedBoth" : "status.removedRecord");
        RefreshDownloadJobs();
    }

    [RelayCommand]
    private async Task DeleteSelectedQueueAsync()
    {
        if (CanCancelSelected)
            await CancelSelectedAsync();
        else if (CanRemoveSelected)
            await RemoveSelectedAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRenameSelected))]
    private void BeginRenameSelected()
    {
        if (!CanRenameSelected || EffectiveSelectedJobs.Count != 1)
            return;

        var target = EffectiveSelectedJobs[0];
        foreach (var job in DownloadJobs)
        {
            if (!ReferenceEquals(job, target) && job.IsEditing)
                job.CancelEdit();
        }

        SelectedDownloadJob = target;
        target.BeginEdit();
    }

    public async Task CommitRenameAsync(DownloadJobViewModel? vm)
    {
        if (vm is null || !vm.IsEditing)
            return;

        var draft = vm.EditStem;
        if (string.IsNullOrWhiteSpace(DownloadFileNameBuilder.FilterLiveInput(draft).Trim().TrimEnd('.')))
        {
            vm.CancelEdit();
            SetStatusKey("status.renameEmpty");
            return;
        }

        try
        {
            var applied = await _downloadEngine.RenameAsync(vm.Job.Id, draft);
            vm.IsEditing = false;
            vm.EditStem = applied;
            vm.Refresh();
            SetStatusKey("status.renamed", applied + vm.ExtensionLabel);
            NotifyQueueCommands();
        }
        catch (Exception ex)
        {
            vm.CancelEdit();
            SetStatusKey("status.renameFailed", ex.Message);
            MessageBox.Show(_loc.Format("status.renameFailed", ex.Message), _loc.DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void CancelRename(DownloadJobViewModel? vm) => vm?.CancelEdit();

    [RelayCommand(CanExecute = nameof(CanPlaySelected))]
    private async Task PlaySelectedDownloadAsync()
    {
        var job = EffectiveSelectedJobs.Count == 1
            ? EffectiveSelectedJobs[0].Job
            : SelectedDownloadJob?.Job;
        if (job is null)
            return;

        if (job.Status != DownloadStatus.Completed)
        {
            SetStatusKey("status.playIncomplete");
            MessageBox.Show(
                _loc.T("dialog.playIncomplete"),
                _loc.DialogTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(job.TargetPath) || !File.Exists(job.TargetPath))
        {
            SetStatusKey("status.fileMissing");
            MessageBox.Show(
                _loc.T("dialog.fileMissing"),
                _loc.DialogTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            RefreshDownloadJobs();
            return;
        }

        if (!_localLibrary.IsRunning)
        {
            SetStatusKey("status.localLibraryUnavailable");
            MessageBox.Show(
                _loc.T("status.localLibraryUnavailable"),
                _loc.DialogTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            var playUrl = _localLibrary.PlayUrl(job.Id);
            AddressBar = playUrl;
            await NavigateAsync();
            SetStatusKey("status.playedLocal", Path.GetFileName(job.TargetPath));
        }
        catch (Exception ex)
        {
            SetStatusKey("status.playFailed", ex.Message);
            MessageBox.Show(
                _loc.Format("status.playFailed", ex.Message),
                _loc.DialogTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void OpenSelectedDownload() => _ = PlaySelectedDownloadAsync();

    [RelayCommand(CanExecute = nameof(CanOpenSelectedPage))]
    private async Task OpenSelectedPageAsync()
    {
        var pageUrl = EffectiveSelectedJobs.Count == 1
            ? EffectiveSelectedJobs[0].Job.PageUrl
            : SelectedDownloadJob?.Job.PageUrl;
        if (pageUrl is null)
        {
            MessageBox.Show(_loc.T("status.noPageUrl"), _loc.DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AddressBar = pageUrl.AbsoluteUri;
        if (SelectedTab is not null)
            SelectedTab.Address = pageUrl.AbsoluteUri;

        await NavigateCommand.ExecuteAsync(null);
        SetStatusKey("status.openedPage", pageUrl.AbsoluteUri);
    }

    [RelayCommand(CanExecute = nameof(CanOpenSelectedFolder))]
    private void OpenSelectedFolder()
    {
        if (EffectiveSelectedJobs.Count != 1)
            return;

        var job = EffectiveSelectedJobs[0].Job;
        if (!TryResolveOpenFolderPath(job, out var path))
        {
            SetStatusKey("status.folderMissing");
            MessageBox.Show(
                _loc.T("status.folderMissing"),
                _loc.DialogTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + path + "\"",
                    UseShellExecute = true
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }

            SetStatusKey("status.openedFolder", path);
        }
        catch (Exception ex)
        {
            SetStatusKey("status.openFolderFailed", ex.Message);
            MessageBox.Show(
                _loc.Format("status.openFolderFailed", ex.Message),
                _loc.DialogTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static bool TryResolveOpenFolderPath(DownloadJob job, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(job.TargetPath))
            return false;

        if (File.Exists(job.TargetPath))
        {
            path = job.TargetPath;
            return true;
        }

        var partPath = job.TargetPath + ".part";
        if (File.Exists(partPath))
        {
            path = partPath;
            return true;
        }

        var dir = Path.GetDirectoryName(job.TargetPath);
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            path = dir;
            return true;
        }

        return false;
    }

    private void NotifyQueueCommands()
    {
        OnPropertyChanged(nameof(CanPauseSelected));
        OnPropertyChanged(nameof(CanResumeSelected));
        OnPropertyChanged(nameof(CanCancelSelected));
        OnPropertyChanged(nameof(CanRemoveSelected));
        OnPropertyChanged(nameof(CanRenameSelected));
        OnPropertyChanged(nameof(CanPlaySelected));
        OnPropertyChanged(nameof(CanOpenSelectedPage));
        OnPropertyChanged(nameof(CanOpenSelectedFolder));
        PauseSelectedCommand.NotifyCanExecuteChanged();
        ResumeSelectedCommand.NotifyCanExecuteChanged();
        CancelSelectedCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        BeginRenameSelectedCommand.NotifyCanExecuteChanged();
        PlaySelectedDownloadCommand.NotifyCanExecuteChanged();
        OpenSelectedPageCommand.NotifyCanExecuteChanged();
        OpenSelectedFolderCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Refresh queue command enablement after right-click selection changes.</summary>
    public void NotifyQueueCommandsPublic() => NotifyQueueCommands();

    private static void DispatchProbePublication(Action apply)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess()) apply();
        else _ = dispatcher.InvokeAsync(apply, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnVideoDetected(object? sender, DetectedVideo video)
    {
        var displayEpoch = _feedDisplayState.Epoch;
        DispatchProbePublication(() =>
        {
            if (!_feedDisplayState.CanPublish(displayEpoch, video.SessionId, _pipeline.SessionId)) return;
            if (!IsVideoRelevantToCurrentPage(video))
                return;

            // Ignore empty publishes — they must not wipe a sticky last-good result.
            if (video.Variants.Count == 0 && !video.IsLive)
                return;

            CollectProbeVideo(video);
        });
    }

    private void OnPageProbed(object? sender, IReadOnlyList<DetectedVideo> videos)
    {
        var displayEpoch = _feedDisplayState.Epoch;
        DispatchProbePublication(() =>
        {
            var relevant = videos
                .Where(v => v.Variants.Count > 0 || v.IsLive)
                .Where(v => _feedDisplayState.CanPublish(displayEpoch, v.SessionId, _pipeline.SessionId))
                .Where(IsVideoRelevantToCurrentPage)
                .ToArray();
            if (relevant.Length == 0)
                return;

            if (!_detectionRunning) return;
            var pageKey = BuildPageIdentity(relevant[0].PageUrl);
            var keepIds = relevant.Select(v => v.VideoId).ToHashSet();
            foreach (var prior in _pendingProbeVideos.Values.Where(v =>
                string.Equals(BuildPageIdentity(v.PageUrl), pageKey, StringComparison.OrdinalIgnoreCase) &&
                !keepIds.Contains(v.VideoId)).ToArray())
                _pendingProbeVideos.Remove(prior.VideoId);
            foreach (var video in relevant)
                CollectProbeVideo(video);
        });
    }

    // The probe owns all intermediate observations. Both consumers see only this final batch.
    private void CollectProbeVideo(DetectedVideo video)
    {
        if (!_detectionRunning) return;
        if (_pendingProbeVideos.TryGetValue(video.VideoId, out var previous) &&
            (IsEquivalentDetection(video, previous) || IsWeakerDetection(video, previous))) return;
        _pendingProbeVideos[video.VideoId] = video;
    }

    private DetectedVideo[] PublishFinalProbe(Guid sessionId)
    {
        var videos = _pendingProbeVideos.Values
            .Where(v => v.SessionId == sessionId && IsVideoRelevantToCurrentPage(v)).ToArray();
        _detectionRunning = false;
        foreach (var video in videos)
            UpsertDetectedVideo(video, focus: false);
        if (SelectedDetectedVideo is null && DetectedVideos.Count > 0)
            FocusLargestVideoVariant(DetectedVideos[0]);
        _forceReplaceResults = false;
        return DetectedVideos.Select(v => v.Video).ToArray();
    }

    private PageProbeResult BuildPublishedProbeResult(Guid sessionId, DetectedVideo[] videos, string? error = null)
    {
        var addresses = videos.Select(video =>
            _videoMap.TryGetValue(video.VideoId, out var card) && card.SelectedVariant is { } selected
                ? new PublishedVideoAddress(video, selected.Variant) : null)
            .OfType<PublishedVideoAddress>().ToArray();
        return new(sessionId, Array.AsReadOnly(videos), error)
        {
            Addresses = Array.AsReadOnly(addresses)
        };
    }

    private void UpsertDetectedVideo(DetectedVideo video, bool focus)
    {
        if (_videoMap.TryGetValue(video.VideoId, out var existing))
        {
            var force = _forceReplaceResults;
            if (!force && IsEquivalentDetection(video, existing.Video))
                return;
            if (!force && IsWeakerDetection(video, existing.Video))
                return;

            existing.Update(video);
            existing.ReplaceVariants(video);
            RepositionDetectedVideo(existing);
            if (IsExclusiveSiteId(video.SiteId))
                PruneOtherVideosForPage(video.PageUrl, video.VideoId);
            if (focus)
                FocusLargestVideoVariant(existing);
            SetStatusKey(_pipeline.IsCompleted ? "status.probeDone" : "status.probeFound", DetectedVideos.Count);
            return;
        }

        var vm = new DetectedVideoViewModel();
        vm.BindLocalization(_loc);
        vm.Update(video);
        vm.ReplaceVariants(video);
        _videoMap[video.VideoId] = vm;
        InsertDetectedVideo(vm);
        // Exclusive sites keep one card (largest/current work); Generic may keep multi-video cards.
        if (IsExclusiveSiteId(video.SiteId))
            PruneOtherVideosForPage(video.PageUrl, video.VideoId);
        if (focus)
            FocusLargestVideoVariant(vm);
        SetStatusKey(_pipeline.IsCompleted ? "status.probeDone" : "status.probeFound", DetectedVideos.Count);
    }

    private static bool IsExclusiveSiteId(string siteId) =>
        siteId is SiteIds.Douyin or SiteIds.TikTok or SiteIds.YouTube or SiteIds.Bilibili;

    /// <summary>Host-level exclusive sites (Douyin/TikTok/YouTube/Bilibili) for settle/probe tuning.</summary>
    private static bool IsExclusiveHost(Uri pageUrl) =>
        SiteHosts.MatchesAny(pageUrl,
            "douyin.com", "iesdouyin.com", "tiktok.com",
            "youtube.com", "youtu.be", "youtube-nocookie.com",
            "bilibili.com", "b23.tv");

    private void FocusLargestVideoVariant(DetectedVideoViewModel vm)
    {
        SelectedDetectedVideo = vm;
        var videoLabel = _loc.ModeVideo;
        var audioLabel = _loc.ModeAudio;
        var hasVideo = vm.AllVariants.Any(v => DetectedVideoViewModel.MatchesMode(v.Variant, videoLabel));
        vm.SelectedMode = hasVideo ? videoLabel : audioLabel;
        vm.ApplyModeFilter();
        var preferred = VideoDownloader.Infrastructure.Detection.MediaVariantRanking
            .SelectPreferredVideo(vm.Variants.Select(v => v.Variant))
            ?? VideoDownloader.Infrastructure.Detection.MediaVariantRanking
                .SelectPreferredAudio(vm.Variants.Select(v => v.Variant));
        vm.SelectedVariant = preferred is null
            ? vm.Variants.FirstOrDefault()
            : vm.Variants.FirstOrDefault(v => ReferenceEquals(v.Variant, preferred)
                || v.Variant.SourceUrl == preferred.SourceUrl
                   && v.Variant.VariantId == preferred.VariantId)
              ?? vm.Variants.FirstOrDefault();
    }

    private void PruneOtherVideosForPage(Uri pageUrl, Guid keepId)
    {
        var pageKey = BuildPageIdentity(pageUrl);
        for (var i = DetectedVideos.Count - 1; i >= 0; i--)
        {
            var item = DetectedVideos[i];
            if (item.Video.VideoId == keepId)
                continue;
            if (!string.Equals(BuildPageIdentity(item.Video.PageUrl), pageKey, StringComparison.OrdinalIgnoreCase))
                continue;

            DetectedVideos.RemoveAt(i);
            _videoMap.Remove(item.Video.VideoId);
            if (ReferenceEquals(SelectedDetectedVideo, item))
                SelectedDetectedVideo = null;
        }
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is a clear downgrade of <paramref name="current"/>
    /// (typical false-positive flash after a soft re-detect).
    /// </summary>
    private static bool IsWeakerDetection(DetectedVideo candidate, DetectedVideo current)
    {
        if (candidate.IsLive) return false;
        if (current.Variants.Count == 0)
            return false;
        if (candidate.Variants.Count == 0)
            return true;

        // Pair completion outranks size heuristics: an audio length may still be unknown.
        if (VideoDownloader.Infrastructure.Detection.MediaVariantReconciler.HasAudioCompletion(candidate, current))
            return false;
        if (VideoDownloader.Infrastructure.Detection.MediaVariantReconciler.HasAudioCompletion(current, candidate))
            return true;

        // Different playing media on the same page (feed swipe) — always accept the new set.
        static Uri? PrimaryVideoUrl(DetectedVideo v) =>
            v.Variants
                .Where(x => x.Tracks.Any(t => t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined))
                .Select(x => x.SourceUrl)
                .FirstOrDefault();

        var curUrl = PrimaryVideoUrl(current);
        var newUrl = PrimaryVideoUrl(candidate);
        if (curUrl is not null && newUrl is not null &&
            !VideoDownloader.Infrastructure.Detection.MediaUrlNormalizer.IsSameSession(curUrl, newUrl))
            return false;

        var currentHasVideo = current.Variants.Any(v => v.Tracks.Any(t => t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined));
        var candidateHasVideo = candidate.Variants.Any(v => v.Tracks.Any(t => t.Kind is MediaTrackKind.Video or MediaTrackKind.Combined));
        if (currentHasVideo && !candidateHasVideo)
            return true;

        // Prefer sets that include an audio track when both have video.
        var currentHasAudio = current.Variants.Any(v =>
            v.Tracks.Any(t => t.Kind == MediaTrackKind.Audio) ||
            v.Tracks.Any(t => t.Kind == MediaTrackKind.Combined));
        var candidateHasAudio = candidate.Variants.Any(v =>
            v.Tracks.Any(t => t.Kind == MediaTrackKind.Audio) ||
            v.Tracks.Any(t => t.Kind == MediaTrackKind.Combined));
        if (currentHasAudio && !candidateHasAudio && currentHasVideo && candidateHasVideo)
            return true;

        var currentBest = GetLargestVariantSize(current);
        var candidateBest = GetLargestVariantSize(candidate);
        if (currentBest > 0 && candidateBest > 0 && candidateBest < currentBest / 4)
            return true;

        var currentHeight = current.Variants.Max(v => v.Height ?? 0);
        var candidateHeight = candidate.Variants.Max(v => v.Height ?? 0);
        if (currentHeight >= 360 && candidateHeight > 0 && candidateHeight < currentHeight / 2)
            return true;

        return false;
    }

    private static bool IsEquivalentDetection(DetectedVideo candidate, DetectedVideo current)
    {
        if (!string.Equals(candidate.DisplayTitle, current.DisplayTitle, StringComparison.Ordinal) ||
            candidate.IsDrmProtected != current.IsDrmProtected ||
            candidate.Variants.Count != current.Variants.Count)
            return false;

        static string Key(MediaVariant variant) => string.Join("|", variant.Tracks
            .Select(track => $"{track.Kind}:{track.SourceUrl.AbsoluteUri}:{track.ContentLength}:{track.Bandwidth}"));

        return candidate.Variants.Select(Key).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(current.Variants.Select(Key).OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
    }

    private void OnTabPageIdentityChanged(BrowserTabViewModel tab, PageIdentityChangedEventArgs e)
    {
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            // YouTube playlist SPA: observation href may lead Source by one tick — still sync UI.
            if (tab.Host.CurrentPageUrl is { } current &&
                !IsSameDocumentPage(current, e.PageUrl) &&
                !WebView2Host.AcceptsObservedPageUrl(current, e.PageUrl))
                return;
            HangProbe.Mark("vm.pageIdentity", $"{e.PageUrl.Host}{e.PageUrl.AbsolutePath}");
            tab.Address = e.PageUrl.AbsoluteUri;
            if (!string.IsNullOrWhiteSpace(e.PageTitle))
                tab.Title = e.PageTitle!;

            if (!ReferenceEquals(SelectedTab, tab))
                return;

            AddressBar = e.PageUrl.AbsoluteUri;
            RefreshSidePanelVisibility(e.PageUrl);

            // Soft document URL change (SPA) without NavigationStarting (modal_id etc.).
            // YouTube: ignore list/index-only AbsoluteUri jitter — compare stable watch id.
            var pageOnly = BuildPageIdentity(e.PageUrl);
            if (string.Equals(_lastNavigatedPageUrl, pageOnly, StringComparison.Ordinal) ||
                string.Equals(_currentPageIdentity, pageOnly, StringComparison.Ordinal) ||
                IsSameYouTubeIdentity(_currentPageIdentity, e.PageUrl, tab.Host.CurrentMediaSessionKey))
                return;

            RestartDetectionForPageChange(
                e.PageUrl,
                e.PageTitle,
                mediaSessionKey: tab.Host.CurrentMediaSessionKey,
                allowSuspended: true);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static bool IsSameDocumentPage(Uri? a, Uri? b)
    {
        if (a is null || b is null)
            return false;
        if (string.Equals(a.AbsoluteUri, b.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            return true;
        return WebView2Host.IsSameYouTubeWatchVideo(a, b);
    }

    private static bool IsSameYouTubeIdentity(string? currentIdentity, Uri pageUrl, string? mediaSessionKey)
    {
        if (string.IsNullOrWhiteSpace(currentIdentity) ||
            !SiteHosts.MatchesAny(pageUrl, "youtube.com", "youtu.be", "youtube-nocookie.com"))
            return false;
        var currentStable = ExtractStableContentKeyFromIdentity(currentIdentity);
        var nextStable = ExtractStableContentKey(mediaSessionKey, pageUrl);
        return currentStable is not null && nextStable is not null &&
               string.Equals(currentStable, nextStable, StringComparison.Ordinal);
    }

    private bool IsVideoRelevantToCurrentPage(DetectedVideo video)
    {
        var current = _currentPageIdentity;
        if (string.IsNullOrWhiteSpace(current))
            return true;

        var currentPage = current.Split('\n')[0];
        var videoIdentity = BuildPageIdentity(video.PageUrl);
        var videoPage = videoIdentity.Split('\n')[0];
        if (string.Equals(currentPage, videoPage, StringComparison.OrdinalIgnoreCase))
            return true;

        // Soft-nav / query-normalized pages: compare without trailing slash / fragment already normalized.
        if (string.Equals(
                currentPage.TrimEnd('/'),
                videoPage.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase))
            return true;

        // Same stable content id even when query/list params differ.
        var currentStable = ExtractStableContentKeyFromIdentity(current);
        var videoStable = ExtractStableContentKeyFromIdentity(videoIdentity);
        if (currentStable is not null &&
            videoStable is not null &&
            string.Equals(currentStable, videoStable, StringComparison.Ordinal))
            return true;

        // Two known but different content ids are different videos; same-host feed tolerance
        // below must not let the retired video's card survive into the next one.
        if (currentStable is not null && videoStable is not null)
            return false;

        // Exclusive-site feed soft-nav: same host accepts the exclusive card.
        // Douyin/TikTok paths kept; YouTube/Bilibili added so content-keyed identities are not dropped.
        if (Uri.TryCreate(currentPage, UriKind.Absolute, out var cur) &&
            SiteHosts.MatchesAny(cur,
                "douyin.com", "tiktok.com", "iesdouyin.com",
                "youtube.com", "youtu.be", "bilibili.com") &&
            string.Equals(cur.Host, video.PageUrl.Host, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static string BuildPageIdentity(Uri pageUrl, string? mediaSessionKey = null)
    {
        var builder = new UriBuilder(pageUrl) { Fragment = string.Empty };
        var page = builder.Uri.AbsoluteUri.TrimEnd('/');
        var stable = ExtractStableContentKey(mediaSessionKey, pageUrl);
        if (!string.IsNullOrWhiteSpace(stable))
            return page + "\n" + stable;
        if (string.IsNullOrWhiteSpace(mediaSessionKey))
            return page;

        // This is a logical identity, not a transport URL to normalize.
        return page + "\n" + mediaSessionKey;
    }

    private static string? ExtractStableContentKey(string? mediaSessionKey, Uri? pageUrl)
    {
        // YouTube updates its URL before the debounced player key catches up.
        if (pageUrl is not null && !string.IsNullOrWhiteSpace(mediaSessionKey) &&
            SiteHosts.MatchesAny(pageUrl, "youtube.com", "youtube-nocookie.com", "youtu.be") &&
            ExtractStableContentKey(null, pageUrl) is { } pageKey)
            return pageKey;

        if (!string.IsNullOrWhiteSpace(mediaSessionKey))
        {
            // Douyin/TikTok digit ids (unchanged).
            var fromKey = System.Text.RegularExpressions.Regex.Match(mediaSessionKey, @"(\d{10,})");
            if (fromKey.Success)
                return "content:" + fromKey.Groups[1].Value;

            var ytKey = System.Text.RegularExpressions.Regex.Match(
                mediaSessionKey,
                @"(?:content:)?youtube:(?<id>[\w-]{6,})",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (ytKey.Success)
                return "content:youtube:" + ytKey.Groups["id"].Value;

            var bvKey = System.Text.RegularExpressions.Regex.Match(
                mediaSessionKey,
                @"\b(?<id>BV[\w]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (bvKey.Success)
                return "content:bilibili:" + bvKey.Groups["id"].Value;
        }

        if (pageUrl is null)
            return null;

        // Douyin/TikTok path + query digits (unchanged).
        var path = System.Text.RegularExpressions.Regex.Match(
            pageUrl.AbsolutePath, @"/(?:video|note)/(?<id>\d{10,})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (path.Success)
            return "content:" + path.Groups["id"].Value;

        foreach (var key in new[] { "modal_id=", "item_id=", "aweme_id=" })
        {
            var idx = pageUrl.Query.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var start = idx + key.Length;
            var end = pageUrl.Query.IndexOf('&', start);
            var raw = end < 0 ? pageUrl.Query[start..] : pageUrl.Query[start..end];
            if (System.Text.RegularExpressions.Regex.IsMatch(raw, @"^\d{10,}$"))
                return "content:" + raw;
        }

        // YouTube watch / Shorts / youtu.be (additive).
        var ytWatch = System.Text.RegularExpressions.Regex.Match(
            pageUrl.Query, @"[?&]v=(?<id>[\w-]{6,})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (ytWatch.Success)
            return "content:youtube:" + ytWatch.Groups["id"].Value;
        var ytShorts = System.Text.RegularExpressions.Regex.Match(
            pageUrl.AbsolutePath, @"/shorts/(?<id>[\w-]{6,})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (ytShorts.Success)
            return "content:youtube:" + ytShorts.Groups["id"].Value;
        if (SiteHosts.Matches(pageUrl, "youtu.be"))
        {
            var id = pageUrl.AbsolutePath.Trim('/');
            if (id.Length >= 6)
                return "content:youtube:" + id.Split('/')[0];
        }

        // Bilibili BV (additive).
        var bv = System.Text.RegularExpressions.Regex.Match(
            pageUrl.AbsolutePath, @"/video/(?<id>BV[\w]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (bv.Success)
            return "content:bilibili:" + bv.Groups["id"].Value;

        return null;
    }

    private static string? ExtractStableContentKeyFromIdentity(string? pageIdentity)
    {
        if (string.IsNullOrWhiteSpace(pageIdentity))
            return null;
        var parts = pageIdentity.Split('\n');
        if (parts.Length >= 2)
            return ExtractStableContentKey(parts[1], null);
        return ExtractStableContentKey(null, Uri.TryCreate(parts[0], UriKind.Absolute, out var page) ? page : null);
    }

    private void RepositionDetectedVideo(DetectedVideoViewModel vm)
    {
        DetectedVideos.Remove(vm);
        InsertDetectedVideo(vm);
    }

    private void InsertDetectedVideo(DetectedVideoViewModel vm)
    {
        var size = GetLargestVariantSize(vm.Video);
        var index = 0;
        while (index < DetectedVideos.Count &&
               GetLargestVariantSize(DetectedVideos[index].Video) >= size)
        {
            index++;
        }

        DetectedVideos.Insert(index, vm);
    }

    private static long GetLargestVariantSize(DetectedVideo video) =>
        video.Variants
            .Select(v => v.TotalContentLength ?? v.Bandwidth ?? 0)
            .DefaultIfEmpty(0)
            .Max();

    public async Task DisposeHostsAsync()
    {
        foreach (var tab in Tabs.ToArray())
        {
            try
            {
                await tab.Host.DisposeAsync();
            }
            catch
            {
                // ignore
            }
        }
    }
}
