using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Security;
using VideoDownloader.UI.Localization;

namespace VideoDownloader.UI.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppOptions _options;
    private readonly UserSettingsStore _store;
    private readonly LocalizationService _loc;

    [ObservableProperty]
    private string _savePath;

    [ObservableProperty]
    private string _maxConcurrentText;

    [ObservableProperty]
    private string _retryCountText;

    [ObservableProperty]
    private bool _autoRecover;

    [ObservableProperty]
    private string _logLevel;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public LocalizationService L => _loc;

    public SettingsViewModel(AppOptions options, UserSettingsStore store, LocalizationService loc)
    {
        _options = options;
        _store = store;
        _loc = loc;
        _savePath = options.Download.DefaultSavePath;
        _maxConcurrentText = options.Download.MaxConcurrentDownloads.ToString();
        _retryCountText = options.Download.RetryCount.ToString();
        _autoRecover = options.Download.AutoRecoverDownloads;
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

        _options.Download.DefaultSavePath = SavePath.Trim();
        _options.Download.MaxConcurrentDownloads = Math.Clamp(maxConcurrent, 1, 10);
        _options.Download.RetryCount = Math.Clamp(retryCount, 0, 10);
        _options.Download.AutoRecoverDownloads = AutoRecover;
        _options.Logging.MinimumLevel = LogLevel.Trim();
        _options.Ui.Language = _loc.LanguageCode;

        _store.Save(_options);
        StatusMessage = _loc.T("settings.saved");
    }
}
