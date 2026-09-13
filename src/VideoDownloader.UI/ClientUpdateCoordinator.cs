using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VideoDownloader.Infrastructure.Licensing;
using VideoDownloader.Infrastructure.Update;
using VideoDownloader.UI.Localization;
using VideoDownloader.UI.ViewModels;

namespace VideoDownloader.UI;

internal static class ClientUpdateCoordinator
{
    private static int _busy;

    public static bool IsBusy => Volatile.Read(ref _busy) != 0;

    public static async Task RunStartupCheckAsync(IServiceProvider services)
    {
        try
        {
            // Let the main window paint first.
            await Task.Delay(1500);
            await RunCheckCoreAsync(services, promptWhenReady: true, showProgress: true);
        }
        catch (Exception ex)
        {
            // Never block app use on update failures.
            try
            {
                services.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("ClientUpdate")
                    .LogWarning(ex, "Startup update check crashed");
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>
    /// Starts a manual update check on a background task. Progress is shown on the main window
    /// and continues even if the Settings dialog is closed.
    /// </summary>
    public static void BeginManualCheck(IServiceProvider services)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RunCheckCoreAsync(services, promptWhenReady: true, showProgress: true);
            }
            catch (Exception ex)
            {
                try
                {
                    services.GetRequiredService<ILoggerFactory>()
                        .CreateLogger("ClientUpdate")
                        .LogWarning(ex, "Manual update check crashed");
                    var loc = services.GetRequiredService<LocalizationService>();
                    ReportStatus(services, loc.Format("update.failed", ex.Message));
                }
                catch
                {
                    // ignore
                }
            }
        });
    }

    private static async Task RunCheckCoreAsync(
        IServiceProvider services,
        bool promptWhenReady,
        bool showProgress)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            var locBusy = services.GetRequiredService<LocalizationService>();
            ReportStatus(services, locBusy.T("update.alreadyRunning"));
            return;
        }

        try
        {
            var updates = services.GetRequiredService<UpdateService>();
            var license = services.GetRequiredService<LicenseService>().Current;
            var loc = services.GetRequiredService<LocalizationService>();
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("ClientUpdate");

            if (!license.IsFull || !license.IsValid)
            {
                ReportStatus(services, loc.T("update.licenseRequired"));
                return;
            }

            IProgress<UpdateProgress>? progress = showProgress
                ? new Progress<UpdateProgress>(p => ReportProgress(services, loc, p))
                : null;

            var result = await updates.CheckAndPrepareAsync(progress);
            logger.LogInformation("Update check: {Outcome} remote={Version}", result.Outcome, result.RemoteVersion);

            var finalStatus = result.Outcome switch
            {
                UpdateCheckOutcome.SkippedNotLicensed => loc.T("update.licenseRequired"),
                UpdateCheckOutcome.UpToDate => loc.Format("update.upToDate", AppVersionInfo.SemVer),
                UpdateCheckOutcome.Downloaded or UpdateCheckOutcome.ReadyToApply =>
                    await HandleReadyAsync(services, result, loc, promptWhenReady),
                UpdateCheckOutcome.Forbidden => loc.T("update.forbidden"),
                UpdateCheckOutcome.SkippedDisabled => loc.T("update.checking"),
                _ => loc.Format("update.failed", result.Message ?? result.Outcome.ToString())
            };

            if (!string.IsNullOrWhiteSpace(finalStatus))
                ReportStatus(services, finalStatus);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            NotifyBusyChanged(services);
        }
    }

    private static async Task<string> HandleReadyAsync(
        IServiceProvider services,
        UpdateCheckResult result,
        LocalizationService loc,
        bool promptWhenReady)
    {
        if (!promptWhenReady)
            return loc.Format("update.readyLater", result.RemoteVersion ?? "");

        var apply = false;
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            apply = PromptApply(services, result, loc);
        });

        return apply
            ? loc.Format("update.restarting", result.RemoteVersion ?? "")
            : loc.Format("update.readyLater", result.RemoteVersion ?? "");
    }

    private static void ReportProgress(IServiceProvider services, LocalizationService loc, UpdateProgress progress)
    {
        var text = progress.Kind switch
        {
            UpdateProgressKind.Checking => loc.T("update.checking"),
            UpdateProgressKind.FoundVersion => loc.Format("update.foundVersion", progress.Version ?? "?"),
            UpdateProgressKind.DownloadingFile => loc.Format(
                "update.downloadingFile",
                string.IsNullOrWhiteSpace(progress.FilePath) ? "?" : progress.FilePath),
            _ => loc.T("update.checking")
        };
        ReportStatus(services, text);
    }

    private static void ReportStatus(IServiceProvider services, string message)
    {
        void Apply()
        {
            if (Application.Current?.MainWindow?.DataContext is MainViewModel main)
                main.SetStatus(message);

            // Mirror onto the settings VM owned by the main window when still open.
            if (Application.Current?.Windows is not null)
            {
                foreach (Window window in Application.Current.Windows)
                {
                    if (window is SettingsWindow { DataContext: SettingsViewModel settings })
                        settings.StatusMessage = message;
                }
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;
        if (dispatcher.CheckAccess())
            Apply();
        else
            dispatcher.Invoke(Apply);
    }

    private static void NotifyBusyChanged(IServiceProvider services)
    {
        void Apply()
        {
            if (Application.Current?.Windows is null)
                return;
            foreach (Window window in Application.Current.Windows)
            {
                if (window is SettingsWindow { DataContext: SettingsViewModel settings })
                    settings.CheckUpdateEnabled = !IsBusy;
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;
        if (dispatcher.CheckAccess())
            Apply();
        else
            dispatcher.Invoke(Apply);
    }

    private static bool PromptApply(IServiceProvider services, UpdateCheckResult result, LocalizationService loc)
    {
        var version = result.RemoteVersion ?? "?";
        var answer = MessageBox.Show(
            loc.Format("update.promptBody", version),
            loc.T("update.promptTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (answer != MessageBoxResult.Yes)
            return false;

        var license = services.GetRequiredService<LicenseService>().Current;
        if (!license.IsFull || !license.IsValid)
        {
            services.GetRequiredService<UpdateService>().ClearPending();
            MessageBox.Show(loc.T("update.licenseRequired"), loc.T("update.promptTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        ApplyAndRestart();
        return true;
    }

    private static void ApplyAndRestart()
    {
        var installRoot = UpdateService.InstallRoot;
        var launcher = Path.Combine(installRoot, "VideoBrowser.exe");
        if (!File.Exists(launcher))
            launcher = Path.Combine(installRoot, "VideoDownloader.exe");
        if (!File.Exists(launcher))
        {
            MessageBox.Show(
                "找不到启动器，无法自动升级。请手动重启。",
                "Video Downloader",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = launcher,
                WorkingDirectory = installRoot,
                Arguments = "--apply-update",
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "无法启动升级程序：" + ex.Message,
                "Video Downloader",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        Application.Current.Shutdown(0);
    }

    public static void ShowPreviousFailureIfAny(LocalizationService loc)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VideoDownloader",
                "update-error.log");
            if (!File.Exists(path))
                return;
            var text = File.ReadAllText(path);
            File.Delete(path);
            if (string.IsNullOrWhiteSpace(text))
                return;
            MessageBox.Show(
                loc.Format("update.applyFailed", text.Trim()),
                loc.T("update.promptTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
            // ignore
        }
    }
}
