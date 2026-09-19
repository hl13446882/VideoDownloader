using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using VideoDownloader.Core.Models;
using VideoDownloader.Core.Subtitles;
using VideoDownloader.Core.Subtitles.Contracts;
using VideoDownloader.Infrastructure.Configuration;

namespace VideoDownloader.Infrastructure.Subtitles.Audio;

public sealed class FfmpegMediaAudioDecoder : IMediaAudioDecoder
{
    private readonly AppOptions _options;

    public FfmpegMediaAudioDecoder(IOptions<AppOptions> options)
    {
        _options = options.Value;
    }

    public async Task<AudioChunk> DecodeAsync(
        MediaVariant variant,
        TimeSpan start,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (start < TimeSpan.Zero)
            start = TimeSpan.Zero;
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));

        // Keep each recognition request bounded. The scheduler is responsible for advancing windows.
        if (duration > TimeSpan.FromSeconds(90))
            duration = TimeSpan.FromSeconds(90);

        var track = SelectTrack(variant)
            ?? throw new InvalidOperationException("The selected media variant has no audio-capable track.");

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

        var headers = BuildHeaders(track.RequestContext);
        if (!string.IsNullOrEmpty(headers))
        {
            psi.ArgumentList.Add("-headers");
            psi.ArgumentList.Add(headers);
        }

        psi.ArgumentList.Add("-ss");
        psi.ArgumentList.Add(start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(track.SourceUrl.AbsoluteUri);
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

        try
        {
            await Task.WhenAll(copyTask, process.WaitForExitAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }

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

    private static MediaTrack? SelectTrack(MediaVariant variant)
    {
        return variant.Tracks.FirstOrDefault(t => t.Kind == MediaTrackKind.Audio && !t.IsMseTrack)
               ?? variant.Tracks.FirstOrDefault(t => t.Kind == MediaTrackKind.Combined && !t.IsMseTrack)
               ?? variant.Tracks.FirstOrDefault(t => t.Kind == MediaTrackKind.Audio)
               ?? variant.Tracks.FirstOrDefault(t => t.Kind == MediaTrackKind.Combined);
    }

    private static string BuildHeaders(RequestContext context)
    {
        var lines = new List<string>();

        Add("Referer", context.Referer);
        Add("Origin", context.Origin);
        Add("User-Agent", context.UserAgent);

        foreach (var pair in context.Headers)
        {
            if (pair.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                lines.Any(line => line.StartsWith(pair.Key + ":", StringComparison.OrdinalIgnoreCase)))
                continue;
            Add(pair.Key, pair.Value);
        }

        if (context.Cookies.Count > 0)
        {
            var cookie = string.Join("; ", context.Cookies.Select(c => c.Name + "=" + c.Value));
            Add("Cookie", cookie);
        }

        return lines.Count == 0 ? string.Empty : string.Join("\r\n", lines) + "\r\n";

        void Add(string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            // Header values are transported to FFmpeg as one CRLF-delimited argument.
            // Strip embedded CR/LF so captured page data cannot inject additional headers.
            var safe = value.Replace("\r", string.Empty, StringComparison.Ordinal)
                            .Replace("\n", string.Empty, StringComparison.Ordinal);
            lines.Add(name + ": " + safe);
        }
    }
}
