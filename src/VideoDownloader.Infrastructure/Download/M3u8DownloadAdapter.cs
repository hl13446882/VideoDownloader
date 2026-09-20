using System.Diagnostics;
using System.Net.Http;
using System.Xml.Linq;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Licensing;
using VideoDownloader.Core.Errors;

namespace VideoDownloader.Infrastructure.Download;

public sealed class M3u8DownloadAdapter(
    IRequestMessageFactory requests,
    IFfmpegAdapter ffmpeg,
    IHttpClientFactory httpClients,
    LicenseService? license = null)
{
    public async Task DownloadAsync(DownloadJob job, Func<Task> checkpoint, CancellationToken ct)
    {
        if (license?.DownloadLimitBytes is int limit &&
            (job.Variant.TotalContentLength is not long size || size > limit))
            throw new DownloadException(ErrorCodes.LicenseLimit, "DEMO requires a verified size within the download limit before starting the external downloader.");
        var root = Path.Combine(Path.GetDirectoryName(job.TargetPath)!, ".parts", job.Id.ToString("N"));
        Directory.CreateDirectory(root);
        var progress = new long[job.Variant.Tracks.Count];
        job.TotalBytes = job.Variant.Tracks.All(t => t.ContentLength > 0) ? job.Variant.Tracks.Sum(t => t.ContentLength!.Value) : null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = job.Variant.Tracks.Select(async (track, index) =>
        {
            try { return await DownloadTrackAsync(track, index); }
            catch { stop.Cancel(); throw; }
        }).ToArray();
        var tracks = await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();
        job.Status = DownloadStatus.Muxing;
        await checkpoint();
        var staging = Path.Combine(root, "result" + Path.GetExtension(job.TargetPath));
        var muxed = false;
        try
        {
            await ffmpeg.RunMultiInputRemuxAsync(tracks, staging, ct);
            if (license?.DownloadLimitBytes is int demoLimit && new FileInfo(staging).Length > demoLimit)
            {
                File.Delete(staging);
                throw new VideoDownloader.Core.Errors.DownloadException(VideoDownloader.Core.Errors.ErrorCodes.LicenseLimit, "DEMO download limit is 10 MiB.");
            }
            ct.ThrowIfCancellationRequested();
            File.Move(staging, job.TargetPath, false);
            job.TotalBytes = job.DownloadedBytes = new FileInfo(job.TargetPath).Length;
            muxed = true;
        }
        finally
        {
            // Only wipe scratch after a successful final file exists; keep markers for Resume.
            if (muxed)
            {
                try
                {
                    if (Directory.Exists(root))
                        Directory.Delete(root, true);
                }
                catch
                {
                    // best-effort; DownloadEngine also cleans on complete
                }
            }
        }

        async Task<MediaTrack> DownloadTrackAsync(MediaTrack track, int index)
        {
            var folder = Path.Combine(root, index.ToString());
            var output = Path.Combine(folder, "output");
            Directory.CreateDirectory(output);
            var marker = Path.Combine(folder, "completed.path");
            if (File.Exists(marker))
            {
                var saved = await File.ReadAllTextAsync(marker, stop.Token);
                if (File.Exists(saved)) { progress[index] = new FileInfo(saved).Length; return track with { SourceUrl = new Uri(saved), RequestContext = RequestContext.CreateEmpty(), Hls = null }; }
            }
            var input = track.SourceUrl.AbsoluteUri;
            if (track.Container is not ("hls" or "dash"))
            {
                input = Path.Combine(folder, "input.mpd");
                XNamespace ns = "urn:mpeg:dash:schema:mpd:2011";
                var audio = track.Kind == MediaTrackKind.Audio && track.TrackId != "audio-extract";
                var manifest = new XDocument(new XElement(ns + "MPD", new XAttribute("type", "static"), new XAttribute("mediaPresentationDuration", "PT1S"),
                    new XElement(ns + "Period", new XElement(ns + "AdaptationSet", new XAttribute("mimeType", audio ? "audio/mp4" : "video/mp4"),
                        new XElement(ns + "Representation", new XAttribute("id", "0"), new XAttribute("bandwidth", "1000000"),
                            new XElement(ns + "BaseURL", track.SourceUrl.AbsoluteUri), new XElement(ns + "SegmentBase", new XElement(ns + "Initialization")))))));
                await File.WriteAllTextAsync(input, manifest.ToString(), stop.Token);
            }
            var appDir = PathExpander.ResolveAppDirectory();
            var psi = new ProcessStartInfo(Path.Combine(appDir, "M3u8", "N_m3u8DL-RE.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = folder };
            foreach (var arg in new[] { input, "--save-dir", output, "--tmp-dir", Path.Combine(folder, "segments"), "--save-name", "media", "--thread-count", "6", "--download-retry-count", "3", "--concurrent-download", "--auto-select", "--no-ansi-color", "--no-log", "--write-meta-json", "false", "--disable-update-check", "--ffmpeg-binary-path", Path.Combine(appDir, "ffmpeg", "ffmpeg.exe") }) psi.ArgumentList.Add(arg);
            if (track.Container is not ("hls" or "dash")) psi.ArgumentList.Add("--binary-merge");
            else { psi.ArgumentList.Add("-M"); psi.ArgumentList.Add("format=mkv"); }
            if (track.TrackId.StartsWith("dash:",StringComparison.Ordinal))
            {
                var audioTrack = track.Kind == MediaTrackKind.Audio;
                psi.ArgumentList.Add(audioTrack ? "--select-audio" : "--select-video");
                psi.ArgumentList.Add("id=^" + System.Text.RegularExpressions.Regex.Escape(track.TrackId[5..]) + "$:for=best");
                psi.ArgumentList.Add(audioTrack ? "--drop-video" : "--drop-audio");
                psi.ArgumentList.Add("all");
                psi.ArgumentList.Add("--drop-subtitle");
                psi.ArgumentList.Add("all");
            }
            using var request = requests.Create(MediaVariant.FromTracks("download", null, null, null, track.Container, [track]), HttpMethod.Get, track.SourceUrl);
            foreach (var header in request.Headers.Where(h => !h.Key.Equals("Range", StringComparison.OrdinalIgnoreCase) && !h.Key.Equals("If-Range", StringComparison.OrdinalIgnoreCase)))
            { psi.ArgumentList.Add("-H"); psi.ArgumentList.Add($"{header.Key}: {string.Join(", ", header.Value)}"); }

            // Clear-key AES only: inject custom HLS key/IV. Plain and DRM tracks never enter this branch.
            await AppendClearKeyArgsAsync(psi, track, stop.Token);

            using var process = Process.Start(psi)!;
            using var cancel = stop.Token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
            var error = new Queue<string>();
            using var drainCancellation = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            async Task Drain(StreamReader reader)
            {
                try { while (await reader.ReadLineAsync(drainCancellation.Token) is { } line) { lock (error) { error.Enqueue(line); while (error.Count > 20) error.Dequeue(); } } }
                catch (OperationCanceledException) when (drainCancellation.IsCancellationRequested) { }
            }
            var readers = Task.WhenAll(Drain(process.StandardOutput), Drain(process.StandardError));
            try
            {
                await ProcessProgress.WaitForExitAsync(process, () =>
                {
                    var segmentDir = Path.Combine(folder, "segments");
                    long bytes = 0;
                    if (Directory.Exists(segmentDir))
                        try { bytes = Directory.EnumerateFiles(segmentDir, "*", SearchOption.AllDirectories).Sum(ProcessProgress.FileLength); } catch (IOException) { }
                    Interlocked.Exchange(ref progress[index], Math.Max(progress[index], track.ContentLength > 0 ? Math.Min(bytes, track.ContentLength.Value) : bytes));
                    job.DownloadedBytes = progress.Sum();
                    if (license?.DownloadLimitBytes is int runningLimit && job.DownloadedBytes > runningLimit)
                    {
                        stop.Cancel();
                        throw new DownloadException(ErrorCodes.LicenseLimit, "DEMO download limit exceeded.");
                    }
                    return bytes + Directory.EnumerateFiles(output).Sum(ProcessProgress.FileLength);
                }, TimeSpan.FromMinutes(2), stop.Token);
                await readers.WaitAsync(TimeSpan.FromSeconds(5), stop.Token);
            }
            finally
            {
                drainCancellation.Cancel();
                await readers;
            }
            stop.Token.ThrowIfCancellationRequested();
            var files = Directory.GetFiles(output).Where(p => new[] { ".mp4", ".m4a", ".mkv", ".webm", ".ts", ".aac" }.Contains(Path.GetExtension(p))).ToArray();
            if (process.ExitCode != 0 || files.Length != 1 || new FileInfo(files[0]).Length == 0)
            {
                var message = string.Join("\n", error);
                throw new DownloadException(message.Contains("403", StringComparison.Ordinal) ? ErrorCodes.Http403 :
                    message.Contains("404", StringComparison.Ordinal) ? ErrorCodes.Http404 : ErrorCodes.InvalidFormat,
                    "N_m3u8DL-RE failed: " + message);
            }
            await File.WriteAllTextAsync(marker, files[0], stop.Token);
            Interlocked.Exchange(ref progress[index], new FileInfo(files[0]).Length);
            job.DownloadedBytes = progress.Sum();
            await checkpoint();
            return track with { SourceUrl = new Uri(files[0]), RequestContext = RequestContext.CreateEmpty(), Hls = null };
        }
    }

    /// <summary>
    /// Applies N_m3u8DL-RE custom HLS decrypt args only when <see cref="MediaTrack.Hls"/>
    /// carries clear-key AES metadata. All other pages keep the previous argument set.
    /// </summary>
    private async Task AppendClearKeyArgsAsync(ProcessStartInfo psi, MediaTrack track, CancellationToken ct)
    {
        if (track.Hls is not { HasClearKeyEncryption: true, Encryption: { KeyUri: not null } enc })
            return;

        var method = enc.Method.Replace('-', '_').ToUpperInvariant();
        byte[] keyBytes;
        try
        {
            using var client = httpClients.CreateClient("media-primary");
            using var keyRequest = requests.Create(
                MediaVariant.FromTracks("hls-key", null, null, null, "hls", [track]),
                HttpMethod.Get,
                enc.KeyUri);
            using var keyResponse = await client.SendAsync(keyRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            keyResponse.EnsureSuccessStatusCode();
            keyBytes = await keyResponse.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex)
        {
            throw new DownloadException(ErrorCodes.InvalidFormat, "Failed to fetch HLS clear-key: " + ex.Message);
        }

        if (keyBytes.Length == 0)
            throw new DownloadException(ErrorCodes.InvalidFormat, "HLS clear-key response was empty.");

        psi.ArgumentList.Add("--custom-hls-method");
        psi.ArgumentList.Add(method);
        psi.ArgumentList.Add("--custom-hls-key");
        psi.ArgumentList.Add(Convert.ToHexString(keyBytes));
        if (!string.IsNullOrWhiteSpace(enc.IvHex))
        {
            var iv = enc.IvHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? enc.IvHex[2..] : enc.IvHex;
            if (iv.Length > 0 && iv.All(Uri.IsHexDigit))
            {
                psi.ArgumentList.Add("--custom-hls-iv");
                psi.ArgumentList.Add(iv);
            }
        }
    }
}
