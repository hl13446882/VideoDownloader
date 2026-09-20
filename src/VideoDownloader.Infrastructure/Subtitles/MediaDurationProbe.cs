using System.Diagnostics;
using System.Globalization;
using VideoDownloader.Infrastructure.Configuration;

namespace VideoDownloader.Infrastructure.Subtitles;

internal static class MediaDurationProbe
{
    public static async Task<TimeSpan?> ProbeAsync(string filePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        var ffmpeg = PathExpander.Expand("%APPDIR%/ffmpeg/ffmpeg.exe");
        var dir = Path.GetDirectoryName(ffmpeg);
        var ffprobe = string.IsNullOrWhiteSpace(dir)
            ? "ffprobe"
            : Path.Combine(dir, "ffprobe.exe");
        if (!File.Exists(ffprobe))
            ffprobe = "ffprobe";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("format=duration");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
            psi.ArgumentList.Add(filePath);

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
                return null;

            if (!double.TryParse(
                    stdout.Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var seconds) ||
                !double.IsFinite(seconds) ||
                seconds <= 0)
                return null;

            return TimeSpan.FromSeconds(seconds);
        }
        catch
        {
            return null;
        }
    }
}
