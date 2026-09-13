using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Errors;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Logging;

namespace VideoDownloader.Infrastructure.Ffmpeg;

public sealed class FfmpegAdapter : IFfmpegAdapter
{
    private readonly AppOptions _options;
    private readonly ILogger<FfmpegAdapter> _logger;
    private const int StderrTailLimit = 2000;
    private static readonly TimeSpan GracefulExitTimeout = TimeSpan.FromSeconds(8);

    public FfmpegAdapter(IOptions<AppOptions> options, ILogger<FfmpegAdapter> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task RunRemuxAsync(Uri inputUrl, RequestContext context, string outputPath, CancellationToken ct)
    {
        var ffmpegPath = PathExpander.Expand(_options.Ffmpeg.ExecutablePath);
        if (!File.Exists(ffmpegPath) && !IsOnPath(ffmpegPath))
            throw new DownloadException(ErrorCodes.FfmpegNotFound, "FFmpeg executable was not found.");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("warning");
        psi.ArgumentList.Add("-dn");
        AddInputHeaders(psi.ArgumentList, context);
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(GetInputArgument(inputUrl));
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-map_metadata");
        psi.ArgumentList.Add("-1");
        psi.ArgumentList.Add(outputPath);

        using var process = Process.Start(psi)
            ?? throw new DownloadException(ErrorCodes.FfmpegFailed, "Unable to start FFmpeg.");

        var stderrTail = new StringBuilder();
        var stderrTask = ConsumeStderrAsync(process, stderrTail, ct);
        var stdoutTask = ConsumeOutputAsync(process, ct);

        await using var reg = ct.Register(() => _ = RequestStopAsync(process));

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            await RequestStopAsync(process);
            throw;
        }

        await stderrTask;
        await stdoutTask;

        if (process.ExitCode != 0 && !ct.IsCancellationRequested)
        {
            var tail = SanitizedLogger.SanitizeMessage(stderrTail.ToString());
            if (tail.Length > StderrTailLimit)
                tail = tail[^StderrTailLimit..];

            _logger.LogWarning(
                "FFmpeg failed exit={ExitCode} url={Url}",
                process.ExitCode,
                SanitizedLogger.SanitizeUrl(inputUrl.ToString()));

            throw new DownloadException(ErrorCodes.FfmpegFailed, tail);
        }

        if (!File.Exists(outputPath))
            throw new DownloadException(ErrorCodes.FfmpegFailed, "FFmpeg finished without creating output file.");
    }

    public async Task RunMultiInputRemuxAsync(
        IReadOnlyList<MediaTrack> tracks,
        string outputPath,
        CancellationToken ct)
    {
        if (tracks.Count == 0)
            throw new DownloadException(ErrorCodes.FfmpegFailed, "No tracks to remux.");

        var ffmpegPath = PathExpander.Expand(_options.Ffmpeg.ExecutablePath);
        if (!File.Exists(ffmpegPath) && !IsOnPath(ffmpegPath))
            throw new DownloadException(ErrorCodes.FfmpegNotFound, "FFmpeg executable was not found.");

        var ffprobePath = Path.Combine(Path.GetDirectoryName(ffmpegPath)!, "ffprobe.exe");
        if (!File.Exists(ffprobePath))
            ffprobePath = "ffprobe";

        var probed = new List<(MediaTrack Track, bool HasVideo, bool HasAudio)>(tracks.Count);
        foreach (var track in tracks)
        {
            var streams = await ProbeStreamKindsAsync(ffprobePath, GetInputArgument(track.SourceUrl), ct);
            probed.Add((track, streams.HasVideo, streams.HasAudio));
        }

        var selected = SelectTracksForRemux(probed);
        if (selected.Count == 0)
            throw new DownloadException(ErrorCodes.FfmpegFailed, "No usable tracks to remux.");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("warning");
        psi.ArgumentList.Add("-dn");
        var orderedTracks = selected.OrderBy(t => t.Kind == MediaTrackKind.Audio ? 1 : 0).ToList();
        foreach (var track in orderedTracks)
        {
            AddInputHeaders(psi.ArgumentList, track.RequestContext);
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(GetInputArgument(track.SourceUrl));
        }

        // Single self-contained input: copy every stream (already has audio when present).
        // Multi-input: map video from non-audio inputs and audio only from Audio tracks.
        if (orderedTracks.Count == 1)
        {
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("0");
        }
        else
        {
            for (var i = 0; i < orderedTracks.Count; i++)
            {
                psi.ArgumentList.Add("-map");
                psi.ArgumentList.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture) + (orderedTracks[i].Kind == MediaTrackKind.Audio ? ":a:0" : orderedTracks[i].Kind == MediaTrackKind.Video ? ":v:0" : ""));
            }
        }

        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-map_metadata");
        psi.ArgumentList.Add("-1");
        psi.ArgumentList.Add(outputPath);

        using var process = Process.Start(psi)
            ?? throw new DownloadException(ErrorCodes.FfmpegFailed, "Unable to start FFmpeg.");

        var stderrTail = new StringBuilder();
        using var outputStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stderrTask = ConsumeStderrAsync(process, stderrTail, outputStop.Token);
        var stdoutTask = ConsumeOutputAsync(process, outputStop.Token);
        await using var reg = ct.Register(() => _ = RequestStopAsync(process));

        try
        {
            await VideoDownloader.Infrastructure.Download.ProcessProgress.WaitForExitAsync(process,
                () => VideoDownloader.Infrastructure.Download.ProcessProgress.FileLength(outputPath), TimeSpan.FromMinutes(2), ct);
        }
        catch (OperationCanceledException)
        {
            await RequestStopAsync(process);
            throw;
        }
        finally
        {
            outputStop.CancelAfter(TimeSpan.FromSeconds(5));
            try { await Task.WhenAll(stderrTask, stdoutTask); }
            catch (OperationCanceledException) when (outputStop.IsCancellationRequested) { }
        }

        if (process.ExitCode != 0 && !ct.IsCancellationRequested)
            throw new DownloadException(ErrorCodes.FfmpegFailed, SanitizedLogger.SanitizeMessage(stderrTail.ToString()));

        if (!File.Exists(outputPath))
            throw new DownloadException(ErrorCodes.FfmpegFailed, "FFmpeg finished without creating output file.");
    }

    public async Task RunAlbumSlideshowAsync(
        IReadOnlyList<string> imagePaths,
        string audioPath,
        string outputPath,
        CancellationToken ct)
    {
        if (imagePaths.Count == 0)
            throw new DownloadException(ErrorCodes.FfmpegFailed, "Album slideshow requires at least one image.");
        if (!File.Exists(audioPath))
            throw new DownloadException(ErrorCodes.FfmpegFailed, "Album slideshow audio file was not found.");

        var ffmpegPath = PathExpander.Expand(_options.Ffmpeg.ExecutablePath);
        if (!File.Exists(ffmpegPath) && !IsOnPath(ffmpegPath))
            throw new DownloadException(ErrorCodes.FfmpegNotFound, "FFmpeg executable was not found.");

        var ffprobePath = Path.Combine(Path.GetDirectoryName(ffmpegPath)!, "ffprobe.exe");
        if (!File.Exists(ffprobePath))
            ffprobePath = "ffprobe";

        var duration = await ProbeDurationSecondsAsync(ffprobePath, audioPath, ct);
        if (duration is null or <= 0.05)
            duration = Math.Max(1.0, imagePaths.Count * 2.0);
        var perImage = Math.Max(0.2, duration.Value / imagePaths.Count);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("warning");

        var perImageText = perImage.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var image in imagePaths)
        {
            psi.ArgumentList.Add("-loop");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(perImageText);
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(image);
        }

        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(audioPath);

        var filter = new StringBuilder();
        for (var i = 0; i < imagePaths.Count; i++)
        {
            filter.Append('[')
                .Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(":v]scale=1280:720:force_original_aspect_ratio=decrease,")
                .Append("pad=1280:720:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,format=yuv420p[v")
                .Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append("];");
        }

        for (var i = 0; i < imagePaths.Count; i++)
            filter.Append("[v").Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(']');
        filter.Append("concat=n=")
            .Append(imagePaths.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(":v=1:a=0[vout]");

        psi.ArgumentList.Add("-filter_complex");
        psi.ArgumentList.Add(filter.ToString());
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("[vout]");
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add(imagePaths.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":a:0?");
        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add("libx264");
        psi.ArgumentList.Add("-preset");
        psi.ArgumentList.Add("veryfast");
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("aac");
        psi.ArgumentList.Add("-shortest");
        psi.ArgumentList.Add("-map_metadata");
        psi.ArgumentList.Add("-1");
        psi.ArgumentList.Add(outputPath);

        using var process = Process.Start(psi)
            ?? throw new DownloadException(ErrorCodes.FfmpegFailed, "Unable to start FFmpeg.");

        var stderrTail = new StringBuilder();
        using var outputStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stderrTask = ConsumeStderrAsync(process, stderrTail, outputStop.Token);
        var stdoutTask = ConsumeOutputAsync(process, outputStop.Token);
        await using var reg = ct.Register(() => _ = RequestStopAsync(process));

        try
        {
            await VideoDownloader.Infrastructure.Download.ProcessProgress.WaitForExitAsync(process,
                () => VideoDownloader.Infrastructure.Download.ProcessProgress.FileLength(outputPath), TimeSpan.FromMinutes(5), ct);
        }
        catch (OperationCanceledException)
        {
            await RequestStopAsync(process);
            throw;
        }
        finally
        {
            outputStop.CancelAfter(TimeSpan.FromSeconds(5));
            try { await Task.WhenAll(stderrTask, stdoutTask); }
            catch (OperationCanceledException) when (outputStop.IsCancellationRequested) { }
        }

        if (process.ExitCode != 0 && !ct.IsCancellationRequested)
            throw new DownloadException(ErrorCodes.FfmpegFailed, SanitizedLogger.SanitizeMessage(stderrTail.ToString()));

        if (!File.Exists(outputPath))
            throw new DownloadException(ErrorCodes.FfmpegFailed, "FFmpeg finished without creating album output file.");
    }

    /// <summary>
    /// When a primary already embeds audio, drop companion Audio tracks.
    /// When no usable video-only primary exists but a self-contained A+V file does, use that alone.
    /// </summary>
    internal static IReadOnlyList<MediaTrack> SelectTracksForRemux(
        IReadOnlyList<(MediaTrack Track, bool HasVideo, bool HasAudio)> probed)
    {
        if (probed.Count <= 1)
            return probed.Select(p => p.Track).ToArray();

        IReadOnlyList<(MediaTrack Track, bool HasVideo, bool HasAudio)> candidates = probed;

        // Already-muxed primary (Combined / video with embedded audio): do not merge extra audio.
        if (candidates.Any(p =>
                p.Track.Kind != MediaTrackKind.Audio &&
                p.HasAudio))
        {
            candidates = candidates
                .Where(p => p.Track.Kind != MediaTrackKind.Audio)
                .ToArray();
        }

        var videoOnlyPrimary = candidates.Any(p =>
            p.Track.Kind != MediaTrackKind.Audio &&
            p.HasVideo &&
            !p.HasAudio);

        if (!videoOnlyPrimary)
        {
            var selfContained = candidates
                .Where(p => p.HasVideo && p.HasAudio)
                .OrderByDescending(p => p.Track.Kind == MediaTrackKind.Combined ? 1 : 0)
                .ThenByDescending(p => p.Track.ContentLength ?? 0)
                .Select(p => p.Track)
                .FirstOrDefault();
            if (selfContained is not null)
                return [selfContained];
        }

        return candidates.Select(p => p.Track).ToArray();
    }

    public async Task<(double? DurationSec, int? Height)> ProbeLocalFileAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return (null, null);

        var ffmpegPath = PathExpander.Expand(_options.Ffmpeg.ExecutablePath);
        var ffprobePath = Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? string.Empty, "ffprobe.exe");
        if (!File.Exists(ffprobePath))
            ffprobePath = "ffprobe";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffprobePath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("format=duration:stream=codec_type,height");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("json");
            psi.ArgumentList.Add(path);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var process = Process.Start(psi);
            if (process is null)
                return (null, null);

            var stdout = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
                return (null, null);

            return ParseProbeJson(stdout);
        }
        catch (OperationCanceledException)
        {
            return (null, null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ffprobe failed for {Path}", path);
            return (null, null);
        }
    }

    public async Task<bool> TryExtractThumbnailAsync(string videoPath, string jpegPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath) || string.IsNullOrWhiteSpace(jpegPath))
            return false;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(jpegPath)!);
            var tmp = jpegPath + ".part.jpg";
            if (File.Exists(tmp))
                File.Delete(tmp);

            var ffmpegPath = PathExpander.Expand(_options.Ffmpeg.ExecutablePath);
            if (!File.Exists(ffmpegPath))
                return false;

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(videoPath);
            psi.ArgumentList.Add("-frames:v");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-q:v");
            psi.ArgumentList.Add("4");
            psi.ArgumentList.Add(tmp);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var process = Process.Start(psi);
            if (process is null)
                return false;

            _ = await process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length < 32)
            {
                try { File.Delete(tmp); } catch { /* ignore */ }
                return false;
            }

            if (File.Exists(jpegPath))
                File.Delete(jpegPath);
            File.Move(tmp, jpegPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "thumbnail extract failed for {Path}", videoPath);
            return false;
        }
    }

    internal static (double? DurationSec, int? Height) ParseProbeJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            double? duration = null;
            int? height = null;

            if (root.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var durationEl))
            {
                if (durationEl.ValueKind == JsonValueKind.Number && durationEl.TryGetDouble(out var n) && n > 0)
                    duration = n;
                else if (durationEl.ValueKind == JsonValueKind.String &&
                         double.TryParse(durationEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
                         parsed > 0)
                    duration = parsed;
            }

            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (!stream.TryGetProperty("codec_type", out var kind) ||
                        !string.Equals(kind.GetString(), "video", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (stream.TryGetProperty("height", out var heightEl) &&
                        heightEl.ValueKind == JsonValueKind.Number &&
                        heightEl.TryGetInt32(out var h) &&
                        h > 0)
                    {
                        height = h;
                        break;
                    }
                }
            }

            return (duration, height);
        }
        catch
        {
            return (null, null);
        }
    }

    private static async Task<(bool HasVideo, bool HasAudio)> ProbeStreamKindsAsync(
        string ffprobePath,
        string mediaPath,
        CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffprobePath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("stream=codec_type");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("csv=p=0");
            psi.ArgumentList.Add(mediaPath);

            using var process = Process.Start(psi);
            if (process is null) return (false, false);
            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0) return (false, false);

            var hasVideo = false;
            var hasAudio = false;
            foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.Equals("video", StringComparison.OrdinalIgnoreCase))
                    hasVideo = true;
                else if (line.Equals("audio", StringComparison.OrdinalIgnoreCase))
                    hasAudio = true;
            }

            return (hasVideo, hasAudio);
        }
        catch
        {
            return (false, false);
        }
    }

    private static async Task<double?> ProbeDurationSecondsAsync(string ffprobePath, string mediaPath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffprobePath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("format=duration");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
            psi.ArgumentList.Add(mediaPath);

            using var process = Process.Start(psi);
            if (process is null) return null;
            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0) return null;
            var text = stdout.Trim();
            return double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                ? seconds
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string GetInputArgument(Uri uri) =>
        uri.IsFile ? uri.LocalPath : uri.ToString();

    private static void AddInputHeaders(ICollection<string> args, RequestContext context)
    {
        var headers = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(context.UserAgent))
            headers.Append("User-Agent: ").Append(context.UserAgent).Append("\r\n");
        if (!string.IsNullOrWhiteSpace(context.Referer))
            headers.Append("Referer: ").Append(context.Referer).Append("\r\n");
        if (!string.IsNullOrWhiteSpace(context.Origin))
            headers.Append("Origin: ").Append(context.Origin).Append("\r\n");

        foreach (var (name, value) in context.Headers)
        {
            if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
                continue;

            headers.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        if (headers.Length == 0)
            return;

        args.Add("-headers");
        args.Add(headers.ToString());
    }

    private static async Task ConsumeStderrAsync(Process process, StringBuilder tail, CancellationToken ct)
    {
        var buffer = new char[512];
        while (true)
        {
            var read = await process.StandardError.ReadAsync(buffer, ct);
            if (read <= 0)
                break;

            tail.Append(buffer, 0, read);
            if (tail.Length > StderrTailLimit * 2)
                tail.Remove(0, tail.Length - StderrTailLimit);
        }
    }

    private static async Task ConsumeOutputAsync(Process process, CancellationToken ct)
    {
        var buffer = new char[512];
        while (true)
        {
            var read = await process.StandardOutput.ReadAsync(buffer, ct);
            if (read <= 0)
                break;
        }
    }

    private static async Task RequestStopAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("q");
                await process.StandardInput.FlushAsync();
            }

            using var timeout = new CancellationTokenSource(GracefulExitTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    private static bool IsOnPath(string fileName) =>
        !fileName.Contains('\\') && !fileName.Contains('/');
}
