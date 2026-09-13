using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Download;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Download;
using VideoDownloader.Infrastructure.Ffmpeg;
using VideoDownloader.Infrastructure.Http;
using VideoDownloader.Infrastructure.Licensing;
using VideoDownloader.Infrastructure.LocalLibrary;

namespace VideoDownloader.Infrastructure.Tests;

public class CancelScratchCleanupTests
{
    [Fact]
    public async Task Cancel_DuringDouyinRemuxDownload_DeletesPartsDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "VideoDownloader-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var payloadBytes = 512 * 1024;
        var serve = Task.Run(() => ServeSlow(listener, payloadBytes));
        try
        {
            var options = Options.Create(new AppOptions
            {
                Download = new DownloadOptions
                {
                    DefaultSavePath = dir,
                    RetryCount = 0,
                    FailedRetryIntervalSeconds = 0,
                    MaxConcurrentDownloads = 1
                },
                Ffmpeg = new FfmpegOptions
                {
                    ExecutablePath = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe")
                }
            });
            var context = RequestContext.CreateEmpty();
            var videoUrl = new Uri($"http://localhost:{port}/video.bin");
            var audioUrl = new Uri($"http://localhost:{port}/audio.bin");
            var variant = MediaVariant.FromTracks(
                "douyin-av",
                1080,
                1920,
                2_000_000,
                "mp4",
                [
                    new MediaTrack("video", MediaTrackKind.Video, videoUrl, "h264", "mp4", 1_500_000, payloadBytes, context),
                    new MediaTrack("audio", MediaTrackKind.Audio, audioUrl, "aac", "mp4", 128_000, payloadBytes, context)
                ]);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var downloader = new HttpMediaDownloader(
                http,
                null,
                new RequestMessageFactory(options),
                options,
                NullLogger<HttpMediaDownloader>.Instance);
            var ffmpeg = new FfmpegAdapter(options, NullLogger<FfmpegAdapter>.Instance);
            var m3u8 = new M3u8DownloadAdapter(
                new RequestMessageFactory(options),
                ffmpeg,
                Substitute.For<IHttpClientFactory>());
            var contexts = Substitute.For<IRequestContextProvider>();
            contexts.CaptureCurrentContext(Arg.Any<Uri?>(), Arg.Any<Uri>()).Returns(context);
            contexts.RefreshContextAsync(
                    Arg.Any<Uri?>(),
                    Arg.Any<Uri>(),
                    Arg.Any<RequestContext?>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<bool>())
                .Returns(context);
            using var engine = new DownloadEngine(
                downloader,
                m3u8,
                ffmpeg,
                new DownloadJobStateMachine(),
                new MemoryDownloadRepository(),
                contexts,
                new DownloadBackendRouter(),
                options,
                NullLogger<DownloadEngine>.Instance,
                new LicenseService(new HttpClient(), options, NullLogger<LicenseService>.Instance),
                new LocalVideoThumbnailStore(ffmpeg, NullLogger<LocalVideoThumbnailStore>.Instance));

            await engine.EnqueueAsync(variant, "cancel-scratch", new Uri("https://www.douyin.com/video/1"));
            var job = await WaitForAsync(
                () => engine.GetActiveJobs().FirstOrDefault(),
                TimeSpan.FromSeconds(5));
            Assert.NotNull(job);

            var partsDir = Path.Combine(dir, "douyin.com", ".parts", job.Id.ToString("N"));
            await WaitForAsync(() => Directory.Exists(partsDir) ? partsDir : null, TimeSpan.FromSeconds(8));

            await engine.CancelAsync(job.Id);

            await WaitForAsync(
                () => Directory.Exists(partsDir) || File.Exists(job.TargetPath + ".part") ? null : "gone",
                TimeSpan.FromSeconds(8));
            Assert.False(Directory.Exists(partsDir));
            Assert.False(File.Exists(job.TargetPath + ".part"));
            var partsRoot = Path.Combine(dir, "douyin.com", ".parts");
            Assert.True(!Directory.Exists(partsRoot) || !Directory.EnumerateFileSystemEntries(partsRoot).Any());
        }
        finally
        {
            listener.Stop();
            try { await serve.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static async Task ServeSlow(HttpListener listener, int payloadBytes)
    {
        var chunk = new byte[16 * 1024];
        try
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "video/mp4";
                        ctx.Response.ContentLength64 = payloadBytes;
                        ctx.Response.SendChunked = false;
                        var remaining = payloadBytes;
                        while (remaining > 0)
                        {
                            var n = Math.Min(chunk.Length, remaining);
                            await ctx.Response.OutputStream.WriteAsync(chunk.AsMemory(0, n));
                            remaining -= n;
                            await Task.Delay(25);
                        }
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        try { ctx.Response.Close(); } catch { }
                    }
                });
            }
        }
        catch (HttpListenerException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> probe, TimeSpan timeout) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var value = probe();
            if (value is not null)
                return value;
            await Task.Delay(40);
        }

        throw new TimeoutException("Timed out waiting for condition.");
    }

    private sealed class MemoryDownloadRepository : IDownloadRepository
    {
        private readonly ConcurrentDictionary<Guid, DownloadJob> _jobs = new();

        public Task SaveAsync(DownloadJob job, CancellationToken ct = default)
        {
            _jobs[job.Id] = job;
            return Task.CompletedTask;
        }

        public Task<DownloadJob?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(_jobs.TryGetValue(id, out var job) ? job : null);

        public Task<IReadOnlyList<DownloadJob>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult((IReadOnlyList<DownloadJob>)_jobs.Values.ToArray());

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
        {
            _jobs.TryRemove(id, out _);
            return Task.CompletedTask;
        }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
