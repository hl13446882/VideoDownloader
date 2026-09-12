using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VideoDownloader.Infrastructure.Licensing;
using VideoDownloader.Infrastructure.Update;
using VideoDownloader.UI.Localization;

namespace VideoDownloader.UI;

internal static class ClientUpdateCoordinator
{
    public static async Task RunStartupCheckAsync(IServiceProvider services)
    {
        try
        {
            var updates = services.GetRequiredService<UpdateService>();
            var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("ClientUpdate");
            var loc = services.GetRequiredService<LocalizationService>();

            // Let the main window paint first.
            await Task.Delay(1500);

            var result = await updates.CheckAndPrepareAsync();
            logger.LogInformation("Startup update check: {Outcome} remote={Version}", result.Outcome, result.RemoteVersion);

            if (result.Outcome is UpdateCheckOutcome.Downloaded or UpdateCheckOutcome.ReadyToApply)
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                    PromptApply(services, result, loc));
            }
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

    public static async Task<string> RunManualCheckAsync(IServiceProvider services)
    {
        var updates = services.GetRequiredService<UpdateService>();
        var license = services.GetRequiredService<LicenseService>().Current;
        var loc = services.GetRequiredService<LocalizationService>();

        if (!license.IsFull || !license.IsValid)
            return loc.T("update.licenseRequired");

        var result = await updates.CheckAndPrepareAsync();
        return result.Outcome switch
        {
            UpdateCheckOutcome.SkippedNotLicensed => loc.T("update.licenseRequired"),
            UpdateCheckOutcome.UpToDate => loc.Format("update.upToDate", AppVersionInfo.SemVer),
            UpdateCheckOutcome.Downloaded or UpdateCheckOutcome.ReadyToApply =>
                PromptApply(services, result, loc)
                    ? loc.Format("update.restarting", result.RemoteVersion ?? "")
                    : loc.Format("update.readyLater", result.RemoteVersion ?? ""),
            UpdateCheckOutcome.Forbidden => loc.T("update.forbidden"),
            _ => loc.Format("update.failed", result.Message ?? result.Outcome.ToString())
        };
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
        var launcher = Path.Combine(installRoot, "VideoDownloader.exe");
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
