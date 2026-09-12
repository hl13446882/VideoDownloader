using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoDownloader.Launcher;

internal static class Program
{
    private const string AppRelativePath = @"app\VideoDownloader.exe";
    private const string RequiredMajor = "10";
    private const string RuntimeInstallerUrl =
        "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe";

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            var rootDir = AppDomain.CurrentDomain.BaseDirectory;
            var appExe = Path.GetFullPath(Path.Combine(rootDir, AppRelativePath));
            var appDir = Path.GetDirectoryName(appExe);
            if (appDir is null || !File.Exists(appExe))
            {
                MessageBox.Show(
                    "找不到主程序（app\\VideoDownloader.exe）。\n请重新复制完整发布包。\n\n" +
                    "Main program not found. Please reinstall the full package.",
                    "Video Downloader",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }

            if (!HasWindowsDesktopRuntime10())
            {
                var answer = MessageBox.Show(
                    "未检测到本机 .NET 10 Desktop Runtime，需要先安装才能运行。\n\n" +
                    ".NET 10 Desktop Runtime is required and was not found on this PC.\n\n" +
                    "是否立即下载并安装？（需要管理员权限）\nDownload and install now? (Administrator required)",
                    "Video Downloader",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);
                if (answer != DialogResult.Yes)
                    return 0;

                if (!InstallRuntime().GetAwaiter().GetResult())
                    return 1;

                if (!HasWindowsDesktopRuntime10())
                {
                    MessageBox.Show(
                        "安装完成但仍未检测到本机 .NET 10 Desktop Runtime。\n请重启电脑后再试，或手动安装：\n" +
                        RuntimeInstallerUrl,
                        "Video Downloader",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return 1;
                }
            }

            var start = new ProcessStartInfo
            {
                FileName = appExe,
                WorkingDirectory = appDir,
                UseShellExecute = false,
                Arguments = string.Join(" ", args.Select(QuoteArg))
            };
            Process.Start(start);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "启动失败 / Launch failed：\n" + ex.Message,
                "Video Downloader",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
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
        // Machine environment only — never the app folder.
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

                // Shared framework folders ship deps/runtimeconfig; do not require a
                // Microsoft.WindowsDesktop.App.dll (it is not present in install trees).
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

                // e.g. Microsoft.WindowsDesktop.App 10.0.9 [...]
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

        // PATH lookup (still machine host, not app-dir).
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
        var list = new System.Collections.Generic.List<string>();
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

            // Never treat the application directory as a runtime root.
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
                // 0 = success, 3010 = success reboot required
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
}
