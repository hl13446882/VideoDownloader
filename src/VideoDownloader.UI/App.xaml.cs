using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using VideoDownloader.Infrastructure;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Licensing;
using VideoDownloader.UI.ViewModels;

namespace VideoDownloader.UI;

public partial class App : Application
{
    private ServiceProvider? _services;

    static App()
    {
        ConfigureAssemblyResolution();
    }

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    private static void ConfigureAssemblyResolution()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        if (Directory.Exists(dataDir))
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            Environment.SetEnvironmentVariable("PATH", dataDir + Path.PathSeparator + path);
        }

        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var name = new AssemblyName(args.Name).Name;
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var candidate = Path.Combine(AppContext.BaseDirectory, "data", name + ".dll");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };

        NativeLibrary.SetDllImportResolver(typeof(App).Assembly, (libraryName, _, _) =>
        {
            var local = Path.Combine(AppContext.BaseDirectory, "data", libraryName);
            if (File.Exists(local) && NativeLibrary.TryLoad(local, out var handle))
                return handle;

            var withDll = local.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? local : local + ".dll";
            if (File.Exists(withDll) && NativeLibrary.TryLoad(withDll, out handle))
                return handle;

            return IntPtr.Zero;
        });
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _services = AppServiceComposition.Build();
            await _services.InitializeInfrastructureAsync();

            var license = _services.GetRequiredService<LicenseService>().Current;
            var loc = _services.GetRequiredService<VideoDownloader.UI.Localization.LocalizationService>();
            if (!license.IsFull || !license.IsValid)
            {
                MessageBox.Show(
                    loc.Format("status.demoBody", license.MachineId),
                    "Video Downloader",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            var mainWindow = new MainWindow(_services.GetRequiredService<MainViewModel>());
            MainWindow = mainWindow;
            mainWindow.Show();
            UpdateLicenseTitle(mainWindow, license, loc);
            loc.LanguageChanged += (_, _) =>
            {
                var current = _services.GetRequiredService<LicenseService>().Current;
                UpdateLicenseTitle(mainWindow, current, loc);
            };
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"应用启动失败 / Startup failed：{ex.Message}",
                "Video Downloader",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_services is IAsyncDisposable asyncDisposable)
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        else
            _services?.Dispose();

        base.OnExit(e);
    }

    private static void UpdateLicenseTitle(MainWindow window, LicenseInfo license, VideoDownloader.UI.Localization.LocalizationService loc) =>
        window.Title = license.IsFull && license.IsValid ? loc.T("title.full") : loc.T("title.demo");

    private int _dispatcherErrorDialogShown;

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        // Progress/timer refresh can re-enter the same binding failure every 400ms.
        if (Interlocked.Exchange(ref _dispatcherErrorDialogShown, 1) != 0)
            return;

        MessageBox.Show(
            $"发生未处理错误 / Unhandled error：{e.Exception.Message}",
            "Video Downloader",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            MessageBox.Show(
                $"发生严重错误 / Fatal error：{ex.Message}",
                "Video Downloader",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
