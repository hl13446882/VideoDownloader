using Microsoft.Extensions.DependencyInjection;
using VideoDownloader.Infrastructure;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.UI;
using VideoDownloader.UI.ViewModels;

var services = new ServiceCollection();
services.AddVideoDownloaderInfrastructure(new AppOptions());
services.AddTransient<MainViewModel>();
services.AddTransient<SettingsViewModel>();
services.AddTransient<MainWindow>();
try {
    var sp = services.BuildServiceProvider();
    sp.GetRequiredService<SiteAdapterRouter>();
    Console.WriteLine("SiteAdapterRouter OK");
    sp.GetRequiredService<MainWindow>();
    Console.WriteLine("MainWindow OK");
} catch (Exception ex) {
    Console.WriteLine(ex.ToString());
}
