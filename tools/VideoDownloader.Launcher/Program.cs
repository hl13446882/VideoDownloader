using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoDownloader.Launcher;

internal static class Program
{
    private const string AppRelativePathMain = @"app\main\VideoDownloader.exe";
    private const string AppRelativePathLegacy = @"app\VideoDownloader.exe";
    private const string LauncherExeName = "VideoBrowser.exe";
    private const string DeferredLauncherFileName = "VideoBrowser.exe.new";
    private const string RequiredMajor = "10";
    private const string RuntimeInstallerUrl =
        "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe";
    private const string ApplyUpdateArg = "--apply-update";

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    private const int SwRestore = 9;

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        LoadingForm splash = null;
        try
        {
            var rootDir = AppDomain.CurrentDomain.BaseDirectory;
            // Leftover from a prior self-replace that never finished (Windows can't overwrite a running exe).
            CleanupStaleDeferredLauncher(rootDir);

            var applyUpdate = args != null && args.Any(a =>
                string.Equals(a, ApplyUpdateArg, StringComparison.OrdinalIgnoreCase));

            if (applyUpdate || File.Exists(PendingUpdatePath()))
            {
                splash = new LoadingForm();
                splash.Show();
                splash.SetStatus("正在应用更新…");
                Application.DoEvents();
                if (!TryApplyPendingUpdate(rootDir, splash))
                    return 1;
                args = (args ?? Array.Empty<string>())
                    .Where(a => !string.Equals(a, ApplyUpdateArg, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                // If this apply deferred a newer launcher, hand off to it so path logic
                // (app\main) matches the files just written. Old field launchers skip this.
                var deferred = Path.Combine(rootDir, DeferredLauncherFileName);
                if (File.Exists(deferred))
                {
                    try
                    {
                        HideSplash(splash);
                        var relayArgs = string.Join(" ", (args ?? Array.Empty<string>()).Select(QuoteArg));
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = deferred,
                            WorkingDirectory = rootDir,
                            Arguments = relayArgs,
                            UseShellExecute = false
                        });
                        return 0;
                    }
                    catch
                    {
                        // Fall through and start with this process's path resolution.
                    }
                }
            }

            if (splash == null)
            {
                splash = new LoadingForm();
                splash.Show();
                splash.Activate();
                Application.DoEvents();
            }

            var appExe = ResolveMainAppExe(rootDir);
            var appDir = appExe is null ? null : Path.GetDirectoryName(appExe);
            if (appDir is null || appExe is null || !File.Exists(appExe))
            {
                HideSplash(splash);
                MessageBox.Show(
                    "找不到主程序（app\\main\\VideoDownloader.exe）。\n请重新复制完整发布包。\n\n" +
                    "Main program not found. Please reinstall the full package.",
                    "Video Downloader",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }

            splash.SetStatus("正在检查运行环境…");
            if (!HasWindowsDesktopRuntime10())
            {
                HideSplash(splash);
                var answer = MessageBox.Show(
                    "未检测到本机 .NET 10 Desktop Runtime，需要先安装才能运行。\n\n" +
                    ".NET 10 Desktop Runtime is required and was not found on this PC.\n\n" +
                    "是否立即下载并安装？（需要管理员权限）\nDownload and install now? (Administrator required)",
                    "Video Downloader",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);
                if (answer != DialogResult.Yes)
                    return 0;

                splash = new LoadingForm();
                splash.Show();
                splash.SetStatus("正在下载并安装 .NET 10…");
                Application.DoEvents();

                if (!InstallRuntime().GetAwaiter().GetResult())
                {
                    HideSplash(splash);
                    return 1;
                }

                if (!HasWindowsDesktopRuntime10())
                {
                    HideSplash(splash);
                    MessageBox.Show(
                        "安装完成但仍未检测到本机 .NET 10 Desktop Runtime。\n请重启电脑后再试，或手动安装：\n" +
                        RuntimeInstallerUrl,
                        "Video Downloader",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return 1;
                }
            }

            splash.SetStatus("正在启动主程序…");
            var start = new ProcessStartInfo
            {
                FileName = appExe,
                WorkingDirectory = appDir,
                UseShellExecute = false,
                Arguments = string.Join(" ", args.Select(QuoteArg))
            };
            var process = Process.Start(start);
            if (process is null)
            {
                HideSplash(splash);
                MessageBox.Show(
                    "无法启动主程序。",
                    "Video Downloader",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }

            splash.SetStatus("正在载入，请稍候…");
            BringMainWindowToFront(process, splash);
            return 0;
        }
        catch (Exception ex)
        {
            HideSplash(splash);
            MessageBox.Show(
                "启动失败 / Launch failed：\n" + ex.Message,
                "Video Downloader",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            HideSplash(splash);
        }
    }

    private static string ResolveMainAppExe(string installRoot)
    {
        foreach (var relative in new[] { AppRelativePathMain, AppRelativePathLegacy })
        {
            var candidate = Path.GetFullPath(Path.Combine(installRoot, relative));
            if (!File.Exists(candidate))
                continue;

            // Prefer real main app over the tiny app\ forwarder stub.
            if (relative.Equals(AppRelativePathLegacy, StringComparison.OrdinalIgnoreCase))
            {
                var mainSibling = Path.GetFullPath(Path.Combine(installRoot, AppRelativePathMain));
                if (File.Exists(mainSibling))
                    return mainSibling;
            }

            return candidate;
        }

        return null;
    }

    private static string PendingUpdatePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoDownloader",
            "pending-update.json");
    }

    private static string UpdateErrorLogPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VideoDownloader",
            "update-error.log");
    }

    private static bool TryApplyPendingUpdate(string launcherDir, LoadingForm splash)
    {
        var pendingPath = PendingUpdatePath();
        if (!File.Exists(pendingPath))
            return true;

        try
        {
            var json = File.ReadAllText(pendingPath);
            var pending = ReadPendingUpdate(json);
            var installRoot = !string.IsNullOrWhiteSpace(pending.InstallRoot)
                ? pending.InstallRoot
                : launcherDir;
            var zipPath = pending.ZipPath;
            if (string.IsNullOrWhiteSpace(installRoot))
                installRoot = launcherDir;

            installRoot = Path.GetFullPath(installRoot);
            if (!string.Equals(
                    Path.GetFullPath(launcherDir).TrimEnd('\\'),
                    installRoot.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase))
            {
                // Safety: only apply into the directory that started us.
                installRoot = Path.GetFullPath(launcherDir);
            }

            // Wait until the main app under this install root has exited (and force-kill if needed)
            // before touching side-by-side DLLs under app\main\.
            WaitForAppExit(splash, installRoot);
            StopHelperProcesses();
            Thread.Sleep(400);

            var staging = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VideoDownloader",
                "updates",
                "staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);

            if (!string.IsNullOrWhiteSpace(zipPath) && File.Exists(zipPath))
            {
                splash.SetStatus("正在解压更新包…");
                Application.DoEvents();
                ExtractZipSafe(zipPath, staging);

                splash.SetStatus("正在替换文件…");
                Application.DoEvents();
                string deferredLauncherSource = null;
                string currentLauncherPath = null;
                try
                {
                    if (Process.GetCurrentProcess().MainModule != null)
                        currentLauncherPath = Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName);
                }
                catch
                {
                    currentLauncherPath = Path.GetFullPath(Path.Combine(installRoot, LauncherExeName));
                }

                foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
                {
                    var relative = file.Substring(staging.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (relative.IndexOf("..", StringComparison.Ordinal) >= 0)
                        throw new InvalidDataException("Unsafe update path: " + relative);

                    var dest = Path.Combine(installRoot, relative);
                    var destFull = Path.GetFullPath(dest);
                    if (currentLauncherPath != null &&
                        string.Equals(destFull, currentLauncherPath, StringComparison.OrdinalIgnoreCase))
                    {
                        deferredLauncherSource = file;
                        continue;
                    }

                    CopyFileWithRetry(file, dest);
                }

                if (deferredLauncherSource != null)
                {
                    var newPath = Path.Combine(installRoot, DeferredLauncherFileName);
                    CopyFileWithRetry(deferredLauncherSource, newPath);
                    ScheduleLauncherReplace(installRoot, newPath);
                }
            }

            if (pending.Deletes != null)
            {
                foreach (var relative in pending.Deletes)
                {
                    if (string.IsNullOrWhiteSpace(relative) || relative.IndexOf("..", StringComparison.Ordinal) >= 0)
                        continue;
                    var target = Path.Combine(installRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                    DeleteFileWithRetry(target);
                }
            }

            try { Directory.Delete(staging, true); } catch { /* ignore */ }

            File.Delete(pendingPath);
            splash.SetStatus("更新完成，正在启动…");
            Application.DoEvents();
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(UpdateErrorLogPath()));
                File.WriteAllText(UpdateErrorLogPath(), ex.ToString());
            }
            catch
            {
                // ignore
            }

            HideSplash(splash);
            MessageBox.Show(
                "应用更新失败，将尝试启动当前版本。\n\n" + ex.Message,
                "Video Downloader",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return true; // continue launching old build
        }
    }

    /// <summary>
    /// Removes a leftover launcher sidecar from a previous update. Safe on every cold start:
    /// this run's deferred replace (if any) writes the file later, after this cleanup.
    /// Skip deleting the file we are currently executing (VideoBrowser.exe.new relay).
    /// </summary>
    private static void CleanupStaleDeferredLauncher(string installRoot)
    {
        string currentPath = null;
        try
        {
            if (Process.GetCurrentProcess().MainModule != null)
                currentPath = Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName);
        }
        catch
        {
            // ignore
        }

        foreach (var name in new[] { DeferredLauncherFileName, "VideoDownloader.exe.new" })
        {
            var path = Path.Combine(installRoot, name);
            if (currentPath != null &&
                string.Equals(Path.GetFullPath(path), currentPath, StringComparison.OrdinalIgnoreCase))
                continue;

            for (var attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    if (!File.Exists(path))
                        break;
                    File.Delete(path);
                    break;
                }
                catch
                {
                    Thread.Sleep(250);
                }
            }
        }
    }

    private static void ExtractZipSafe(string zipPath, string staging)
    {
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith("/"))
                    continue;
                var relative = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (relative.Contains("..") || Path.IsPathRooted(relative))
                    throw new InvalidDataException("Unsafe zip entry: " + entry.FullName);
                var dest = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                entry.ExtractToFile(dest, true);
            }
        }
    }

    private static void ScheduleLauncherReplace(string installRoot, string newLauncherPath)
    {
        var bat = Path.Combine(
            Path.GetTempPath(),
            "vd-replace-launcher-" + Guid.NewGuid().ToString("N") + ".cmd");
        var target = Path.Combine(installRoot, LauncherExeName);
        // Retry move after this process exits; if still stuck, delete the sidecar so it does not linger.
        var content =
            "@echo off\r\n" +
            "set \"SRC=" + newLauncherPath + "\"\r\n" +
            "set \"DST=" + target + "\"\r\n" +
            "set /a N=0\r\n" +
            ":retry\r\n" +
            "ping 127.0.0.1 -n 2 >nul\r\n" +
            "move /Y \"%SRC%\" \"%DST%\" >nul 2>nul\r\n" +
            "if not exist \"%SRC%\" goto done\r\n" +
            "set /a N+=1\r\n" +
            "if %N% LSS 15 goto retry\r\n" +
            "del /F /Q \"%SRC%\" >nul 2>nul\r\n" +
            ":done\r\n" +
            "del \"%~f0\"\r\n";
        File.WriteAllText(bat, content);
        Process.Start(new ProcessStartInfo
        {
            FileName = bat,
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static void WaitForAppExit(LoadingForm splash, string installRoot)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            var running = EnumerateMainAppProcesses(installRoot).ToList();
            try
            {
                if (running.Count == 0)
                    return;
            }
            finally
            {
                foreach (var process in running)
                {
                    try { process.Dispose(); } catch { /* ignore */ }
                }
            }

            splash.SetStatus("等待主程序退出…");
            Thread.Sleep(250);
        }

        splash.SetStatus("正在结束主程序…");
        Application.DoEvents();
        foreach (var process in EnumerateMainAppProcesses(installRoot))
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(8000);
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
                try { process.Dispose(); } catch { /* ignore */ }
            }
        }

        // One more short poll so OS releases loaded DLL handles.
        var releaseDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < releaseDeadline)
        {
            var leftover = EnumerateMainAppProcesses(installRoot).ToList();
            try
            {
                if (leftover.Count == 0)
                    return;
            }
            finally
            {
                foreach (var process in leftover)
                {
                    try { process.Dispose(); } catch { /* ignore */ }
                }
            }

            Thread.Sleep(200);
        }
    }

    /// <summary>
    /// Main UI process for this install: app\main\VideoDownloader.exe (current) or
    /// app\VideoDownloader.exe (legacy forwarder / older layouts). Never match other installs.
    /// </summary>
    private static IEnumerable<Process> EnumerateMainAppProcesses(string installRoot)
    {
        var rootPrefix = Path.GetFullPath(installRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var currentId = Process.GetCurrentProcess().Id;
        foreach (var process in Process.GetProcessesByName("VideoDownloader"))
        {
            if (process.Id == currentId)
            {
                process.Dispose();
                continue;
            }

            string module = null;
            try
            {
                module = process.MainModule != null ? process.MainModule.FileName : null;
            }
            catch
            {
                process.Dispose();
                continue;
            }

            if (string.IsNullOrWhiteSpace(module))
            {
                process.Dispose();
                continue;
            }

            string full;
            try
            {
                full = Path.GetFullPath(module);
            }
            catch
            {
                process.Dispose();
                continue;
            }

            if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                process.Dispose();
                continue;
            }

            if (!IsMainAppExecutablePath(full, rootPrefix))
            {
                process.Dispose();
                continue;
            }

            yield return process;
        }
    }

    private static bool IsMainAppExecutablePath(string fullPath, string installRootPrefix)
    {
        var main = Path.GetFullPath(Path.Combine(installRootPrefix, AppRelativePathMain));
        var legacy = Path.GetFullPath(Path.Combine(installRootPrefix, AppRelativePathLegacy));
        return string.Equals(fullPath, main, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fullPath, legacy, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyFileWithRetry(string source, string dest, int attempts = 40, int delayMs = 250)
    {
        Exception last = null;
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                var dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.Copy(source, dest, true);
                return;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                last = ex;
                Thread.Sleep(delayMs);
            }
        }

        throw new IOException("无法替换文件（仍被占用）：" + dest, last);
    }

    private static void DeleteFileWithRetry(string path, int attempts = 20, int delayMs = 250)
    {
        if (!File.Exists(path))
            return;

        Exception last = null;
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                last = ex;
                Thread.Sleep(delayMs);
            }
        }

        throw new IOException("无法删除文件（仍被占用）：" + path, last);
    }

    private static void StopHelperProcesses()
    {
        foreach (var name in new[] { "N_m3u8DL-RE", "ffmpeg", "ffprobe", "yt-dlp" })
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(3000);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void HideSplash(LoadingForm splash)
    {
        if (splash is null || splash.IsDisposed)
            return;
        try
        {
            splash.Close();
            splash.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    private static void BringMainWindowToFront(Process process, LoadingForm splash)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        IntPtr hwnd = IntPtr.Zero;
        while (DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            try
            {
                process.Refresh();
                if (process.HasExited)
                    break;

                hwnd = process.MainWindowHandle;
                if (hwnd != IntPtr.Zero)
                    break;
            }
            catch
            {
                break;
            }

            Thread.Sleep(100);
        }

        if (hwnd == IntPtr.Zero)
            return;

        try
        {
            AllowSetForegroundWindow(process.Id);
            if (IsIconic(hwnd))
                ShowWindow(hwnd, SwRestore);
            SetForegroundWindow(hwnd);
            splash?.SetStatus("即将打开…");
            Application.DoEvents();
            Thread.Sleep(150);
            SetForegroundWindow(hwnd);
        }
        catch
        {
            // Best-effort focus only.
        }
    }

    private static string QuoteArg(string arg)
    {
        if (string.IsNullOrEmpty(arg))
            return "\"\"";
        if (arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            return arg;
        return "\"" + arg.Replace("\"", "\\\"") + "\"";
    }

    private static bool HasWindowsDesktopRuntime10()
    {
        if (TryListRuntimesHasDesktop10())
            return true;

        foreach (var root in MachineDotNetRoots())
        {
            var shared = Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App");
            if (!Directory.Exists(shared))
                continue;

            foreach (var dir in Directory.EnumerateDirectories(shared))
            {
                var name = Path.GetFileName(dir);
                if (name is null ||
                    !name.StartsWith(RequiredMajor + ".", StringComparison.Ordinal))
                    continue;

                if (File.Exists(Path.Combine(dir, "Microsoft.WindowsDesktop.App.deps.json")) ||
                    File.Exists(Path.Combine(dir, "PresentationFramework.dll")) ||
                    Directory.EnumerateFiles(dir).Any())
                    return true;
            }
        }

        return false;
    }

    private static bool TryListRuntimesHasDesktop10()
    {
        var dotnet = ResolveDotNetHost();
        if (dotnet is null)
            return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = dotnet,
                Arguments = "--list-runtimes",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using (var process = Process.Start(psi))
            {
                if (process is null)
                    return false;
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(15000);
                if (process.ExitCode != 0)
                    return false;

                using (var reader = new StringReader(output))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.IndexOf("Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 &&
                            parts[1].StartsWith(RequiredMajor + ".", StringComparison.Ordinal))
                            return true;
                    }
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static string ResolveDotNetHost()
    {
        foreach (var root in MachineDotNetRoots())
        {
            var candidate = Path.Combine(root, "dotnet.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where",
                Arguments = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using (var process = Process.Start(psi))
            {
                if (process is null)
                    return null;
                var line = process.StandardOutput.ReadLine();
                process.WaitForExit(5000);
                if (!string.IsNullOrWhiteSpace(line) && File.Exists(line.Trim()))
                    return line.Trim();
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string[] MachineDotNetRoots()
    {
        var list = new List<string>();
        void Add(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            try
            {
                path = Path.GetFullPath(path);
            }
            catch
            {
                return;
            }

            var appDir = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(normalized, appDir, StringComparison.OrdinalIgnoreCase))
                return;

            if (Directory.Exists(path) &&
                !list.Exists(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase)))
                list.Add(path);
        }

        Add(Environment.GetEnvironmentVariable("DOTNET_ROOT"));
        Add(Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"));
        Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet"));
        Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "dotnet"));
        return list.ToArray();
    }

    private static async Task<bool> InstallRuntime()
    {
        var temp = Path.Combine(
            Path.GetTempPath(),
            "VideoDownloader-windowsdesktop-runtime-10-win-x64.exe");

        try
        {
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromMinutes(15);
                using (var response = await http.GetAsync(
                           RuntimeInstallerUrl,
                           HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = File.Create(temp))
                        await input.CopyToAsync(output).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "下载 .NET 10 失败 / Download failed：\n" + ex.Message +
                "\n\n请手动打开：\n" + RuntimeInstallerUrl,
                "Video Downloader",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = temp,
                Arguments = "/install /passive /norestart",
                UseShellExecute = true,
                Verb = "runas"
            };
            using (var process = Process.Start(psi))
            {
                if (process is null)
                {
                    MessageBox.Show(
                        "无法启动安装程序（可能取消了管理员授权）。",
                        "Video Downloader",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return false;
                }

                process.WaitForExit();
                if (process.ExitCode is 0 or 3010)
                    return true;

                MessageBox.Show(
                    "安装程序退出码：" + process.ExitCode +
                    "\n请手动安装：\n" + RuntimeInstallerUrl,
                    "Video Downloader",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return false;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "安装失败 / Install failed：\n" + ex.Message +
                "\n\n请手动安装：\n" + RuntimeInstallerUrl,
                "Video Downloader",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch
            {
                // ignore temp cleanup
            }
        }
    }

    /// <summary>
    /// Framework-built-in JSON for pending-update.json — keeps the net48 launcher
    /// free of NuGet side-by-side DLLs in the install root.
    /// </summary>
    private static PendingUpdateDto ReadPendingUpdate(string json)
    {
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
        {
            var ser = new DataContractJsonSerializer(typeof(PendingUpdateDto));
            return (PendingUpdateDto)ser.ReadObject(ms) ?? new PendingUpdateDto();
        }
    }

    [DataContract]
    private sealed class PendingUpdateDto
    {
        [DataMember(Name = "installRoot")]
        public string InstallRoot { get; set; }

        [DataMember(Name = "zipPath")]
        public string ZipPath { get; set; }

        [DataMember(Name = "deletes")]
        public string[] Deletes { get; set; }
    }
}
