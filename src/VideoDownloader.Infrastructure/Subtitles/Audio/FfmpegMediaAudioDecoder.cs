using System.Diagnostics;
using System.Globalization;
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

    public FfmpegMediaAudioDecoder(IOptions<AppOptions> options)
    {
        _options = options.Value;
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
            throw new FileNotFoundException("Local media file was not found.", fullPath);

        var ffmpeg = PathExpander.Expand(_options.Ffmpeg.ExecutablePath);
        if (!File.Exists(ffmpeg))
            throw new FileNotFoundException("FFmpeg executable was not found.", ffmpeg);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-ss");
        psi.ArgumentList.Add(start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(fullPath);
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
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
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"FFmpeg subtitle audio decode failed with exit code {process.ExitCode}."
                    : "FFmpeg subtitle audio decode failed: " + error.Trim());

        var bytes = output.ToArray();
        var actualDuration = TimeSpan.FromSeconds(bytes.Length / (16000d * 2d));
        return new AudioChunk(bytes, start, start + actualDuration);
    }
}
