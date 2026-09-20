using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Core.Subtitles;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Logging;
using VideoDownloader.Infrastructure.Security;
using VideoDownloader.Infrastructure.Subtitles.Speech;
using VideoDownloader.Infrastructure.Subtitles.Translation;
using VideoDownloader.Infrastructure.Update;
using VideoDownloader.UI.Localization;
using VideoDownloader.UI.Subtitles;

namespace VideoDownloader.UI.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly int[] BottomOffsets = [20, 40, 60, 80, 100, 120, 140, 160];
    private static readonly int[] BackgroundOpacityLevels = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

    private readonly AppOptions _options;
    private readonly UserSettingsStore _store;
    private readonly AppLogService _appLog;
    private readonly LocalizationService _loc;
    private readonly IServiceProvider _services;

    [ObservableProperty] private string _savePath;
    [ObservableProperty] private int _maxConcurrent;
    [ObservableProperty] private string _retryCountText;
    [ObservableProperty] private string _failedRetryIntervalText;
    [ObservableProperty] private bool _autoRecover;
    [ObservableProperty] private bool _autoCheckForUpdates;
    [ObservableProperty] private bool _loggingEnabled;
    [ObservableProperty] private string _logLevel;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _checkUpdateEnabled = true;
    [ObservableProperty] private bool _migrateEnabled = true;

    [ObservableProperty] private bool _subtitleEnabled;
    [ObservableProperty] private SubtitleMode _subtitleMode;
    [ObservableProperty] private string _subtitleTranslationProvider;
    [ObservableProperty] private int _subtitlePreloadAheadSeconds;
    [ObservableProperty] private string _subtitleFontFamily;
    [ObservableProperty] private int _subtitleFontSize;
    [ObservableProperty] private bool _subtitleBold;
    [ObservableProperty] private string _subtitleTextColor;
    [ObservableProperty] private string _subtitleOutlineColor;
    [ObservableProperty] private int _subtitleOutlineSize;
    [ObservableProperty] private string _subtitleBackgroundColor;
    [ObservableProperty] private int _subtitleBackgroundOpacityLevel;
    [ObservableProperty] private int _subtitleBottomOffsetPx;
    [ObservableProperty] private int _subtitleMaxLines;
    [ObservableProperty] private int _subtitleMaxWidthPercent;
    [ObservableProperty] private int _subtitleOffsetMs;
    [ObservableProperty] private string _subtitleWhisperModelPath;
    [ObservableProperty] private string _subtitleLocalTranslationEndpoint;
    [ObservableProperty] private string _subtitleLocalTranslationModel;
    [ObservableProperty] private string _subtitleModelInstallStatus = string.Empty;
    [ObservableProperty] private bool _subtitleModelInstallEnabled = true;
    [ObservableProperty] private bool _subtitleLocalTranslationFieldsEnabled = true;

    public LocalizationService L => _loc;
    public string AppVersionText => "V" + AppVersionInfo.SemVer;
    public IReadOnlyList<int> ConcurrentOptions { get; } = [1, 2, 3, 4, 5];
    public IReadOnlyList<int> SubtitleBottomOffsetOptions { get; } = BottomOffsets;
    public IReadOnlyList<int> SubtitleBackgroundOpacityOptions { get; } = BackgroundOpacityLevels;

    public IReadOnlyList<NamedValue<SubtitleMode>> SubtitleModeOptions { get; } =
    [
        new(string.Empty, SubtitleMode.Original),
        new(string.Empty, SubtitleMode.Chinese),
        new(string.Empty, SubtitleMode.English),
        new(string.Empty, SubtitleMode.Bilingual)
    ];

    public IReadOnlyList<NamedValue<string>> SubtitleTranslationProviderOptions { get; } =
    [
        new(string.Empty, "local"),
        new(string.Empty, "cloud")
    ];

    public IReadOnlyList<NamedValue<string>> SubtitleColorOptions { get; } =
    [
        new(string.Empty, "#FF0000"),
        new(string.Empty, "#000000"),
        new(string.Empty, "#0000FF"),
        new(string.Empty, "#FFFF00"),
        new(string.Empty, "#00FF00")
    ];

    public IReadOnlyList<FontFamilyOption> SystemFontFamilies { get; }

    public event EventHandler? DownloadsMigrated;

    private string? _subtitleStatusKey;
    private object[] _subtitleStatusArgs = [];
    private CancellationTokenSource? _statusDismissCts;
    private int _statusGeneration;

    public SettingsViewModel(
        AppOptions options,
        UserSettingsStore store,
        AppLogService appLog,
        LocalizationService loc,
        IServiceProvider services)
    {
        _options = options;
        _store = store;
        _appLog = appLog;
        _loc = loc;
        _services = services;
        _savePath = options.Download.DefaultSavePath;
        _maxConcurrent = Math.Clamp(options.Download.MaxConcurrentDownloads, 1, 5);
        _retryCountText = options.Download.RetryCount.ToString();
        _failedRetryIntervalText = options.Download.FailedRetryIntervalSeconds.ToString();
        _autoRecover = options.Download.AutoRecoverDownloads;
        _autoCheckForUpdates = options.Ui.AutoCheckForUpdates;
        _loggingEnabled = options.Logging.Enabled;
        _logLevel = options.Logging.MinimumLevel;

        SystemFontFamilies = FontFamilyCatalog.Build();

        var subtitles = options.Subtitles;
        _subtitleEnabled = subtitles.Enabled;
        _subtitleMode = Enum.IsDefined(typeof(SubtitleMode), subtitles.Mode)
            ? subtitles.Mode
            : SubtitleMode.Chinese;
        _subtitleTranslationProvider = string.Equals(subtitles.TranslationProvider, "cloud", StringComparison.OrdinalIgnoreCase)
            ? "cloud"
            : "local";
        _subtitleLocalTranslationFieldsEnabled =
            !string.Equals(_subtitleTranslationProvider, "cloud", StringComparison.OrdinalIgnoreCase);
        _subtitlePreloadAheadSeconds = subtitles.PreloadAheadSeconds;
        _subtitleFontFamily = ResolveFontFamily(subtitles.FontFamily);
        _subtitleFontSize = Math.Clamp(subtitles.FontSize, 12, 60);
        _subtitleBold = subtitles.Bold;
        _subtitleTextColor = ResolvePresetColor(subtitles.TextColor);
        _subtitleOutlineColor = subtitles.OutlineColor;
        _subtitleOutlineSize = subtitles.OutlineSize;
        _subtitleBackgroundColor = subtitles.BackgroundColor;
        _subtitleBackgroundOpacityLevel = Math.Clamp(subtitles.BackgroundOpacityLevel, 0, 10);
        _subtitleBottomOffsetPx = SnapBottomOffset(subtitles.BottomOffsetPx);
        _subtitleMaxLines = Math.Max(2, subtitles.MaxLines);
        _subtitleMaxWidthPercent = subtitles.MaxWidthPercent;
        _subtitleOffsetMs = subtitles.SubtitleOffsetMs;
        _subtitleWhisperModelPath = subtitles.WhisperModelPath;
        _subtitleLocalTranslationEndpoint = subtitles.LocalTranslationEndpoint;
        _subtitleLocalTranslationModel = subtitles.LocalTranslationModel;

        try
        {
            var installer = _services.GetService<WhisperModelInstaller>();
            if (installer?.IsInstalled(_subtitleWhisperModelPath) == true)
                SetSubtitleStatus("settings.subtitle.installed");
        }
        catch
        {
            // Settings can still open if model probing fails.
        }

        ApplyLocalizedOptionLabels();
        _loc.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(L));
            OnPropertyChanged(nameof(AppVersionText));
            ApplyLocalizedOptionLabels();
            RefreshSubtitleStatus();
        };
    }

    private void ApplyLocalizedOptionLabels()
    {
        SubtitleModeOptions[0].Label = _loc.T("settings.subtitle.modeOriginal");
        SubtitleModeOptions[1].Label = _loc.T("settings.subtitle.modeChinese");
        SubtitleModeOptions[2].Label = _loc.T("settings.subtitle.modeEnglish");
        SubtitleModeOptions[3].Label = _loc.T("settings.subtitle.modeBilingual");
        SubtitleTranslationProviderOptions[0].Label = _loc.T("settings.subtitle.providerLocal");
        SubtitleTranslationProviderOptions[1].Label = _loc.T("settings.subtitle.providerCloud");
        SubtitleColorOptions[0].Label = _loc.T("settings.subtitle.colorRed");
        SubtitleColorOptions[1].Label = _loc.T("settings.subtitle.colorBlack");
        SubtitleColorOptions[2].Label = _loc.T("settings.subtitle.colorBlue");
        SubtitleColorOptions[3].Label = _loc.T("settings.subtitle.colorYellow");
        SubtitleColorOptions[4].Label = _loc.T("settings.subtitle.colorGreen");
    }

    private void SetSubtitleStatus(string key, params object[] args)
    {
        _subtitleStatusKey = key;
        _subtitleStatusArgs = args;
        RefreshSubtitleStatus();
    }

    private void RefreshSubtitleStatus()
    {
        if (string.IsNullOrEmpty(_subtitleStatusKey))
            return;
        SubtitleModelInstallStatus = _subtitleStatusArgs.Length == 0
            ? _loc.T(_subtitleStatusKey)
            : _loc.Format(_subtitleStatusKey, _subtitleStatusArgs);
    }

    partial void OnSubtitleTranslationProviderChanged(string value)
    {
        SubtitleLocalTranslationFieldsEnabled =
            !string.Equals(value, "cloud", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!TryPersistSettings(out var error))
        {
            SetStatusMessage(error);
            return;
        }

        try
        {
            await SubtitleWebViewBootstrapper.RefreshAllStylesAsync();
        }
        catch
        {
            // Style refresh must not block a successful settings save.
        }

        SetStatusMessage(_loc.T("settings.saved"));
    }

    [RelayCommand]
    private async Task InstallWhisperModelAsync()
    {
        if (!SubtitleModelInstallEnabled)
            return;

        var configuredPath = SubtitleWhisperModelPath?.Trim();
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            SetSubtitleStatus("settings.subtitle.needPath");
            return;
        }

        SubtitleModelInstallEnabled = false;
        try
        {
            var installer = _services.GetRequiredService<WhisperModelInstaller>();
            if (installer.IsInstalled(configuredPath))
            {
                SetSubtitleStatus("settings.subtitle.installed");
                return;
            }

            SetSubtitleStatus("settings.subtitle.downloading");
            var progress = new Progress<double>(value =>
            {
                SetSubtitleStatus("settings.subtitle.downloadingPct", value.ToString("P0"));
            });
            var installedPath = await installer.InstallBaseModelAsync(configuredPath, progress);

            var runtime = _services.GetService<WhisperSpeechRecognizerOptions>();
            if (runtime is not null)
                runtime.ModelPath = configuredPath;

            SetSubtitleStatus("settings.subtitle.installedPath", installedPath);
        }
        catch (OperationCanceledException)
        {
            SetSubtitleStatus("settings.subtitle.cancelled");
        }
        catch (Exception ex)
        {
            SetSubtitleStatus("settings.subtitle.installFailed", ex.Message);
        }
        finally
        {
            SubtitleModelInstallEnabled = true;
        }
    }

    [RelayCommand]
    private async Task MigrateDownloadsAsync()
    {
        if (!TryPersistSettings(out var error))
        {
            SetStatusMessage(error);
            return;
        }

        var engine = _services.GetRequiredService<IDownloadEngine>();
        var root = PathExpander.Expand(_options.Download.DefaultSavePath.Trim());
        string rootFull;
        try
        {
            rootFull = Path.GetFullPath(root);
        }
        catch
        {
            SetStatusMessage(_loc.T("settings.migrateInvalidPath"));
            return;
        }

        var candidateCount = engine.GetActiveJobs().Count(j =>
            j.Status == DownloadStatus.Completed &&
            !string.IsNullOrWhiteSpace(j.TargetPath) &&
            File.Exists(j.TargetPath) &&
            !IsUnderRoot(j.TargetPath, rootFull));

        if (candidateCount == 0)
        {
            SetStatusMessage(_loc.T("settings.migrateNone"));
            return;
        }

        var confirm = MessageBox.Show(
            _loc.Format("settings.migrateConfirm", candidateCount),
            _loc.T("settings.migrate"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        MigrateEnabled = false;
        try
        {
            var result = await engine.MigrateCompletedToSaveRootAsync(rootFull);
            if (result.BlockReason is "in-flight")
            {
                SetStatusMessage(_loc.T("settings.migrateBlocked"));
                return;
            }

            if (result.BlockReason is "invalid-root" or "empty-root")
            {
                SetStatusMessage(_loc.T("settings.migrateInvalidPath"));
                return;
            }

            SetStatusMessage(_loc.Format("settings.migrateDone", result.Moved, result.Skipped, result.Failed));
            if (result.Moved > 0)
                DownloadsMigrated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            SetStatusMessage(_loc.Format("settings.migrateFailed", ex.Message));
        }
        finally
        {
            MigrateEnabled = true;
        }
    }

    private bool TryPersistSettings(out string error)
    {
        error = string.Empty;
        if (!PathValidator.IsValidSaveDirectory(SavePath.Trim()))
        {
            error = _loc.T("settings.invalidPath");
            return false;
        }
        if (!int.TryParse(RetryCountText, out var retryCount))
        {
            error = _loc.T("settings.invalidRetry");
            return false;
        }
        if (!int.TryParse(FailedRetryIntervalText, out var failedRetryInterval))
        {
            error = _loc.T("settings.invalidFailedRetryInterval");
            return false;
        }
        if (!Uri.TryCreate(SubtitleLocalTranslationEndpoint, UriKind.Absolute, out var localEndpoint) ||
            !localEndpoint.IsLoopback)
        {
            error = _loc.T("settings.subtitle.invalidEndpoint");
            return false;
        }

        _options.Download.DefaultSavePath = SavePath.Trim();
        _options.Download.MaxConcurrentDownloads = Math.Clamp(MaxConcurrent, 1, 5);
        _options.Download.RetryCount = Math.Clamp(retryCount, 0, 10);
        _options.Download.FailedRetryIntervalSeconds = Math.Clamp(failedRetryInterval, 0, 3600);
        _options.Download.AutoRecoverDownloads = AutoRecover;
        _options.Ui.AutoCheckForUpdates = AutoCheckForUpdates;
        _options.Logging.Enabled = LoggingEnabled;
        _options.Logging.MinimumLevel = string.IsNullOrWhiteSpace(LogLevel) ? "Information" : LogLevel.Trim();
        _options.Ui.Language = _loc.LanguageCode;

        var subtitles = _options.Subtitles;
        subtitles.Enabled = SubtitleEnabled;
        subtitles.Mode = SubtitleMode;
        subtitles.PreloadAheadSeconds = Math.Clamp(SubtitlePreloadAheadSeconds, 10, 90);
        // Cloud remains selectable/persisted, but runtime always uses the local translator.
        subtitles.TranslationProvider = string.Equals(SubtitleTranslationProvider, "cloud", StringComparison.OrdinalIgnoreCase)
            ? "cloud"
            : "local";
        subtitles.FontFamily = string.IsNullOrWhiteSpace(SubtitleFontFamily) ? "Microsoft YaHei" : SubtitleFontFamily.Trim();
        subtitles.FontSize = Math.Clamp(SubtitleFontSize, 12, 60);
        subtitles.Bold = SubtitleBold;
        subtitles.TextColor = ResolvePresetColor(SubtitleTextColor);
        subtitles.OutlineColor = NormalizeColor(SubtitleOutlineColor, "#000000");
        subtitles.OutlineSize = Math.Clamp(SubtitleOutlineSize, 0, 8);
        subtitles.BackgroundColor = NormalizeColor(SubtitleBackgroundColor, "#000000");
        subtitles.BackgroundOpacityLevel = Math.Clamp(SubtitleBackgroundOpacityLevel, 0, 10);
        subtitles.BottomOffsetPx = SnapBottomOffset(SubtitleBottomOffsetPx);
        subtitles.MaxLines = Math.Clamp(Math.Max(SubtitleMaxLines, SubtitleMode == SubtitleMode.Bilingual ? 2 : 1), 1, 4);
        subtitles.MaxWidthPercent = Math.Clamp(SubtitleMaxWidthPercent, 20, 100);
        subtitles.SubtitleOffsetMs = Math.Clamp(SubtitleOffsetMs, -5000, 5000);
        subtitles.WhisperModelPath = SubtitleWhisperModelPath.Trim();
        subtitles.LocalTranslationEndpoint = localEndpoint.AbsoluteUri;
        subtitles.LocalTranslationModel = string.IsNullOrWhiteSpace(SubtitleLocalTranslationModel)
            ? "local-model"
            : SubtitleLocalTranslationModel.Trim();

        // Keep already-created local engines in sync; no application restart is required.
        var whisperRuntime = _services.GetService<WhisperSpeechRecognizerOptions>();
        if (whisperRuntime is not null)
            whisperRuntime.ModelPath = subtitles.WhisperModelPath;
        var translationRuntime = _services.GetService<LocalLlmTranslatorOptions>();
        if (translationRuntime is not null)
        {
            translationRuntime.Endpoint = subtitles.LocalTranslationEndpoint;
            translationRuntime.Model = subtitles.LocalTranslationModel;
        }

        _store.Save(_options);
        _appLog.ApplyFromOptions();
        return true;
    }

    private string ResolveFontFamily(string? preferred)
    {
        var name = string.IsNullOrWhiteSpace(preferred) ? "Microsoft YaHei" : preferred.Trim();
        var exact = SystemFontFamilies.FirstOrDefault(x =>
            string.Equals(x.FamilyName, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.DisplayName, name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact.FamilyName;

        var yahei = SystemFontFamilies.FirstOrDefault(x =>
            x.FamilyName.Contains("YaHei", StringComparison.OrdinalIgnoreCase) ||
            x.DisplayName.Contains("微软雅黑", StringComparison.OrdinalIgnoreCase));
        return yahei?.FamilyName ?? SystemFontFamilies.FirstOrDefault()?.FamilyName ?? "Microsoft YaHei";
    }

    /// <summary>One-shot tips auto-clear after 10s. Sticky messages (e.g. update progress) stay until replaced.</summary>
    public void SetStatusMessage(string message, bool sticky = false)
    {
        CancelStatusDismiss();
        StatusMessage = message ?? string.Empty;
        if (!sticky && !string.IsNullOrWhiteSpace(StatusMessage))
            ScheduleStatusDismiss();
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
                if (generation == _statusGeneration)
                    StatusMessage = string.Empty;
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

    private static string ResolvePresetColor(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToUpperInvariant();
        return text switch
        {
            "#FF0000" => "#FF0000",
            "#000000" => "#000000",
            "#0000FF" or "#0080FF" => "#0000FF",
            "#FFFF00" => "#FFFF00",
            "#00FF00" or "#008000" => "#00FF00",
            _ => "#FFFF00"
        };
    }

    private static int SnapBottomOffset(int value)
    {
        var best = BottomOffsets[0];
        var bestDistance = Math.Abs(value - best);
        foreach (var choice in BottomOffsets)
        {
            var distance = Math.Abs(value - choice);
            if (distance >= bestDistance)
                continue;
            best = choice;
            bestDistance = distance;
        }
        return best;
    }

    private static string NormalizeColor(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        var text = value.Trim();
        if (text.Length == 7 && text[0] == '#' && text.Skip(1).All(Uri.IsHexDigit))
            return text.ToUpperInvariant();
        return fallback;
    }

    private static bool IsUnderRoot(string filePath, string fullRoot)
    {
        try
        {
            var root = Path.GetFullPath(fullRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var file = Path.GetFullPath(filePath);
            var prefix = root + Path.DirectorySeparatorChar;
            return file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(file, root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [RelayCommand]
    private void ClearLogs()
    {
        var confirm = MessageBox.Show(
            _loc.T("settings.clearLogsConfirm"),
            _loc.T("settings.clearLogs"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        var (deleted, failed) = _appLog.ClearAllLogs();
        SetStatusMessage(failed == 0
            ? _loc.Format("settings.clearLogsDone", deleted)
            : _loc.Format("settings.clearLogsPartial", deleted, failed));
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            var dir = _appLog.LogDirectory;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
            SetStatusMessage(_loc.Format("settings.openLogsFolderDone", dir));
        }
        catch (Exception ex)
        {
            SetStatusMessage(_loc.Format("settings.openLogsFolderFailed", ex.Message));
        }
    }

    [RelayCommand]
    private void CheckUpdate()
    {
        if (ClientUpdateCoordinator.IsBusy)
        {
            SetStatusMessage(_loc.T("update.alreadyRunning"));
            return;
        }

        CheckUpdateEnabled = false;
        SetStatusMessage(_loc.T("update.checking"), sticky: true);
        ClientUpdateCoordinator.BeginManualCheck(_services);
    }
}

public sealed class NamedValue<T> : INotifyPropertyChanged
{
    private string _label;

    public NamedValue(string label, T value)
    {
        _label = label;
        Value = value;
    }

    public T Value { get; }

    public string Label
    {
        get => _label;
        set
        {
            if (_label == value)
                return;
            _label = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
