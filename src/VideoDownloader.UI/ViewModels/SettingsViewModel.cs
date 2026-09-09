using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Logging;
using VideoDownloader.Infrastructure.Security;
using VideoDownloader.UI.Localization;

namespace VideoDownloader.UI.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppOptions _options;
    private readonly UserSettingsStore _store;
    private readonly AppLogService _appLog;
    private readonly LocalizationService _loc;

    [ObservableProperty]
    private string _savePath;

    [ObservableProperty]
    private string _maxConcurrentText;

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

    public LocalizationService L => _loc;

    public SettingsViewModel(
        AppOptions options,
        UserSettingsStore store,
        AppLogService appLog,
        LocalizationService loc)
    {
        _options = options;
        _store = store;
        _appLog = appLog;
        _loc = loc;
        _savePath = options.Download.DefaultSavePath;
        _maxConcurrentText = options.Download.MaxConcurrentDownloads.ToString();
        _retryCountText = options.Download.RetryCount.ToString();
        _failedRetryIntervalText = options.Download.FailedRetryIntervalSeconds.ToString();
        _autoRecover = options.Download.AutoRecoverDownloads;
        _loggingEnabled = options.Logging.Enabled;
        _logLevel = options.Logging.MinimumLevel;
        _loc.LanguageChanged += (_, _) => OnPropertyChanged(nameof(L));
    }

    [RelayCommand]
    private void Save()
    {
        if (!PathValidator.IsValidSaveDirectory(SavePath.Trim()))
        {
            StatusMessage = _loc.T("settings.invalidPath");
            return;
        }

        if (!int.TryParse(MaxConcurrentText, out var maxConcurrent))
        {
            StatusMessage = _loc.T("settings.invalidConcurrent");
            return;
        }

        if (!int.TryParse(RetryCountText, out var retryCount))
        {
            StatusMessage = _loc.T("settings.invalidRetry");
            return;
        }

        if (!int.TryParse(FailedRetryIntervalText, out var failedRetryInterval))
        {
            StatusMessage = _loc.T("settings.invalidFailedRetryInterval");
            return;
        }

        _options.Download.DefaultSavePath = SavePath.Trim();
        _options.Download.MaxConcurrentDownloads = Math.Clamp(maxConcurrent, 1, 10);
        _options.Download.RetryCount = Math.Clamp(retryCount, 0, 10);
        _options.Download.FailedRetryIntervalSeconds = Math.Clamp(failedRetryInterval, 0, 3600);
        _options.Download.AutoRecoverDownloads = AutoRecover;
        _options.Logging.Enabled = LoggingEnabled;
        _options.Logging.MinimumLevel = string.IsNullOrWhiteSpace(LogLevel) ? "Information" : LogLevel.Trim();
        _options.Ui.Language = _loc.LanguageCode;

        _store.Save(_options);
        _appLog.ApplyFromOptions();
        StatusMessage = _loc.T("settings.saved");
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
}
