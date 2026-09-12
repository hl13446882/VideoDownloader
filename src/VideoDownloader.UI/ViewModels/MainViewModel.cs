using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Wpf;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Errors;
using VideoDownloader.Core.Models;
using VideoDownloader.Core.Naming;
using VideoDownloader.Infrastructure.Browser;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Detection;
using VideoDownloader.Infrastructure.Diagnostics;
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
    }

    public string Title => Video.DisplayTitle;
    public string SiteId => Video.SiteId;
    public string Family => Video.Family.ToString();
    public bool IsDrm => Video.IsDrmProtected;
    public bool CanDownload => !Video.IsDrmProtected && SelectedVariant is not null;

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

    partial void OnSelectedVariantChanged(VariantItemViewModel? value) =>
        OnPropertyChanged(nameof(CanDownload));

    partial void OnSelectedModeChanged(string? value) => ApplyModeFilter();

    public void ReplaceVariants(DetectedVideo video)
    {
        var previousMode = SelectedMode;
        var previousVariantId = SelectedVariant?.Variant.VariantId;
        var previousUrl = SelectedVariant?.Variant.SourceUrl.AbsoluteUri;

        var built = video.Variants
            .OrderBy(v => MediaVariantRanking.IsMseOrPartialVariant(v) ? 1 : 0)
            .ThenByDescending(v => v.TotalContentLength ?? v.Bandwidth ?? 0)
            .ThenByDescending(v => v.Height ?? 0)
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

        SelectedVariant =
            Variants.FirstOrDefault(v => v.Variant.VariantId == previousVariantId) ??
            Variants.FirstOrDefault(v => v.Variant.SourceUrl.AbsoluteUri == previousUrl) ??
            Variants.FirstOrDefault(v => !MediaVariantRanking.IsMseOrPartialVariant(v.Variant)) ??
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
    private string _address = "https://www.bing.com/";

    [ObservableProperty]
    private bool _isInitialized;

    public BrowserTabViewModel(WebView2Host host) => Host = host;
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
    private readonly HashSet<string> _collapsedQueueGroups = new(StringComparer.OrdinalIgnoreCase);
    private bool _queueGrouped;
    private readonly Dictionary<Guid, DetectedVideoViewModel> _videoMap = new();
    private readonly object _pageSync = new();
    private CancellationTokenSource _probeCts = new();
    private string? _currentPageIdentity;
    private string? _lastNavigatedPageUrl;
    private long _pageGeneration;
    private bool _detectionRunning;
    private bool _forceReplaceResults;
    private string? _statusKey;
    private object[]? _statusArgs;
    /// <summary>Douyin feed→detail boost already attempted for this aweme id (avoid loops).</summary>
    private string? _douyinDetailBoostId;

    public ObservableCollection<AddressPreset> AddressPresets { get; } = new();

    [ObservableProperty]
    private string _addressBar = "https://www.bing.com/";

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

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));

    public void SetStatus(string message)
    {
        _statusKey = null;
        _statusArgs = null;
        StatusMessage = message;
    }

    public void SetStatusKey(string key, params object[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        StatusMessage = args.Length == 0 ? _loc.T(key) : _loc.Format(key, args);
    }

    public void ClearStatus()
    {
        _statusKey = null;
        _statusArgs = null;
        if (!string.IsNullOrWhiteSpace(StatusMessage))
            StatusMessage = string.Empty;
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
        LocalizationService loc)
    {
        _services = services;
        _hostLocator = hostLocator;
        _pipeline = pipeline;
        _downloadEngine = downloadEngine;
        _aggregator = aggregator;
        _settingsViewModel = settingsViewModel;
        _loc = loc;
        _options = services.GetRequiredService<AppOptions>();
        _settingsStore = services.GetRequiredService<UserSettingsStore>();
        _queueGrouped = _options.Ui.QueueGrouped;

        _pipeline.VideoDetected += OnVideoDetected;
        _pipeline.PageProbed += OnPageProbed;
        _loc.LanguageChanged += OnLanguageChanged;
        RebuildAddressPresets();
    }

    private void RebuildAddressPresets()
    {
        AddressPresets.Clear();
        AddressPresets.Add(new AddressPreset(_loc.T("preset.google"), "https://www.google.com/"));
        AddressPresets.Add(new AddressPreset(_loc.T("preset.youtube"), "https://www.youtube.com/"));
        AddressPresets.Add(new AddressPreset(_loc.T("preset.douyin"), "https://www.douyin.com/"));
        AddressPresets.Add(new AddressPreset(_loc.T("preset.tiktok"), "https://www.tiktok.com/"));
        AddressPresets.Add(new AddressPreset(_loc.T("preset.bilibili"), "https://www.bilibili.com/"));
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
    }

    public async Task InitializeAsync()
    {
        RefreshDownloadJobs();
        ClearStatus();
        await AddTabAsync("https://www.bing.com/", select: true);
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
                    RestartDetectionForPageChange(page, null, mediaSessionKey: null);
            };
            tab.Host.MediaSessionChanged += (_, e) =>
            {
                if (!ReferenceEquals(SelectedTab, tab))
                    return;
                _ = Application.Current.Dispatcher.InvokeAsync(() =>
                {
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
                        mediaSessionKey: e.MediaSessionKey);
                }, System.Windows.Threading.DispatcherPriority.Background);
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
        if (oldValue is not null)
            oldValue.Host.CaptureEnabled = false;

        if (newValue is null)
        {
            CanGoBack = false;
            CanGoForward = false;
            return;
        }

        newValue.Host.CaptureEnabled = true;
        _hostLocator.Active = newValue.Host;
        AddressBar = newValue.Address;
        UpdateNavigationState(newValue);
        var page = newValue.Host.CurrentPageUrl
                   ?? (Uri.TryCreate(newValue.Address, UriKind.Absolute, out var uri) ? uri : null);
        if (page is not null)
            RestartDetectionForPageChange(page, newValue.Title, mediaSessionKey: newValue.Host.CurrentMediaSessionKey);
        else
            ClearDetectedVideos();
    }

    private void UpdateNavigationState(BrowserTabViewModel? tab)
    {
        CanGoBack = tab?.Host.CanGoBack == true;
        CanGoForward = tab?.Host.CanGoForward == true;
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
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
            OpenSelectedDownloadWithSystemPlayer();
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
        await AddTabAsync("https://www.bing.com/", select: true);
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
        if (select)
            SelectedTab = tab;
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task CloseTabAsync(BrowserTabViewModel? tab)
    {
        tab ??= SelectedTab;
        if (tab is null)
            return;

        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
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
            await AddTabAsync("https://www.bing.com/", select: true);
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

            if (tab.IsInitialized)
                await tab.Host.NavigateAsync(url);
            else
            {
                tab.PendingNavigationUrl = url;
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

        // Manual probe: singleton destroy + full re-probe (same entry as auto navigation/SPA/session).
        ClearStatus();
        SetStatusKey("status.probeManual");
        RestartDetectionForPageChange(
            pageUrl,
            tab?.Title,
            mediaSessionKey: tab?.Host.CurrentMediaSessionKey);
    }

    /// <summary>
    /// Unified singleton detection entry for every site: wipe UI, destroy the routed detector
    /// session, BeginSession once, fully re-probe. Manual Probe and automatic navigation /
    /// SPA / media-session switches all use this path.
    /// </summary>
    private void RestartDetectionForPageChange(
        Uri pageUrl,
        string? pageTitle,
        string? mediaSessionKey)
    {
        var enriched = EnrichPageUrlWithContentId(pageUrl, mediaSessionKey);
        StartPageDetectionSession(
            enriched,
            pageTitle,
            clearUi: true,
            forceReplace: true,
            mediaSessionKey: mediaSessionKey,
            resetMediaSession: true,
            forceFullRestart: true,
            destroySameContent: true);
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
    private void StartPageDetectionSession(
        Uri pageUrl,
        string? pageTitle,
        bool clearUi,
        bool forceReplace = false,
        string? mediaSessionKey = null,
        bool resetMediaSession = true,
        bool forceFullRestart = false,
        bool destroySameContent = false)
    {
        long generation;
        CancellationToken token;
        lock (_pageSync)
        {
            var pageKey = BuildPageIdentity(pageUrl, mediaSessionKey);
            var stableIncoming = ExtractStableContentKey(mediaSessionKey, pageUrl);
            var stableCurrent = ExtractStableContentKeyFromIdentity(_currentPageIdentity);
            // Same aweme already running: keep the only singleton session unless Probe / ForceRestart.
            if (!destroySameContent &&
                _detectionRunning &&
                stableIncoming is not null &&
                string.Equals(stableIncoming, stableCurrent, StringComparison.Ordinal))
            {
                return;
            }

            // Same document already being probed — ignore duplicate NavigationStarted/PageIdentity.
            if (!forceFullRestart &&
                clearUi &&
                !forceReplace &&
                _detectionRunning &&
                string.Equals(_currentPageIdentity, pageKey, StringComparison.Ordinal))
            {
                return;
            }

            _currentPageIdentity = pageKey;
            _lastNavigatedPageUrl = BuildPageIdentity(pageUrl);
            _forceReplaceResults = forceReplace || clearUi || forceFullRestart;
            _detectionRunning = true;
            _pageGeneration++;
            generation = _pageGeneration;
            _probeCts.Cancel();
            _probeCts.Dispose();
            _probeCts = new CancellationTokenSource();
            token = _probeCts.Token;
        }

        if (clearUi || forceFullRestart)
        {
            DetectedVideos.Clear();
            _videoMap.Clear();
            SelectedDetectedVideo = null;
        }

        // Fresh singleton entry: destroy previous detector session, BeginSession once.
        if (forceFullRestart)
        {
            _aggregator.Clear();
            if (_pipeline is RoutedMediaDetectionPipeline routed)
                routed.Reenter(pageUrl);
            else
                HardResetDetectionPipeline();
        }
        else
            ResetDetectionPipeline();

        if (resetMediaSession)
            SelectedTab?.Host.ResetDetectionSession();
        _ = RunStableDetectionSessionAsync(pageUrl, pageTitle, token, generation);
    }

    private async Task RunStableDetectionSessionAsync(
        Uri pageUrl,
        string? pageTitle,
        CancellationToken token,
        long generation)
    {
        try
        {
            HangProbe.Mark("vm.session.begin", pageUrl.AbsoluteUri);
            await Application.Current.Dispatcher.InvokeAsync(() => SetStatusKey("status.probeWaiting"));

            // Let CDP/WebResource capture accumulate before the single page pass.
            // Feed players (blob + delayed playAddr) often need longer than a short settle.
            // Douyin /note/ albums need extra time to click-through slides (e.g. 4/17).
            var isDouyinNote = pageUrl.Host.Contains("douyin", StringComparison.OrdinalIgnoreCase) &&
                               pageUrl.AbsolutePath.Contains("/note/", StringComparison.OrdinalIgnoreCase);
            var isDouyin = pageUrl.Host.Contains("douyin", StringComparison.OrdinalIgnoreCase) ||
                           pageUrl.Host.Contains("iesdouyin", StringComparison.OrdinalIgnoreCase);
            var isExclusiveHost = IsExclusiveHost(pageUrl);
            var isYouTubeWatch =
                (pageUrl.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) &&
                 pageUrl.AbsolutePath.Contains("/watch", StringComparison.OrdinalIgnoreCase) &&
                 !string.IsNullOrWhiteSpace(pageUrl.Query) &&
                 pageUrl.Query.Contains("v=", StringComparison.OrdinalIgnoreCase)) ||
                pageUrl.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
            var isYtDlpExclusive = isExclusiveHost &&
                                   !pageUrl.Host.Contains("douyin", StringComparison.OrdinalIgnoreCase);
            var siteId = isYouTubeWatch || pageUrl.Host.Contains("youtube", StringComparison.OrdinalIgnoreCase)
                ? SiteIds.YouTube
                : pageUrl.Host.Contains("bilibili", StringComparison.OrdinalIgnoreCase) || pageUrl.Host.Contains("b23.tv", StringComparison.OrdinalIgnoreCase)
                    ? SiteIds.Bilibili
                    : pageUrl.Host.Contains("tiktok", StringComparison.OrdinalIgnoreCase)
                        ? SiteIds.TikTok
                        : pageUrl.Host.Contains("douyin", StringComparison.OrdinalIgnoreCase)
                            ? SiteIds.Douyin
                            : SiteIds.Generic;
            var probeStats = _services.GetService<IProbeMethodStats>();
            var preferYtdlpFirst = probeStats?.PreferYtdlpFirst(siteId) == true
                                   || isYouTubeWatch; // YouTube watch default until stats say otherwise

            // Highest-probability schedule first: yt-dlp-first vs network/grace-first.
            if (preferYtdlpFirst && isYtDlpExclusive)
            {
                HangProbe.Mark("vm.schedule", $"{siteId} {ProbeMethods.ScheduleYtdlpFirst}");
                var ytdlpRounds = siteId == SiteIds.YouTube ? 2 : 1;
                for (var round = 1; round <= ytdlpRounds; round++)
                {
                    if (generation != _pageGeneration || token.IsCancellationRequested)
                        return;

                    if (round > 1)
                    {
                        HangProbe.Mark("vm.youtube.retryRound", $"round={round}");
                        await Application.Current.Dispatcher.InvokeAsync(() => SetStatusKey("status.probeRunning"));
                        if (_pipeline is RoutedMediaDetectionPipeline routedRetry)
                            routedRetry.Reenter(pageUrl);
                        await Task.Delay(TimeSpan.FromSeconds(1.2), token);
                        if (generation != _pageGeneration || token.IsCancellationRequested)
                            return;
                    }
                    else
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(200), token);
                        if (generation != _pageGeneration)
                            return;
                        await Application.Current.Dispatcher.InvokeAsync(() => SetStatusKey("status.probeRunning"));
                    }

                    HangProbe.Mark("vm.pagePass.begin", $"round={round} {pageUrl.AbsoluteUri}");
                    await RunPagePassAsync(pageUrl, pageTitle, token, generation, runExternal: true);
                    HangProbe.Mark("vm.pagePass.end", $"round={round} videos={DetectedVideos.Count}");

                    for (var late = 0; late < 2; late++)
                    {
                        var have = false;
                        await Application.Current.Dispatcher.InvokeAsync(() => have = DetectedVideos.Count > 0);
                        if (have || generation != _pageGeneration || token.IsCancellationRequested)
                            break;
                        HangProbe.Mark("vm.lateRetry.begin", $"round={round} i={late}");
                        await Task.Delay(TimeSpan.FromMilliseconds(siteId == SiteIds.YouTube ? 1200 : 800), token);
                        if (generation != _pageGeneration || token.IsCancellationRequested)
                            break;
                        // YouTube: allow yt-dlp again on late pass (slot unlocked after cancel/empty).
                        await RunPagePassAsync(
                            pageUrl,
                            pageTitle,
                            token,
                            generation,
                            runExternal: siteId == SiteIds.YouTube);
                        HangProbe.Mark("vm.lateRetry.end", $"round={round} i={late} videos={DetectedVideos.Count}");
                    }

                    var foundRound = false;
                    await Application.Current.Dispatcher.InvokeAsync(() => foundRound = DetectedVideos.Count > 0);
                    if (foundRound)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            if (generation == _pageGeneration)
                                SetStatusKey("status.probeDone", DetectedVideos.Count);
                        });
                        HangProbe.Mark("vm.session.end", $"round={round} videos={DetectedVideos.Count}");
                        return;
                    }

                    // Round 1 empty: do not declare failure — start another full probe round.
                    if (round < ytdlpRounds)
                    {
                        HangProbe.Mark("vm.youtube.retryRound.pending", "first-round-empty");
                        continue;
                    }

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (generation != _pageGeneration)
                            return;
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
                    HangProbe.Mark("vm.session.end", $"round={round} videos=0");
                    return;
                }

                return;
            }

            if (isYtDlpExclusive)
                HangProbe.Mark("vm.schedule", $"{siteId} {ProbeMethods.ScheduleNetworkFirst}");

            // When stats prefer network-first (or non-yt-dlp exclusive), run grace then page-pass.

            // REDUNDANT(pending-delete after confirm): site-agnostic settle paid by exclusive hosts.
            // await Task.Delay(isDouyinNote ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(3), token);
            var settle = isDouyinNote ? TimeSpan.FromSeconds(5)
                : isDouyin ? TimeSpan.FromSeconds(2)
                : isYtDlpExclusive ? TimeSpan.FromMilliseconds(400)
                : isExclusiveHost ? TimeSpan.FromSeconds(1.2)
                : TimeSpan.FromSeconds(3);
            await Task.Delay(settle, token);
            if (generation != _pageGeneration)
            {
                HangProbe.Mark("vm.session.stale.afterDelay", $"gen={generation}/{_pageGeneration}");
                return;
            }

            var host = SelectedTab?.Host;
            if (host is not null)
            {
                // REDUNDANT(pending-delete after confirm): graceLimit = isDouyinNote ? 10 : 6;
                var graceLimit = isDouyinNote ? 10
                    : isDouyin ? 8
                    : isYtDlpExclusive ? 2
                    : isExclusiveHost ? 4
                    : 6;
                // REDUNDANT(pending-delete after confirm): graceGap always 1.5s
                var graceGap = isYtDlpExclusive ? TimeSpan.FromMilliseconds(600)
                    : isDouyin ? TimeSpan.FromSeconds(1.2)
                    : TimeSpan.FromSeconds(1.5);
                for (var grace = 0; grace < graceLimit; grace++)
                {
                    if (generation != _pageGeneration || token.IsCancellationRequested)
                    {
                        HangProbe.Mark("vm.grace.abort", $"i={grace}");
                        return;
                    }
                    try
                    {
                        HangProbe.Mark("vm.grace.probe.begin", $"i={grace}");
                        await host.ProbeCurrentPageAsync(token);
                        HangProbe.Mark("vm.grace.probe.end", $"i={grace}");
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        HangProbe.Mark("vm.grace.probe.fail", $"i={grace} {ex.GetType().Name}");
                        // Probe warnings must not abort the session.
                    }

                    var found = false;
                    var videoDenied = false;
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        found = DetectedVideos.Count > 0;
                        var availability = SelectedDetectedVideo?.Video.Availability;
                        videoDenied = availability is MediaAvailabilityKind.AudioOnly
                            or MediaAvailabilityKind.VideoDenied;
                    });
                    HangProbe.Mark("vm.grace.check", $"i={grace} found={found} denied={videoDenied}");
                    // Audio-only / video-denied is not a finished VOD discovery — keep probing DOM/network.
                    if (found && !videoDenied)
                        break;

                    await Task.Delay(graceGap, token);
                }
            }

            await Application.Current.Dispatcher.InvokeAsync(() => SetStatusKey("status.probeRunning"));
            HangProbe.Mark("vm.pagePass.begin", pageUrl.AbsoluteUri);
            await RunPagePassAsync(pageUrl, pageTitle, token, generation, runExternal: true);
            HangProbe.Mark("vm.pagePass.end", $"videos={DetectedVideos.Count}");

            // Feed soft-nav often seals on MSE-only before progressive CDN arrives; give a few
            // short re-probe/complete cycles without starting a brand-new page generation.
            // REDUNDANT(pending-delete after confirm): lateLimit = isDouyinNote ? 8 : 4;
            var lateLimit = isDouyinNote ? 8
                : isDouyin ? 6
                : isYtDlpExclusive ? 1
                : isExclusiveHost ? 2
                : 4;
            for (var late = 0; late < lateLimit; late++)
            {
                var have = false;
                await Application.Current.Dispatcher.InvokeAsync(() => have = DetectedVideos.Count > 0);
                if (have || generation != _pageGeneration || token.IsCancellationRequested)
                    break;

                HangProbe.Mark("vm.lateRetry.begin", $"i={late}");
                // REDUNDANT(pending-delete after confirm): Delay(isDouyinNote ? 2.5 : 2)
                await Task.Delay(
                    isDouyinNote ? TimeSpan.FromSeconds(2.5)
                    : isDouyin ? TimeSpan.FromSeconds(2)
                    : isYtDlpExclusive ? TimeSpan.FromMilliseconds(800)
                    : TimeSpan.FromSeconds(2),
                    token);
                if (generation != _pageGeneration || token.IsCancellationRequested)
                    break;

                var hostLate = SelectedTab?.Host;
                if (hostLate is not null)
                {
                    try { await hostLate.ProbeCurrentPageAsync(token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                    catch { /* enrichment */ }
                }

                await RunPagePassAsync(pageUrl, pageTitle, token, generation, runExternal: false);
                HangProbe.Mark("vm.lateRetry.end", $"i={late} videos={DetectedVideos.Count}");
            }

            // Douyin feed often only has unverified web-prime; open the stable detail page once
            // so network/zjcdn progressive can be collected (navigation starts a fresh session).
            if (isDouyin && generation == _pageGeneration && !token.IsCancellationRequested)
            {
                var haveFinal = false;
                await Application.Current.Dispatcher.InvokeAsync(() => haveFinal = DetectedVideos.Count > 0);
                if (!haveFinal && await TryBoostDouyinDetailAsync(pageUrl, token))
                {
                    HangProbe.Mark("vm.session.end", "douyin-detail-boost");
                    return;
                }
            }

            // YouTube network-first: first empty must not declare failure — full second probe round.
            if (siteId == SiteIds.YouTube && generation == _pageGeneration && !token.IsCancellationRequested)
            {
                var haveYt = false;
                await Application.Current.Dispatcher.InvokeAsync(() => haveYt = DetectedVideos.Count > 0);
                if (!haveYt)
                {
                    HangProbe.Mark("vm.youtube.retryRound", "network-first-round=2");
                    await Application.Current.Dispatcher.InvokeAsync(() => SetStatusKey("status.probeRunning"));
                    if (_pipeline is RoutedMediaDetectionPipeline routedYt)
                        routedYt.Reenter(pageUrl);
                    await Task.Delay(TimeSpan.FromSeconds(1.2), token);
                    if (generation != _pageGeneration || token.IsCancellationRequested)
                        return;
                    await RunPagePassAsync(pageUrl, pageTitle, token, generation, runExternal: true);
                    for (var late = 0; late < 2; late++)
                    {
                        var haveLate = false;
                        await Application.Current.Dispatcher.InvokeAsync(() => haveLate = DetectedVideos.Count > 0);
                        if (haveLate || generation != _pageGeneration || token.IsCancellationRequested)
                            break;
                        await Task.Delay(TimeSpan.FromMilliseconds(1200), token);
                        if (generation != _pageGeneration || token.IsCancellationRequested)
                            break;
                        await RunPagePassAsync(pageUrl, pageTitle, token, generation, runExternal: true);
                    }
                }
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (generation != _pageGeneration)
                    return;
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
                () => SetStatusKey("status.probeFailed", ex.Message),
                System.Windows.Threading.DispatcherPriority.Background);
        }
        finally
        {
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
        await Application.Current.Dispatcher.InvokeAsync(() => SetStatusKey("status.probeRunning"));
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
        if (generation == _pageGeneration)
        {
            HangProbe.Mark("vm.completeDiscovery.begin");
            await _pipeline.CompleteDiscoveryAsync(token);
            HangProbe.Mark("vm.completeDiscovery.end", $"completed={_pipeline.IsCompleted}");
        }
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

    public async Task StartDownloadAsync(DetectedVideo video, MediaVariant variant)
    {
        if (video.IsDrmProtected)
            return;

        var name = DownloadFileNameBuilder.Build(video, variant);
        try
        {
            var id = await _downloadEngine.EnqueueAsync(
                variant,
                name,
                video.PageUrl,
                video.DisplayTitle,
                video.DurationSec,
                video.Variants);
            _collapsedQueueGroups.Remove(DownloadSiteFolder.Resolve(video.PageUrl));
            RefreshDownloadJobs();
            var created = DownloadJobs.FirstOrDefault(j => j.Job.Id == id);
            if (created is not null)
                SelectOnlyQueueJob(created);
            SetStatusKey("status.enqueued", name);
        }
        catch (DownloadException ex)
        {
            var reason = _loc.T("error." + ex.ErrorCode);
            if (string.Equals(reason, "error." + ex.ErrorCode, StringComparison.Ordinal))
                reason = ex.Message;
            SetStatusKey("status.enqueueFailed", reason);
            MessageBox.Show(reason, _loc.DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
    private void PlaySelectedDownload() => OpenSelectedDownloadWithSystemPlayer();

    [RelayCommand]
    private void OpenSelectedDownload() => OpenSelectedDownloadWithSystemPlayer();

    private void OpenSelectedDownloadWithSystemPlayer()
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

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = job.TargetPath,
                UseShellExecute = true
            });
            SetStatusKey("status.played", Path.GetFileName(job.TargetPath));
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
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (video.SessionId != Guid.Empty && video.SessionId != _pipeline.SessionId) return;
            if (!IsVideoRelevantToCurrentPage(video))
                return;

            // Ignore empty publishes — they must not wipe a sticky last-good result.
            if (video.Variants.Count == 0)
                return;

            UpsertDetectedVideo(video, focus: SelectedDetectedVideo is null);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnPageProbed(object? sender, IReadOnlyList<DetectedVideo> videos)
    {
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var relevant = videos
                .Where(v => v.Variants.Count > 0)
                .Where(v => v.SessionId == Guid.Empty || v.SessionId == _pipeline.SessionId)
                .Where(IsVideoRelevantToCurrentPage)
                .ToArray();
            if (relevant.Length == 0)
                return;

            var pageKey = BuildPageIdentity(relevant[0].PageUrl);
            var keepIds = relevant.Select(v => v.VideoId).ToHashSet();

            // Drop stale cards for this page that are no longer in the final probe set.
            for (var i = DetectedVideos.Count - 1; i >= 0; i--)
            {
                var item = DetectedVideos[i];
                if (!string.Equals(BuildPageIdentity(item.Video.PageUrl), pageKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (keepIds.Contains(item.Video.VideoId))
                    continue;

                DetectedVideos.RemoveAt(i);
                _videoMap.Remove(item.Video.VideoId);
                if (ReferenceEquals(SelectedDetectedVideo, item))
                    SelectedDetectedVideo = null;
            }

            foreach (var video in relevant)
                UpsertDetectedVideo(video, focus: false);

            if (SelectedDetectedVideo is null && DetectedVideos.Count > 0)
                FocusLargestVideoVariant(DetectedVideos[0]);

            _forceReplaceResults = false;
            SetStatusKey(_pipeline.IsCompleted ? "status.probeDone" : "status.probeFound", DetectedVideos.Count);
        }, System.Windows.Threading.DispatcherPriority.Background);
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
            tab.Address = e.PageUrl.AbsoluteUri;
            if (!string.IsNullOrWhiteSpace(e.PageTitle))
                tab.Title = e.PageTitle!;

            if (!ReferenceEquals(SelectedTab, tab))
                return;

            AddressBar = e.PageUrl.AbsoluteUri;

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

        // Exclusive-site feed soft-nav: same host accepts the exclusive card.
        // Douyin/TikTok paths kept; YouTube/Bilibili added so content-keyed identities are not dropped.
        if (Uri.TryCreate(currentPage, UriKind.Absolute, out var cur) &&
            (cur.Host.Contains("douyin.com", StringComparison.OrdinalIgnoreCase) ||
             cur.Host.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase) ||
             cur.Host.Contains("iesdouyin.com", StringComparison.OrdinalIgnoreCase) ||
             cur.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
             cur.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase) ||
             cur.Host.Contains("bilibili.com", StringComparison.OrdinalIgnoreCase)) &&
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
