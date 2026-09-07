using System.Diagnostics;
using System.Text;
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
        var orderedTracks = tracks.OrderBy(t => t.Kind == MediaTrackKind.Audio ? 1 : 0).ToList();
        foreach (var track in orderedTracks)
        {
            AddInputHeaders(psi.ArgumentList, track.RequestContext);
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(GetInputArgument(track.SourceUrl));
        }

        for (var i = 0; i < orderedTracks.Count; i++)
        {
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture) + (orderedTracks[i].Kind == MediaTrackKind.Audio ? ":a:0" : orderedTracks[i].Kind == MediaTrackKind.Video ? ":v:0" : ""));
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
