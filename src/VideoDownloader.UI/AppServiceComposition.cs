using System.IO;
using Microsoft.Extensions.DependencyInjection;
using VideoDownloader.Infrastructure;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Subtitles;
using VideoDownloader.UI.Localization;
using VideoDownloader.UI.ViewModels;

namespace VideoDownloader.UI;

/// <summary>
/// Single composition root shared by the desktop app and the live verifier so
/// probe/download behavior cannot drift between "real" and acceptance runs.
/// </summary>
public static class AppServiceComposition
{
    public static ServiceProvider Build(
        AppOptions? seed = null,
        Action<AppOptions>? configure = null,
        bool singletonUi = false)
    {
        var store = new UserSettingsStore();
        var options = store.Load(seed ?? new AppOptions());
        configure?.Invoke(options);

        Directory.CreateDirectory(PathExpander.Expand(options.Browser.UserDataFolder));
        Directory.CreateDirectory(PathExpander.Expand(options.Download.DefaultSavePath));

        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddVideoDownloaderInfrastructure(options);
        services.AddVideoDownloaderSubtitles();
        services.AddSingleton<LocalizationService>();
        if (singletonUi)
        {
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<MainViewModel>();
        }
        else
        {
            services.AddTransient<SettingsViewModel>();
            services.AddTransient<MainViewModel>();
        }

        return services.BuildServiceProvider();
    }
}
