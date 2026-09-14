using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
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

    [ObservableProperty]
    private string _savePath;

    [ObservableProperty]
    private int _maxConcurrent;

    [ObservableProperty]
    private string _retryCountText;

    [ObservableProperty]
    private string _failedRetryIntervalText;

    [ObservableProperty]
    private bool _autoRecover;

    [ObservableProperty]
    private bool _loggingEnabled;

    [ObservableProperty]
    private string _logLevel;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _checkUpdateEnabled = true;

    [ObservableProperty]
    private bool _migrateEnabled = true;

    public LocalizationService L => _loc;

    public string AppVersionText => "V" + AppVersionInfo.SemVer;

    public IReadOnlyList<int> ConcurrentOptions { get; } = [1, 2, 3, 4, 5];

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

            StatusMessage = _loc.Format(
                "settings.migrateDone",
                result.Moved,
                result.Skipped,
                result.Failed);
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

        _options.Download.DefaultSavePath = SavePath.Trim();
        _options.Download.MaxConcurrentDownloads = Math.Clamp(MaxConcurrent, 1, 5);
        _options.Download.RetryCount = Math.Clamp(retryCount, 0, 10);
        _options.Download.FailedRetryIntervalSeconds = Math.Clamp(failedRetryInterval, 0, 3600);
        _options.Download.AutoRecoverDownloads = AutoRecover;
        _options.Logging.Enabled = LoggingEnabled;
        _options.Logging.MinimumLevel = string.IsNullOrWhiteSpace(LogLevel) ? "Information" : LogLevel.Trim();
        _options.Ui.Language = _loc.LanguageCode;

        _store.Save(_options);
        _appLog.ApplyFromOptions();
        return true;
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
        // Progress continues on the main window even if this Settings dialog closes.
    }
}
