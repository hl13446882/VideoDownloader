using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace VideoDownloader.AppForwarder;

/// <summary>
/// Compatibility stub at app\VideoDownloader.exe for older VideoBrowser builds that still
/// launch that path after an update. Forwards to app\main\VideoDownloader.exe.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var mainExe = Path.GetFullPath(Path.Combine(appDir, "main", "VideoDownloader.exe"));
            if (!File.Exists(mainExe))
            {
                MessageBox.Show(
                    "找不到主程序（app\\main\\VideoDownloader.exe）。\n请重新安装完整发布包。\n\n" +
                    "Main program not found under app\\main. Please reinstall.",
                    "Video Downloader",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }

            var start = new ProcessStartInfo
            {
                FileName = mainExe,
                WorkingDirectory = Path.GetDirectoryName(mainExe) ?? appDir,
                UseShellExecute = false,
                Arguments = string.Join(" ", (args ?? Array.Empty<string>()).Select(QuoteArg))
            };
            using var process = Process.Start(start);
            return process is null ? 1 : 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "无法启动主程序：\n" + ex.Message,
                "Video Downloader",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static string QuoteArg(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";
        if (value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
