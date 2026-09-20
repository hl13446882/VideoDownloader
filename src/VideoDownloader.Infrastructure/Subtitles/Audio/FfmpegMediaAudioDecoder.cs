using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VideoDownloader.Core.Subtitles;
using VideoDownloader.Core.Subtitles.Contracts;
using VideoDownloader.Infrastructure.Configuration;

namespace VideoDownloader.Infrastructure.Subtitles.Audio;

/// <summary>
/// Extracts 16 kHz mono PCM directly from a completed local video file for subtitle ASR.
/// No HTTP/media-address path exists here by design.
/// </summary>
public sealed class FfmpegMediaAudioDecoder : IMediaAudioDecoder
{
    private readonly AppOptions _options;
    private readonly ILogger<FfmpegMediaAudioDecoder> _logger;

    public FfmpegMediaAudioDecoder(
        IOptions<AppOptions> options,
        ILogger<FfmpegMediaAudioDecoder> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AudioChunk> DecodeLocalFileAsync(
        string filePath,
        TimeSpan start,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("Local media path is required.", nameof(filePath));
        if (start < TimeSpan.Zero)
            start = TimeSpan.Zero;
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));
        if (duration > TimeSpan.FromSeconds(90))
            duration = TimeSpan.FromSeconds(90);

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            _logger.LogWarning("Subtitle decode media missing path={Path}", fullPath);
            throw new FileNotFoundException("Local media file was not found.", fullPath);
        }

        var appDir = PathExpander.ResolveAppDirectory();
        var installRoot = PathExpander.ResolveInstallRoot();
        var ffmpeg = PathExpander.Expand(_options.Ffmpeg.ExecutablePath);
        if (!File.Exists(ffmpeg))
        {
            _logger.LogWarning(
                "Subtitle FFmpeg missing expanded={Expanded} appDir={AppDir} installRoot={InstallRoot} configured={Configured}",
                ffmpeg,
                appDir,
                installRoot,
                _options.Ffmpeg.ExecutablePath);
            throw new FileNotFoundException("FFmpeg executable was not found.", ffmpeg);
        }

        _logger.LogInformation(
            "Subtitle FFmpeg decode start file={File} start={Start:g} duration={Duration:g} ffmpeg={Ffmpeg}",
            fullPath,
            start,
            duration,
            ffmpeg);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Accurate audio cut for ASR timestamps:
        // Input -ss alone can land on a prior keyframe and leave Whisper timestamps
        // shifted vs HTML5 video.currentTime. Use a coarse input seek + fine output -ss.
        var pad = start <= TimeSpan.Zero
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(Math.Min(3, start.TotalSeconds));
        var inputSeek = start - pad;

        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        if (inputSeek > TimeSpan.Zero)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(FormatSeconds(inputSeek));
        }
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(fullPath);
        if (pad > TimeSpan.Zero)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(FormatSeconds(pad));
        }
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(FormatSeconds(duration));
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add("16000");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("s16le");
        psi.ArgumentList.Add("-acodec");
        psi.ArgumentList.Add("pcm_s16le");
        psi.ArgumentList.Add("pipe:1");

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new InvalidOperationException("Failed to start FFmpeg for subtitle audio decoding.");

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort cancellation.
            }
        });

        await using var output = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await Task.WhenAll(copyTask, process.WaitForExitAsync(cancellationToken));

        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            _logger.LogWarning(
                "Subtitle FFmpeg decode failed exit={Exit} stderr={Stderr}",
                process.ExitCode,
                string.IsNullOrWhiteSpace(error) ? "(empty)" : error.Trim());
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"FFmpeg subtitle audio decode failed with exit code {process.ExitCode}."
                    : "FFmpeg subtitle audio decode failed: " + error.Trim());
        }

        var bytes = output.ToArray();
        var actualDuration = TimeSpan.FromSeconds(bytes.Length / (16000d * 2d));
        _logger.LogInformation(
            "Subtitle FFmpeg decode ok pcmBytes={Bytes} actualDuration={Duration:g}",
            bytes.Length,
            actualDuration);
        return new AudioChunk(bytes, start, start + actualDuration);
    }

    private static string FormatSeconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
}
