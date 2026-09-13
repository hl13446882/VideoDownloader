using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
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
    private const string InstanceMutexName = @"Local\VideoDownloader.SingleInstance";
    private ServiceProvider? _services;
    private Mutex? _instanceMutex;
    private bool _mutexOwned;

    static App()
    {
        ConfigureAssemblyResolution();
    }

    public App()
    {
        ShutdownMode = ShutdownMode.OnMainWindowClose;
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

        // Root launcher cannot overwrite itself; leftover sidecar is cleaned (or applied) here.
        CleanupOrApplyDeferredLauncher();

        if (!TryAcquireInstanceMutex())
        {
            MessageBox.Show(
                "客户端已在运行。请关闭已打开的窗口后再试，不要重复启动（会抢占同一份续传文件）。",
                "Video Downloader",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

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
            BringMainWindowToFront(mainWindow);
            UpdateLicenseTitle(mainWindow, license, loc);
            loc.LanguageChanged += (_, _) =>
            {
                var current = _services.GetRequiredService<LicenseService>().Current;
                UpdateLicenseTitle(mainWindow, current, loc);
            };

            ClientUpdateCoordinator.ShowPreviousFailureIfAny(loc);
            _ = ClientUpdateCoordinator.RunStartupCheckAsync(_services);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"应用启动失败 / Startup failed：{ex.Message}",
                "Video Downloader",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            EmergencyExit(1);
        }
    }

    /// <summary>
    /// Install layout is &lt;root&gt;\app\VideoDownloader.exe. A failed launcher self-replace
    /// leaves &lt;root&gt;\VideoBrowser.exe.new. Prefer finishing the replace; otherwise delete.
    /// </summary>
    private static void CleanupOrApplyDeferredLauncher()
    {
        try
        {
            var appDir = Path.GetFullPath(AppContext.BaseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var installRoot = Path.GetDirectoryName(appDir);
            if (string.IsNullOrWhiteSpace(installRoot))
                return;

            CleanupDeferredSidecar(installRoot, "VideoBrowser.exe", "VideoBrowser.exe.new");
            // Legacy name from earlier builds.
            CleanupDeferredSidecar(installRoot, "VideoDownloader.exe", "VideoDownloader.exe.new");
            TryDeleteWithRetry(Path.Combine(appDir, "VideoBrowser.exe.new"));
            TryDeleteWithRetry(Path.Combine(appDir, "VideoDownloader.exe.new"));
        }
        catch
        {
            // Best-effort only.
        }
    }

    private static void CleanupDeferredSidecar(string installRoot, string launcherName, string deferredName)
    {
        var newPath = Path.Combine(installRoot, deferredName);
        var target = Path.Combine(installRoot, launcherName);
        if (!File.Exists(newPath))
            return;

        if (File.Exists(target))
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    File.Copy(newPath, target, overwrite: true);
                    break;
                }
                catch
                {
                    Thread.Sleep(250);
                }
            }
        }

        TryDeleteWithRetry(newPath);
    }

    private static void TryDeleteWithRetry(string path)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                    return;
                File.Delete(path);
                return;
            }
            catch
            {
                Thread.Sleep(250);
            }
        }
    }

    private static void BringMainWindowToFront(Window window)
    {
        try
        {
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
            window.Focus();
        }
        catch
        {
            // Best-effort focus only.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            DisposeServices();
        }
        finally
        {
            ReleaseInstanceMutex();
            base.OnExit(e);
            // Download/WebView2 work can keep the CLR process alive after the window is gone.
            // Hard-exit so close/crash never leaves a mutex-holding ghost.
            Environment.Exit(e.ApplicationExitCode);
        }
    }

    private bool TryAcquireInstanceMutex()
    {
        if (TryCreateMutex(out var createdNew) && createdNew)
            return true;

        // Living process with no UI = previous close/crash left a ghost. Reclaim once.
        if (TryKillWindowlessSiblings())
        {
            ReleaseInstanceMutex();
            return TryCreateMutex(out createdNew) && createdNew;
        }

        ReleaseInstanceMutex();
        return false;
    }

    private bool TryCreateMutex(out bool createdNew)
    {
        createdNew = false;
        try
        {
            _instanceMutex = new Mutex(true, InstanceMutexName, out createdNew);
            _mutexOwned = createdNew;
            if (!createdNew)
            {
                _instanceMutex.Dispose();
                _instanceMutex = null;
            }

            return true;
        }
        catch
        {
            _instanceMutex = null;
            _mutexOwned = false;
            return false;
        }
    }

    private static bool TryKillWindowlessSiblings()
    {
        var self = Process.GetCurrentProcess();
        var selfPath = NormalizePath(self.MainModule?.FileName);
        var killed = false;

        foreach (var process in Process.GetProcessesByName("VideoDownloader"))
        {
            try
            {
                if (process.Id == self.Id)
                    continue;

                var path = NormalizePath(SafeMainModulePath(process));
                // Only reclaim instances of this install. Skip if path cannot be read.
                if (selfPath is null || path is null ||
                    !string.Equals(selfPath, path, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Still starting (no HWND yet) — do not kill a healthy peer mid-init.
                if ((DateTime.Now - process.StartTime) < TimeSpan.FromSeconds(30) &&
                    process.MainWindowHandle == IntPtr.Zero)
                    continue;

                if (process.MainWindowHandle != IntPtr.Zero)
                    continue;

                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
                killed = true;
            }
            catch
            {
                // ignore access / exited races
            }
            finally
            {
                process.Dispose();
            }
        }

        return killed;
    }

    private static string? SafeMainModulePath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch { return null; }
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    private void DisposeServices()
    {
        if (_services is null)
            return;

        try
        {
            if (_services is IAsyncDisposable asyncDisposable)
            {
                var dispose = asyncDisposable.DisposeAsync().AsTask();
                if (!dispose.Wait(TimeSpan.FromSeconds(5)))
                    return;
            }
            else
            {
                _services.Dispose();
            }
        }
        catch
        {
            // ignore shutdown dispose failures
        }
        finally
        {
            _services = null;
        }
    }

    private void ReleaseInstanceMutex()
    {
        if (_instanceMutex is null)
            return;

        try
        {
            if (_mutexOwned)
                _instanceMutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        try { _instanceMutex.Dispose(); }
        catch { /* ignore */ }

        _instanceMutex = null;
        _mutexOwned = false;
    }

    private void EmergencyExit(int code)
    {
        try { DisposeServices(); }
        catch { /* ignore */ }
        ReleaseInstanceMutex();
        Environment.Exit(code);
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

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
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
        catch
        {
            // ignore UI failures during crash
        }
        finally
        {
            // Fatal CLR errors must not leave a mutex-holding process.
            EmergencyExit(1);
        }
    }
}
