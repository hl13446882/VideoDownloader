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
using VideoDownloader.Infrastructure.Browser;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Detection;
using VideoDownloader.Infrastructure.Diagnostics;
using VideoDownloader.Infrastructure.LocalLibrary;
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
    public bool CanDownload => !Video.IsDrmProtected && !Video.IsLive && !Video.IsFormatDiscoveryPending && SelectedVariant is not null;

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
            if (Video.Variants.Count == 0 || Video.IsFormatDiscoveryPending)
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
        var wasDefaultSelection = ReferenceEquals(SelectedVariant, Variants.FirstOrDefault());
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

        // Follow newly discovered higher qualities while still on the default; preserve an explicit other choice.
        SelectedVariant =
            (wasDefaultSelection ? Variants.FirstOrDefault() : null) ??
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

        // AllVariants is quality-ranked; first match is the highest resolution for this mode.
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
    private readonly IMediaAggregator _aggregator;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly LocalizationService _loc;
    private readonly AppOptions _options;
    private readonly UserSettingsStore _settingsStore;
    private readonly LocalLibraryHost _localLibrary;
    private readonly HashSet<string> _collapsedQueueGroups = new(StringComparer.OrdinalIgnoreCase);
    private bool _queueGrouped;
    private readonly Dictionary<Guid, DetectedVideoViewModel> _videoMap = new();
    private readonly object _pageSync = new();
    private CancellationTokenSource _probeCts = new();
    private string? _currentPageIdentity;
    private string? _lastNavigatedPageUrl;
    private long _pageGeneration;
    private readonly FeedDetectionDisplayState _feedDisplayState = new();
    private bool _detectionRunning;
    private DetectionResultBuffer? _detectionResults;
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

    partial void OnIsAutoModeChanged(bool value) => OnPropertyChanged(nameof(AutoModeButtonLabel));

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

    /// <summary>Hidden on local library pages so the WebView can use full width.</summary>
    [ObservableProperty]
    private bool _isSidePanelVisible = true;

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
    public string QueueGroupToggleLabel => _queueGrouped ? _loc.T("btn.ungroup") : _loc.T("btn.group");

    /// <summary>Queue multi-selection (click toggles). Batch ops require every selected row to support them.</summary>
    public IReadOnlyList<DownloadJobViewModel> SelectedDownloadJobs => _selectedDownloadJobs;

    /// <summary>Invoked after queue rebuild so the ListBox can restore multi-selection.</summary>
    public Action<IReadOnlyList<Guid>>? RestoreQueueSelection { get; set; }

    private readonly List<DownloadJobViewModel> _selectedDownloadJobs = new();
    private readonly List<Guid> _selectedDownloadJobIds = new();

    private IReadOnlyList<DownloadJobViewModel> EffectiveSelectedJobs =>
        _selectedDownloadJobs.Count > 0
            ? _selectedDownloadJobs
            : SelectedDownloadJob is null
                ? Array.Empty<DownloadJobViewModel>()
                : new[] { SelectedDownloadJob };

    public bool CanPauseSelected =>
        EffectiveSelectedJobs.Count > 0 &&
        EffectiveSelectedJobs.All(j => j.Job.Status is DownloadStatus.Downloading);

    public bool CanResumeSelected =>
        EffectiveSelectedJobs.Count > 0 &&
        EffectiveSelectedJobs.All(j => j.Job.Status is DownloadStatus.Paused or DownloadStatus.Failed);

    public bool CanCancelSelected =>
        EffectiveSelectedJobs.Count > 0 &&
        EffectiveSelectedJobs.All(j => j.Job.Status is not (
            DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Removed or DownloadStatus.Cancelled));

    public bool CanRemoveSelected =>
        EffectiveSelectedJobs.Count > 0 &&
        EffectiveSelectedJobs.All(j => j.Job.Status is DownloadStatus.Completed or DownloadStatus.Failed);

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
        _aggregator = aggregator;
        _settingsViewModel = settingsViewModel;
        _loc = loc;
        _localLibrary = localLibrary;
        _options = services.GetRequiredService<AppOptions>();
        _settingsStore = services.GetRequiredService<UserSettingsStore>();
        _queueGrouped = _options.Ui.QueueGrouped;

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
        StopAutoMode(announce: false);
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
                    HangProbe.Mark("vm.navStarted", $"{page.Host}{page.AbsolutePath}");
                    if (IsAppFullscreen && !LocalLibraryHost.IsLocalPlayerUrl(page))
                        SetAppFullscreen(false);
                    RestartDetectionForPageChange(page, null, mediaSessionKey: null, forceRestart: true);
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
                Application.Current.Dispatcher.Invoke(() => SuspendDetectedResults(tab));
            };
            tab.Host.MediaSessionChanged += (_, e) =>
            {
                if (!ReferenceEquals(SelectedTab, tab))
                    return;
                _ = Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    HangProbe.Mark("vm.mediaSession", $"{e.PageUrl.Host}{e.PageUrl.AbsolutePath} key={e.MediaSessionKey}");
                    lock (_pageSync)
                    {
                        // Duplicate identity for the session already under probe — skip only.
                        if (!e.ForceRestart &&
                            _detectionRunning &&
                            string.Equals(_currentPageIdentity, BuildPageIdentity(e.PageUrl, e.MediaSessionKey), StringComparison.Ordinal))
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
        IsSidePanelVisible = pageUrl is null || !LocalLibraryHost.IsLocalLibraryHost(pageUrl);
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
        if (SelectedDownloadJob is not null)
            SelectedDownloadJob = null;
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

    [RelayCommand]
    private void Refresh()
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

        // Manual probe: singleton destroy + full re-probe (same entry as auto navigation/SPA/session).
        ClearStatus();
        SetStatusKey("status.probeManual");
        RestartDetectionForPageChange(
            pageUrl,
            tab?.Title,
            mediaSessionKey: tab?.Host.CurrentMediaSessionKey, forceRestart: true);
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
        bool forceRestart = false)
    {
        HangProbe.Mark("vm.restart", $"{pageUrl.Host}{pageUrl.AbsolutePath} det={_detectionRunning}");

        if (LocalLibraryHost.IsLocalLibraryHost(pageUrl))
        {
            EnterLocalLibraryMode(pageUrl);
            return;
        }

        if (!forceRestart && !_feedDisplayState.CanBeginSession(BuildPageIdentity(pageUrl).Split('\n')[0],
                ExtractStableContentKey(null, pageUrl) ?? ExtractStableContentKey(mediaSessionKey, null), mediaSessionKey))
            return;

        lock (_pageSync)
        {
            if (!forceRestart)
            {
                var incomingStable = ExtractStableContentKey(null, pageUrl) ?? ExtractStableContentKey(mediaSessionKey, null);
                var currentStable = ExtractStableContentKeyFromIdentity(_currentPageIdentity);
                // Duplicate browser notifications cannot restart either a running or finished run.
                if ((incomingStable is not null && incomingStable == currentStable) ||
                    (incomingStable is null && _currentPageIdentity == BuildPageIdentity(pageUrl, mediaSessionKey)))
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
        StopAutoMode(announce: false);
        lock (_pageSync)
        {
            _pageGeneration++;
            _probeCts.Cancel();
            _detectionResults?.Cancel();
            _detectionRunning = false;
            _currentPageIdentity = BuildPageIdentity(pageUrl);
            _lastNavigatedPageUrl = _currentPageIdentity;
        }

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
            if (host.Contains("douyin.com", StringComparison.OrdinalIgnoreCase) ||
                host.Contains("iesdouyin.com", StringComparison.OrdinalIgnoreCase))
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

            if (host.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase) &&
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
            (host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
             host.Contains("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase) ||
             host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)))
        {
            var videoId = stable["content:youtube:".Length..];
            if (videoId.Length > 0)
                return new Uri($"https://www.youtube.com/watch?v={videoId}");
        }

        if (stable.StartsWith("content:bilibili:", StringComparison.OrdinalIgnoreCase) &&
            (host.Contains("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
             host.Contains("b23.tv", StringComparison.OrdinalIgnoreCase)))
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
            _detectionResults?.Cancel();
            _detectionRunning = false;
            _currentPageIdentity = null;
        }
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
            _detectionResults?.Cancel();
            _currentPageIdentity = BuildPageIdentity(pageUrl, mediaSessionKey);
            _lastNavigatedPageUrl = BuildPageIdentity(pageUrl);
            _forceReplaceResults = true;
            _detectionRunning = true;
            generation = ++_pageGeneration;
            _probeCts.Cancel();
            _probeCts.Dispose();
            _probeCts = new CancellationTokenSource();
            token = _probeCts.Token;
        }

        _feedDisplayState.BeginSession();
        DetectedVideos.Clear();
        _videoMap.Clear();
        SelectedDetectedVideo = null;
        _aggregator.Clear();
        if (_pipeline is RoutedMediaDetectionPipeline routed) routed.Reenter(pageUrl);
        else HardResetDetectionPipeline();
        SelectedTab?.Host.ResetDetectionSession();
        var results = _detectionResults = new DetectionResultBuffer(_pipeline.SessionId);
        _ = RunStableDetectionSessionAsync(pageUrl, pageTitle, token, generation, results);
    }

    private async Task RunStableDetectionSessionAsync(
        Uri pageUrl,
        string? pageTitle,
        CancellationToken token,
        long generation, DetectionResultBuffer results)
    {
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
            bool Ready() => Current() && results.HasUsableResult;
            bool Stop() => !Current() || results.HasLiveResult;

            // Observe first without external processes; then race one extraction with continuing
            // browser observations. No second full round and no restarting the same singleton.
            await DetectionProbeScheduler.RunAsync(
                observe: ct => RunPagePassAsync(pageUrl, pageTitle, ct, generation, runExternal: false),
                extract: siteId == SiteIds.Douyin ? null :
                    ct => RunPagePassAsync(pageUrl, pageTitle, ct, generation, runExternal: true),
                hasUsableResult: Ready,
                shouldStop: Stop,
                externalDelay: TimeSpan.FromMilliseconds(preferExternal ? 0 : 700),
                budget: TimeSpan.FromSeconds(isAlbum ? 35 : siteId == SiteIds.YouTube ? 100 : 20),
                enrichFormats: false,
                ct: token);
            if (!Current()) return;

            // Seal once, after the external process has completed or been cancelled and joined.
            // All entry points publish only after extraction cleanup and sealing finish.
            using (var sealing = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                sealing.CancelAfter(TimeSpan.FromSeconds(2));
                try { await _pipeline.CompleteDiscoveryAsync(sealing.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    HangProbe.Mark("vm.completeDiscovery.budget", "late validations may still enrich this session");
                }
            }
            if (!Current()) return;
            if (siteId == SiteIds.Douyin && !Ready() &&
                await TryBoostDouyinDetailAsync(pageUrl, token)) return;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (generation != _pageGeneration || token.IsCancellationRequested)
                    return;
                foreach (var video in results.Complete())
                    if (IsVideoRelevantToCurrentPage(video)) UpsertDetectedVideo(video, focus: false);
                if (SelectedDetectedVideo is null && DetectedVideos.Count > 0)
                    FocusLargestVideoVariant(DetectedVideos[0]);
                _forceReplaceResults = false;
                if (DetectedVideos.Count > 0)
                {
                    SetStatusKey("status.probeDone", DetectedVideos.Count);
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
            });
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
                () => { if (generation == _pageGeneration) SetStatusKey("status.probeFailed", ex.Message); },
                System.Windows.Threading.DispatcherPriority.Background);
        }
        finally
        {
            results.Cancel();
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
                            await tab.Host.RefreshContextSnapshotAsync(token, forceCookies: true);
                        HangProbe.Mark("vm.pagePass.probe1.begin");
                        await tab.Host.ProbeCurrentPageAsync(token);
                        HangProbe.Mark("vm.pagePass.probe1.end");
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
    private void ToggleAutoMode()
    {
        if (IsAutoMode)
        {
            StopAutoMode(announce: true);
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
        IsAutoMode = true;
        SetStatusKey("status.autoStarted", FormatAutoStartedLimits());
        _ = RunAutoModeLoopAsync(cts.Token);
    }

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

    public void StopAutoMode(bool announce)
    {
        if (!IsAutoMode && _autoCts is null)
            return;

        try { _autoCts?.Cancel(); }
        catch { /* ignore */ }
        _autoCts?.Dispose();
        _autoCts = null;
        IsAutoMode = false;
        _autoLastEnqueuedFingerprint = null;
        _autoFailedFingerprint = null;
        _autoProcessedCount = 0;
        if (announce)
            SetStatusKey("status.autoStopped");
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
            _detectionResults?.Cancel();
            _detectionRunning = false;
        }
        DetectedVideos.Clear();
        _videoMap.Clear();
        SelectedDetectedVideo = null;
        HardResetDetectionPipeline();
        tab.Host.ResetDetectionSession();
    }

    public async Task SwitchToNextVideoAsync(CancellationToken ct = default)
    {
        var tab = SelectedTab;
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
            // Page may reject synthetic keys; ignore for manual ↓.
        }
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
                    await EndAutoModeTimeoutAsync();
                    break;
                }

                if (probe.Identity is not null && CaptureAutoPlaybackIdentity() != probe.Identity)
                    continue;

                if (probe.SkipLive || probe.AdvanceNoAddress)
                {
                    if (probe.SkipLive)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                            SetStatusKey("status.autoSkipLive"));
                    }

                    if (!await AdvanceFeedAsync(sessionEnd, token, previousWasLive: probe.SkipLive))
                    {
                        await EndAutoModeAfterAdvanceFailAsync();
                        break;
                    }

                    continue;
                }

                if (probe.JobId is null)
                {
                    if (IsAutoTimeExpired())
                    {
                        await EndAutoModeTimeoutAsync();
                        break;
                    }
                    continue;
                }

                // The per-item gate returns a job only after transfer started and five seconds elapsed.
                _autoProcessedCount++;
                await Application.Current.Dispatcher.InvokeAsync(RefreshAutoRunningStatus);
                if (IsAutoCountReached())
                {
                    await EndAutoModeCountAsync();
                    break;
                }

                if (!await AdvanceFeedAsync(sessionEnd, token))
                {
                    await EndAutoModeAfterAdvanceFailAsync();
                    break;
                }
            }

            if (IsAutoMode && !token.IsCancellationRequested && IsAutoLimitReached())
                await EndAutoModeByLimitAsync();
        }
        catch (OperationCanceledException)
        {
            // normal stop
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                StopAutoMode(announce: false);
                SetStatusKey("status.probeFailed", ex.Message);
            });
        }
        finally
        {
            if (IsAutoMode && (token.IsCancellationRequested || _autoCts?.Token == token))
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (_autoCts?.Token == token)
                    {
                        IsAutoMode = false;
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
        await SwitchToNextVideoAsync(token);
        return await WaitForContentSwitchAsync(beforeIdentity, previousWasLive, sessionEndUtc, token);
    }

    private async Task<bool> IsCurrentPlaybackLiveAsync(CancellationToken token)
    {
        var tab = SelectedTab;
        if (tab?.Host is null || !tab.IsInitialized)
            return false;

        try
        {
            if (await tab.Host.IsLivePlaybackAsync(token))
                return true;
        }
        catch
        {
            // fall through to URL / detected-variant heuristics
        }

        var page = tab.Host.CurrentPageUrl
                   ?? (Uri.TryCreate(AddressBar, UriKind.Absolute, out var uri) ? uri : null);
        if (page is not null && WebView2Host.LooksLikeLivePageUrl(page))
            return true;

        // Detected ladder is only live FLV/HLS pull — treat as live card.
        DetectedVideoViewModel? first = null;
        await Application.Current.Dispatcher.InvokeAsync(() => first = FindAutoDownloadCandidate());
        if (first?.Video.IsLive == true)
            return true;
        if (first?.Video.Variants.Any(VideoDownloader.Infrastructure.Detection.Sites.YouTube.YouTubeLiveMedia.IsLiveVariant) == true)
            return true;
        if (first?.Video.Variants is { Count: > 0 } variants &&
            variants.All(v => UnifiedMediaPipeline.IsDouyinLiveStream(v.SourceUrl)))
            return true;

        return false;
    }

    private Task EndAutoModeTimeoutAsync() =>
        EndAutoModeWithStatusAsync("status.autoStoppedTimeout");

    private Task EndAutoModeCountAsync() =>
        EndAutoModeWithStatusAsync("status.autoStoppedCount");

    private Task EndAutoModeByLimitAsync() =>
        IsAutoCountReached() ? EndAutoModeCountAsync() : EndAutoModeTimeoutAsync();

    private Task EndAutoModeAfterAdvanceFailAsync() =>
        IsAutoTimeExpired() ? EndAutoModeTimeoutAsync() : EndAutoModeWithStatusAsync("status.autoFeedStuck");

    private Task EndAutoModeWithStatusAsync(string statusKey) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            IsAutoMode = false;
            _autoCts?.Dispose();
            _autoCts = null;
            _autoFailedFingerprint = null;
            SetStatusKey(statusKey);
        }).Task;

    private static int RemainingSessionMinutes(DateTime sessionEndUtc)
    {
        if (sessionEndUtc >= DateTime.MaxValue - TimeSpan.FromDays(1))
            return 0;
        return Math.Max(0, (int)Math.Ceiling((sessionEndUtc - DateTime.UtcNow).TotalMinutes));
    }

    /// <summary>
    /// Waits for the first detected card from the normal detection pipeline (nav/SPA/session),
    /// then applies download. Does not call Refresh/RestartDetection — auto must not re-orchestrate probe.
    /// </summary>
    private async Task<AutoProbeResult> TryAutoEnqueueUntilAsync(DateTime sessionEndUtc, CancellationToken token)
    {
        var gate = new AutoModeAdvanceGate(_autoItemStartedAt);
        var itemIdentity = CaptureAutoPlaybackIdentity();
        Guid? enqueuedJobId = null;
        var wasAlreadyCompleted = false;
        var waitedForProbe = false;

        while (!token.IsCancellationRequested && DateTime.UtcNow < sessionEndUtc)
        {
            var observedIdentity = CaptureAutoPlaybackIdentity();
            if (observedIdentity is null)
            {
                await Task.Delay(200, token);
                continue;
            }
            if (!string.IsNullOrEmpty(observedIdentity) && observedIdentity != itemIdentity)
            {
                // Natural playlist advancement must not inherit the previous video's switch timer.
                itemIdentity = observedIdentity;
                _autoItemStartedAt = DateTimeOffset.UtcNow;
                gate = new AutoModeAdvanceGate(_autoItemStartedAt);
                enqueuedJobId = null;
                wasAlreadyCompleted = false;
            }
            // Automatic playback consumes the common probe's final result; its own address
            // deadline must never cancel/skip a still-running manual/navigation probe.
            if (_detectionRunning)
            {
                waitedForProbe = true;
                await Task.Delay(200, token);
                continue;
            }
            if (waitedForProbe)
            {
                waitedForProbe = false;
                if (DetectedVideos.Count == 0)
                    return new AutoProbeResult(null, SkipLive: false, AdvanceNoAddress: true, Identity: itemIdentity);
                if (enqueuedJobId is null)
                {
                    _autoItemStartedAt = DateTimeOffset.UtcNow;
                    gate = new AutoModeAdvanceGate(_autoItemStartedAt);
                }
            }
            var live = await IsCurrentPlaybackLiveAsync(token);
            DetectedVideoViewModel? candidate = null;
            await Application.Current.Dispatcher.InvokeAsync(() => candidate = FindAutoDownloadCandidate());
            live |= candidate?.Video.IsLive == true;
            live |= candidate?.Video.Variants.Any(VideoDownloader.Infrastructure.Detection.Sites.YouTube.YouTubeLiveMedia.IsLiveVariant) == true;
            if (candidate?.Video.Variants is { Count: > 0 } variants &&
                variants.All(v => UnifiedMediaPipeline.IsDouyinLiveStream(v.SourceUrl)))
                live = true;
            var job = enqueuedJobId is Guid queued
                ? _downloadEngine.GetActiveJobs().FirstOrDefault(j => j.Id == queued)
                : null;
            var waitingForSlot = !wasAlreadyCompleted &&
                (job?.Status == DownloadStatus.Pending ||
                 enqueuedJobId is null && _downloadEngine.FreeDownloadSlots <= 0);
            var reason = gate.Observe(DateTimeOffset.UtcNow, live, job?.Status == DownloadStatus.Downloading && job.DownloadedBytes == 0
                ? DownloadStatus.Preparing : job?.Status, wasAlreadyCompleted, waitingForSlot);
            if (reason == AutoModeAdvanceReason.Live)
                return new AutoProbeResult(null, SkipLive: true, Identity: itemIdentity);
            if (reason == AutoModeAdvanceReason.AddressTimeout)
                return new AutoProbeResult(null, SkipLive: false, AdvanceNoAddress: true, Identity: itemIdentity);
            if (reason == AutoModeAdvanceReason.DownloadStarted)
                return new AutoProbeResult(enqueuedJobId, SkipLive: false, Identity: itemIdentity);

            if (gate.DownloadStartedAt is { } started)
            {
                var seconds = Math.Max(1, (int)Math.Ceiling(
                    (started + AutoModeAdvanceGate.DownloadSwitchDelay - DateTimeOffset.UtcNow).TotalSeconds));
                await Application.Current.Dispatcher.InvokeAsync(() => SetStatusKey("status.autoDelaySwitch", seconds));
            }
            else if (enqueuedJobId is null && _downloadEngine.FreeDownloadSlots > 0)
            {
                enqueuedJobId = await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    // A queued UI callback from a stopped session must not apply a download.
                    if (token.IsCancellationRequested || _autoCts?.Token != token || !IsAutoMode ||
                        DateTimeOffset.UtcNow >= gate.AddressDeadline)
                        return (Guid?)null;

                    var current = FindAutoDownloadCandidate();
                    if (!IsAutoCandidateDownloadable(current) ||
                        (ExtractStableContentKey(null, current!.Video.PageUrl) is not null &&
                         BuildAutoContentFingerprint(current!) != CaptureAutoPlaybackIdentity()))
                        return null;
                    // Automatic downloads always use the best video, independently of a manual dropdown choice.
                    var automaticVariant = MediaVariantRanking.SelectPreferredVideo(current!.Video.Variants);
                    if (automaticVariant is null) return null;
                    var fingerprint = BuildAutoContentFingerprint(current!);
                    var existing = _downloadEngine.GetActiveJobs().FirstOrDefault(j =>
                        ExtractStableContentKey(null, j.PageUrl) is { } existingKey && existingKey == fingerprint &&
                        (j.Variant.Height ?? 0) >= (automaticVariant.Height ?? 0) &&
                        (j.Status is DownloadStatus.Pending or DownloadStatus.Preparing or DownloadStatus.Downloading or DownloadStatus.Muxing ||
                         j.Status == DownloadStatus.Completed && System.IO.File.Exists(j.TargetPath) && new System.IO.FileInfo(j.TargetPath).Length > 0));
                    if (existing is not null)
                    {
                        wasAlreadyCompleted = false;
                        return (Guid?)existing.Id;
                    }
                    if (!string.IsNullOrEmpty(fingerprint) &&
                        string.Equals(fingerprint, _autoFailedFingerprint, StringComparison.Ordinal))
                        return null;

                    // An address is applied immediately. The five-second delay belongs AFTER transfer starts.
                    var id = await TryStartDownloadQuietAsync(current!.Video, automaticVariant,
                        cancellationToken: token);
                    if (token.IsCancellationRequested || _autoCts?.Token != token)
                        return null;
                    if (id is not null)
                    {
                        wasAlreadyCompleted = false;
                        _autoLastEnqueuedFingerprint = fingerprint;
                        _autoFailedFingerprint = null;
                    }
                    else
                    {
                        _autoFailedFingerprint = fingerprint;
                    }
                    return id;
                }).Task.Unwrap();
            }

            if (gate.DownloadStartedAt is null)
            {
                if (enqueuedJobId is not null || _downloadEngine.FreeDownloadSlots <= 0)
                    await Application.Current.Dispatcher.InvokeAsync(RefreshAutoWaitingSlotStatus);
                else
                {
                    var seconds = Math.Max(1, (int)Math.Ceiling((gate.AddressDeadline - DateTimeOffset.UtcNow).TotalSeconds));
                    await Application.Current.Dispatcher.InvokeAsync(() => SetStatusKey("status.autoNoAddress", seconds));
                }
            }
            await Task.Delay(200, token);
        }
        return new AutoProbeResult(null, SkipLive: false);
    }

    private readonly record struct AutoProbeResult(Guid? JobId, bool SkipLive, bool AdvanceNoAddress = false, string? Identity = null);

    private async Task<bool> WaitForContentSwitchAsync(string? beforeIdentity, bool previousWasLive,
        DateTime sessionEndUtc, CancellationToken token)
    {
        var deadline = _autoItemStartedAt + AutoModeAdvanceGate.AddressTimeout;
        while (!token.IsCancellationRequested && DateTime.UtcNow < sessionEndUtc && DateTimeOffset.UtcNow < deadline)
        {
            var now = CaptureAutoPlaybackIdentity();
            // Read the current player, not the previous detected-card list. Live previews do not
            // necessarily create a new VOD session key, so waiting only for that key hides live checks.
            var currentIsLive = SelectedTab?.Host is { } currentHost &&
                await currentHost.IsLivePlaybackAsync(token);
            if (AutoModeAdvanceGate.HasPlaybackChanged(beforeIdentity, now, previousWasLive, currentIsLive))
                return true;

            // A slow detector is not permission to send another Next key. Only the 60-second gate may retry.
            var seconds = Math.Max(1, (int)Math.Ceiling((deadline - DateTimeOffset.UtcNow).TotalSeconds));
            await Application.Current.Dispatcher.InvokeAsync(() => SetStatusKey("status.autoNoAddress", seconds));
            await Task.Delay(200, token);
        }
        // If the page did not change, the next probe observes this SAME expired item deadline and advances once.
        return !token.IsCancellationRequested && DateTime.UtcNow < sessionEndUtc;
    }

    private string? CaptureAutoPlaybackIdentity()
    {
        var tab = SelectedTab;
        var key = tab?.Host.CurrentMediaSessionKey;
        var pageKey = ExtractStableContentKey(null, tab?.Host.CurrentPageUrl);
        var playerKey = ExtractStableContentKey(key, null);
        // During SPA navigation the URL changes before the player. Do not call this a confirmed switch.
        if (pageKey is not null && playerKey is not null && pageKey != playerKey)
            return null;
        return playerKey ?? pageKey ?? key ??
            (FindAutoDownloadCandidate() is { } candidate ? BuildAutoContentFingerprint(candidate) : null);
    }
    private static string? BuildAutoContentFingerprint(DetectedVideoViewModel item) =>
        ExtractStableContentKey(null, item.Video.PageUrl) ??
        BuildAutoContentFingerprint(item.Video.PageUrl.AbsoluteUri, item.Video.DisplayTitle);

    private static string? BuildAutoContentFingerprint(string? pageUrl, string? caption)
    {
        if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) && ExtractStableContentKey(null, page) is { } stable)
            return stable;
        var url = (pageUrl ?? string.Empty).Trim();
        var text = NormalizeAutoCaption(caption);
        return url.Length == 0 && text.Length == 0 ? null : url + "\n" + text;
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

    /// <summary>Always the first detected card (list order), not the UI selection.</summary>
    private DetectedVideoViewModel? FindAutoDownloadCandidate() =>
        DetectedVideos.Count > 0 ? DetectedVideos[0] : null;

    private static bool IsAutoCandidateDownloadable(DetectedVideoViewModel? item) =>
        item is { IsDrm: false, Video.IsLive: false, Video.IsFormatDiscoveryPending: false, SelectedVariant.Variant: not null } &&
        !UnifiedMediaPipeline.IsDouyinLiveStream(item.SelectedVariant.Variant.SourceUrl);

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
        SelectedDetectedVideo is { IsDrm: false, SelectedVariant: not null };

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

    public void TickDownloads()
    {
        var jobs = _downloadEngine.GetActiveJobs();
        var orderChanged = DownloadJobs.Count != jobs.Count ||
                           !DownloadJobs.Select(vm => vm.Job.Id).SequenceEqual(jobs.Select(j => j.Id));
        if (orderChanged)
        {
            RefreshDownloadJobs();
            return;
        }

        foreach (var job in DownloadJobs)
            job.Refresh();
        NotifyQueueCommands();
    }

    [RelayCommand]
    private void ToggleQueueGrouping()
    {
        _queueGrouped = !_queueGrouped;
        _options.Ui.QueueGrouped = _queueGrouped;
        _settingsStore.Save(_options);
        OnPropertyChanged(nameof(IsQueueGrouped));
        OnPropertyChanged(nameof(QueueGroupToggleLabel));
        RebuildQueueRows();
        if (_selectedDownloadJobIds.Count > 0)
            RestoreQueueSelection?.Invoke(_selectedDownloadJobIds);
        NotifyQueueCommands();
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

    [RelayCommand(CanExecute = nameof(CanPauseSelected))]
    private async Task PauseSelectedAsync()
    {
        var targets = EffectiveSelectedJobs.ToArray();
        if (targets.Length == 0) return;
        foreach (var vm in targets)
            await _downloadEngine.PauseAsync(vm.Job.Id);
        RefreshDownloadJobs();
    }

    [RelayCommand(CanExecute = nameof(CanResumeSelected))]
    private async Task ResumeSelectedAsync()
    {
        var targets = EffectiveSelectedJobs.ToArray();
        if (targets.Length == 0) return;
        foreach (var vm in targets)
            await _downloadEngine.ResumeAsync(vm.Job.Id);
        RefreshDownloadJobs();
    }

    [RelayCommand(CanExecute = nameof(CanCancelSelected))]
    private async Task CancelSelectedAsync()
    {
        var targets = EffectiveSelectedJobs.ToArray();
        if (targets.Length == 0) return;
        foreach (var vm in targets)
            await _downloadEngine.CancelAsync(vm.Job.Id);
        SetStatusKey("status.cancelled");
        RefreshDownloadJobs();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    private async Task RemoveSelectedAsync()
    {
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

    private void OnVideoDetected(object? sender, DetectedVideo video)
    {
        // Transport/DOM events stage only. The shared session exit is the sole UI publisher.
        if (video.SessionId != _pipeline.SessionId || !IsVideoRelevantToCurrentPage(video)) return;
        _detectionResults?.Stage(video);
    }

    private void OnPageProbed(object? sender, IReadOnlyList<DetectedVideo> videos)
    {
        foreach (var video in videos) OnVideoDetected(sender, video);
    }

    private void UpsertDetectedVideo(DetectedVideo video, bool focus)
    {
        if (video.IsFormatDiscoveryPending) return;
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
    private static bool IsExclusiveHost(Uri pageUrl)
    {
        var host = pageUrl.Host;
        return host.Contains("douyin.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("iesdouyin.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
               host.Contains("b23.tv", StringComparison.OrdinalIgnoreCase);
    }

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
            candidate.IsFormatDiscoveryPending != current.IsFormatDiscoveryPending ||
            candidate.Variants.Count != current.Variants.Count)
            return false;

        static string Key(MediaVariant variant) => $"{variant.Width}:{variant.Height}:" + string.Join("|", variant.Tracks
            .Select(track => $"{track.Kind}:{track.SourceUrl.AbsoluteUri}:{track.ContentLength}:{track.Bandwidth}"));

        return candidate.Variants.Select(Key).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(current.Variants.Select(Key).OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
    }

    private void OnTabPageIdentityChanged(BrowserTabViewModel tab, PageIdentityChangedEventArgs e)
    {
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            HangProbe.Mark("vm.pageIdentity", $"{e.PageUrl.Host}{e.PageUrl.AbsolutePath}");
            tab.Address = e.PageUrl.AbsoluteUri;
            if (!string.IsNullOrWhiteSpace(e.PageTitle))
                tab.Title = e.PageTitle!;

            if (!ReferenceEquals(SelectedTab, tab))
                return;

            AddressBar = e.PageUrl.AbsoluteUri;
            RefreshSidePanelVisibility(e.PageUrl);

            // Soft document URL change (SPA) without NavigationStarting (modal_id etc.).
            var pageOnly = BuildPageIdentity(e.PageUrl);
            if (string.Equals(_lastNavigatedPageUrl, pageOnly, StringComparison.Ordinal) ||
                string.Equals(_currentPageIdentity, pageOnly, StringComparison.Ordinal))
                return;

            RestartDetectionForPageChange(
                e.PageUrl,
                e.PageTitle,
                mediaSessionKey: tab.Host.CurrentMediaSessionKey);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private bool IsVideoRelevantToCurrentPage(DetectedVideo video)
    {
        var current = _currentPageIdentity;
        if (string.IsNullOrWhiteSpace(current))
            return true;

        var currentPage = current.Split('\n')[0];
        var videoIdentity = BuildPageIdentity(video.PageUrl);
        var videoPage = videoIdentity.Split('\n')[0];
        var currentContent = ExtractStableContentKeyFromIdentity(current);
        var incomingContent = ExtractStableContentKeyFromIdentity(videoIdentity);
        if (currentContent is not null && incomingContent is not null)
            return string.Equals(currentContent, incomingContent, StringComparison.Ordinal);

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

        // Only id-less Douyin/TikTok feed roots may use a host fallback.
        // Concrete YouTube/Bilibili content mismatches must never cross video boundaries.
        if (Uri.TryCreate(currentPage, UriKind.Absolute, out var cur) &&
            (cur.Host.Contains("douyin.com", StringComparison.OrdinalIgnoreCase) ||
             cur.Host.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase) ||
             cur.Host.Contains("iesdouyin.com", StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(cur.Host, video.PageUrl.Host, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static string BuildPageIdentity(Uri pageUrl, string? mediaSessionKey = null)
    {
        var builder = new UriBuilder(pageUrl) { Fragment = string.Empty };
        var page = builder.Uri.AbsoluteUri.TrimEnd('/');
        var stable = ExtractStableContentKey(null, pageUrl) ?? ExtractStableContentKey(mediaSessionKey, null);
        if (!string.IsNullOrWhiteSpace(stable))
            return page + "\n" + stable;
        if (string.IsNullOrWhiteSpace(mediaSessionKey))
            return page;

        // This is a logical identity, not a transport URL to normalize.
        return page + "\n" + mediaSessionKey;
    }

    private static string? ExtractStableContentKey(string? mediaSessionKey, Uri? pageUrl)
    {
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
        if (pageUrl.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
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
