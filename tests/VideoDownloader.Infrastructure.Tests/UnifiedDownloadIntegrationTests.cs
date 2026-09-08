using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Detection;
using VideoDownloader.Infrastructure.Download;
using VideoDownloader.Infrastructure.Ffmpeg;
using VideoDownloader.Infrastructure.Http;
using VideoDownloader.Infrastructure.Persistence;

namespace VideoDownloader.Infrastructure.Tests;

public class UnifiedDownloadIntegrationTests
{
    [Fact]
    public async Task LocalMedia_DetectsRefreshes_DownloadsRanges_ExtractsAudio_AndPreservesHistory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "VideoDownloader-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var sample = Path.Combine(dir, "sample.mp4");
        var ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        await Run(ffmpegPath, "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30", "-f", "lavfi", "-i", "sine=frequency=440", "-t", "24", "-c:v", "mpeg4", "-q:v", "1", "-c:a", "aac", sample);
        var bytes = await File.ReadAllBytesAsync(sample);
        Assert.True(bytes.Length > 8 * 1024 * 1024);
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var ranges = 0;
        var serve = Task.Run(async () =>
        {
            try
            {
                while (listener.IsListening)
                {
                    var ctx = await listener.GetContextAsync();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            long start = 0, end = bytes.Length - 1;
                            if (ctx.Request.Headers["Range"] is { } range)
                            {
                                var parts = range[6..].Split('-');
                                start = long.Parse(parts[0]);
                                if (parts.Length > 1 && parts[1].Length > 0) end = Math.Min(end, long.Parse(parts[1]));
                                ctx.Response.StatusCode = 206;
                                ctx.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{bytes.Length}";
                                if (end - start > 1000000) Interlocked.Increment(ref ranges);
                            }
                            ctx.Response.ContentType = "video/mp4";
                            ctx.Response.ContentLength64 = end - start + 1;
                            ctx.Response.Headers["Accept-Ranges"] = "bytes";
                            if (ctx.Request.HttpMethod != "HEAD") await ctx.Response.OutputStream.WriteAsync(bytes.AsMemory((int)start, (int)(end - start + 1)));
                        }
                        catch (Exception) { }
                        finally { ctx.Response.Close(); }
                    });
                }
            }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        });
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var url = new Uri($"http://localhost:{port}/sample.mp4");
            var page = new Uri($"http://localhost:{port}/page");
            var factory = new RequestMessageFactory();
            var pipeline = new UnifiedMediaPipeline(
                factory,
                Array.Empty<IExternalSiteResolver>(),
                Options.Create(new AppOptions()));
            var detected = new TaskCompletionSource<DetectedVideo>(TaskCreationOptions.RunContinuationsAsynchronously);
            pipeline.VideoDetected += (_, video) => detected.TrySetResult(video);
            var script = JsonSerializer.Serialize(new { caption = "Local test", media = new[] { url.AbsoluteUri } });
            await pipeline.ProbePageAsync(page, "Local test", script, RequestContext.CreateEmpty(), timeout.Token);
            await pipeline.CompleteDiscoveryAsync(timeout.Token);
            var video = await detected.Task.WaitAsync(timeout.Token);
            Assert.Contains(video.Variants, v => v.Tracks.All(t => t.Kind == MediaTrackKind.Audio));
            pipeline.Clear();
            detected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await pipeline.ProbePageAsync(page, "Wrong page title", JsonSerializer.Serialize(new { caption = "Refreshed", media = new[] { url.AbsoluteUri } }), RequestContext.CreateEmpty(), timeout.Token);
            await pipeline.CompleteDiscoveryAsync(timeout.Token);
            Assert.Equal("Refreshed", (await detected.Task.WaitAsync(timeout.Token)).DisplayTitle);
            var options = Options.Create(new AppOptions { Ffmpeg = new() { ExecutablePath = ffmpegPath }, Database = new() { Path = Path.Combine(dir, "history.db") } });
            var httpClients = Substitute.For<IHttpClientFactory>();
            var adapter = new M3u8DownloadAdapter(factory, new FfmpegAdapter(options, NullLogger<FfmpegAdapter>.Instance), httpClients);
            var combined = video.Variants.First(v => v.Tracks.Any(t => t.Kind == MediaTrackKind.Combined));
            var extractedAudio = video.Variants.First(v => v.Tracks.All(t => t.Kind == MediaTrackKind.Audio)).Tracks[0];
            var paired = MediaVariant.FromTracks("paired", null, combined.Height, null, "mkv",
                [combined.Tracks[0] with { Kind = MediaTrackKind.Video }, extractedAudio]);
            Assert.Equal(DownloadBackendKind.FfmpegRemux, new DownloadBackendRouter().Resolve(paired));
            foreach (var variant in video.Variants.Append(paired))
            {
                var audio = variant.Tracks.All(t => t.Kind == MediaTrackKind.Audio);
                var job = new DownloadJob { Id = Guid.NewGuid(), DisplayName = "Local", Variant = variant, PageUrl = page, TargetPath = Path.Combine(dir, audio ? "audio.mka" : variant.Tracks.Count > 1 ? "paired.mkv" : "video.mkv"), Status = DownloadStatus.Downloading };
                await adapter.DownloadAsync(job, () => Task.CompletedTask, timeout.Token);
                using var probe = JsonDocument.Parse(await Run(Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffprobe.exe"), "-v", "error", "-show_streams", "-of", "json", job.TargetPath));
                var kinds = probe.RootElement.GetProperty("streams").EnumerateArray().Select(s => s.GetProperty("codec_type").GetString()).ToArray();
                Assert.Contains("audio", kinds);
                Assert.Equal(!audio, kinds.Contains("video"));
                Assert.Single(kinds, kind => kind == "audio");
                if (!audio) Assert.Single(kinds, kind => kind == "video");
                Assert.Equal(new FileInfo(job.TargetPath).Length, job.DownloadedBytes);
                var repo = new SqliteDownloadRepository(options);
                await repo.InitializeAsync();
                job.Status = DownloadStatus.Removed;
                await repo.SaveAsync(job);
                var restored = await repo.GetByIdAsync(job.Id);
                Assert.Equal(DownloadStatus.Removed, restored!.Status);
                Assert.Equal(page, restored.PageUrl);
                Assert.Equal(url, restored.Variant.SourceUrl);
                Assert.True(File.Exists(job.TargetPath));
            }
            Assert.True(ranges >= 4, $"Expected parallel byte ranges; saw {ranges}");
            pipeline.Clear();
        }
        finally { listener.Stop(); await serve; }
    }

    private static async Task<string> Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await errors);
        return await output;
    }
}
