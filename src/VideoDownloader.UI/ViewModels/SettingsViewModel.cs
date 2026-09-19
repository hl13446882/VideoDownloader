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
using VideoDownloader.Infrastructure.Update;
using VideoDownloader.UI.Localization;

namespace VideoDownloader.UI.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
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
    [ObservableProperty] private bool _loggingEnabled;
    [ObservableProperty] private string _logLevel;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _checkUpdateEnabled = true;
    [ObservableProperty] private bool _migrateEnabled = true;

    [ObservableProperty] private bool _subtitleEnabled;
    [ObservableProperty] private SubtitleMode _subtitleMode;
    [ObservableProperty] private int _subtitlePreloadAheadSeconds;
    [ObservableProperty] private string _subtitleFontFamily;
    [ObservableProperty] private int _subtitleFontSize;
    [ObservableProperty] private bool _subtitleBold;
    [ObservableProperty] private string _subtitleTextColor;
    [ObservableProperty] private string _subtitleOutlineColor;
    [ObservableProperty] private int _subtitleOutlineSize;
    [ObservableProperty] private string _subtitleBackgroundColor;
    [ObservableProperty] private double _subtitleBackgroundOpacity;
    [ObservableProperty] private int _subtitleBottomOffsetPx;
    [ObservableProperty] private int _subtitleMaxLines;
    [ObservableProperty] private int _subtitleMaxWidthPercent;
    [ObservableProperty] private int _subtitleOffsetMs;
    [ObservableProperty] private string _subtitleWhisperModelPath;
    [ObservableProperty] private string _subtitleLocalTranslationEndpoint;
    [ObservableProperty] private string _subtitleLocalTranslationModel;

    public LocalizationService L => _loc;
    public string AppVersionText => "V" + AppVersionInfo.SemVer;
    public IReadOnlyList<int> ConcurrentOptions { get; } = [1, 2, 3, 4, 5];
    public IReadOnlyList<SubtitleMode> SubtitleModeOptions { get; } =
        [SubtitleMode.Original, SubtitleMode.Chinese, SubtitleMode.English];

    /// <summary>Raised after a successful migrate so the main window can refresh the queue.</summary>
    public event EventHandler? DownloadsMigrated;

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
        _loggingEnabled = options.Logging.Enabled;
        _logLevel = options.Logging.MinimumLevel;

        var subtitles = options.Subtitles;
        _subtitleEnabled = subtitles.Enabled;
        _subtitleMode = subtitles.Mode;
        _subtitlePreloadAheadSeconds = subtitles.PreloadAheadSeconds;
        _subtitleFontFamily = subtitles.FontFamily;
        _subtitleFontSize = subtitles.FontSize;
        _subtitleBold = subtitles.Bold;
        _subtitleTextColor = subtitles.TextColor;
        _subtitleOutlineColor = subtitles.OutlineColor;
        _subtitleOutlineSize = subtitles.OutlineSize;
        _subtitleBackgroundColor = subtitles.BackgroundColor;
        _subtitleBackgroundOpacity = subtitles.BackgroundOpacity;
        _subtitleBottomOffsetPx = subtitles.BottomOffsetPx;
        _subtitleMaxLines = subtitles.MaxLines;
        _subtitleMaxWidthPercent = subtitles.MaxWidthPercent;
        _subtitleOffsetMs = subtitles.SubtitleOffsetMs;
        _subtitleWhisperModelPath = subtitles.WhisperModelPath;
        _subtitleLocalTranslationEndpoint = subtitles.LocalTranslationEndpoint;
        _subtitleLocalTranslationModel = subtitles.LocalTranslationModel;

        _loc.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(L));
            OnPropertyChanged(nameof(AppVersionText));
        };
    }

    [RelayCommand]
    private void Save()
    {
        if (!TryPersistSettings(out var error))
        {
            StatusMessage = error;
            return;
        }
        StatusMessage = _loc.T("settings.saved");
    }

    [RelayCommand]
    private async Task MigrateDownloadsAsync()
    {
        if (!TryPersistSettings(out var error))
        {
            StatusMessage = error;
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
            StatusMessage = _loc.T("settings.migrateInvalidPath");
            return;
        }

        var candidateCount = engine.GetActiveJobs().Count(j =>
            j.Status == DownloadStatus.Completed &&
            !string.IsNullOrWhiteSpace(j.TargetPath) &&
            File.Exists(j.TargetPath) &&
            !IsUnderRoot(j.TargetPath, rootFull));

        if (candidateCount == 0)
        {
            StatusMessage = _loc.T("settings.migrateNone");
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
                StatusMessage = _loc.T("settings.migrateBlocked");
                return;
            }

            if (result.BlockReason is "invalid-root" or "empty-root")
            {
                StatusMessage = _loc.T("settings.migrateInvalidPath");
                return;
            }

            StatusMessage = _loc.Format("settings.migrateDone", result.Moved, result.Skipped, result.Failed);
            if (result.Moved > 0)
                DownloadsMigrated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = _loc.Format("settings.migrateFailed", ex.Message);
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
            error = "本地翻译地址必须是 127.0.0.1/localhost。";
            return false;
        }

        _options.Download.DefaultSavePath = SavePath.Trim();
        _options.Download.MaxConcurrentDownloads = Math.Clamp(MaxConcurrent, 1, 5);
        _options.Download.RetryCount = Math.Clamp(retryCount, 0, 10);
        _options.Download.FailedRetryIntervalSeconds = Math.Clamp(failedRetryInterval, 0, 3600);
        _options.Download.AutoRecoverDownloads = AutoRecover;
        _options.Logging.Enabled = LoggingEnabled;
        _options.Logging.MinimumLevel = string.IsNullOrWhiteSpace(LogLevel) ? "Information" : LogLevel.Trim();
        _options.Ui.Language = _loc.LanguageCode;

        var subtitles = _options.Subtitles;
        subtitles.Enabled = SubtitleEnabled;
        subtitles.Mode = SubtitleMode;
        subtitles.PreloadAheadSeconds = Math.Clamp(SubtitlePreloadAheadSeconds, 10, 90);
        subtitles.TranslationProvider = "local"; // Cloud entry is reserved but not implemented in this phase.
        subtitles.FontFamily = string.IsNullOrWhiteSpace(SubtitleFontFamily) ? "Microsoft YaHei" : SubtitleFontFamily.Trim();
        subtitles.FontSize = Math.Clamp(SubtitleFontSize, 10, 96);
        subtitles.Bold = SubtitleBold;
        subtitles.TextColor = NormalizeColor(SubtitleTextColor, "#FFFFFF");
        subtitles.OutlineColor = NormalizeColor(SubtitleOutlineColor, "#000000");
        subtitles.OutlineSize = Math.Clamp(SubtitleOutlineSize, 0, 8);
        subtitles.BackgroundColor = NormalizeColor(SubtitleBackgroundColor, "#000000");
        subtitles.BackgroundOpacity = Math.Clamp(SubtitleBackgroundOpacity, 0, 1);
        subtitles.BottomOffsetPx = Math.Clamp(SubtitleBottomOffsetPx, 0, 1000);
        subtitles.MaxLines = Math.Clamp(SubtitleMaxLines, 1, 4);
        subtitles.MaxWidthPercent = Math.Clamp(SubtitleMaxWidthPercent, 20, 100);
        subtitles.SubtitleOffsetMs = Math.Clamp(SubtitleOffsetMs, -5000, 5000);
        subtitles.WhisperModelPath = SubtitleWhisperModelPath.Trim();
        subtitles.LocalTranslationEndpoint = localEndpoint.AbsoluteUri;
        subtitles.LocalTranslationModel = string.IsNullOrWhiteSpace(SubtitleLocalTranslationModel)
            ? "local-model"
            : SubtitleLocalTranslationModel.Trim();

        _store.Save(_options);
        _appLog.ApplyFromOptions();
        return true;
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
        StatusMessage = failed == 0
            ? _loc.Format("settings.clearLogsDone", deleted)
            : _loc.Format("settings.clearLogsPartial", deleted, failed);
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
            StatusMessage = _loc.Format("settings.openLogsFolderDone", dir);
        }
        catch (Exception ex)
        {
            StatusMessage = _loc.Format("settings.openLogsFolderFailed", ex.Message);
        }
    }

    [RelayCommand]
    private void CheckUpdate()
    {
        if (ClientUpdateCoordinator.IsBusy)
        {
            StatusMessage = _loc.T("update.alreadyRunning");
            return;
        }

        CheckUpdateEnabled = false;
        StatusMessage = _loc.T("update.checking");
        ClientUpdateCoordinator.BeginManualCheck(_services);
    }
}
