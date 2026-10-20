using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Download;
using VideoDownloader.Core.Errors;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Download;
using VideoDownloader.Infrastructure.Http;
using VideoDownloader.Infrastructure.Licensing;
using VideoDownloader.Infrastructure.LocalLibrary;
using VideoDownloader.Infrastructure.Persistence;

namespace VideoDownloader.Infrastructure.Tests;

public class DownloadIdleRecoveryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IdleSlotReopensRequestRenewsOn403AndAlwaysReleasesCapacity(bool renew)
    {
        var root = Path.Combine(Path.GetTempPath(), "vd-idle-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var payload = new byte[600 * 1024];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), 12);
        "moov"u8.CopyTo(payload.AsSpan(4));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(12, 4), payload.Length - 12);
        "mdat"u8.CopyTo(payload.AsSpan(16));
        var oldRequests = 0;
        var resumed = false;
        using var http = new HttpClient(new Handler(request =>
        {
            var from = (int)(request.Headers.Range?.Ranges.Single().From ?? 0);
            var old = request.RequestUri!.AbsolutePath.Contains("old");
            if (old && Interlocked.Increment(ref oldRequests) > 1 && renew)
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            if (old)
            {
                var response = new HttpResponseMessage(from == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
                    { Content = new StreamContent(new StalledStream(payload.AsMemory(from, 1024).ToArray())) };
                response.Content.Headers.ContentLength = payload.Length - from;
                if (from > 0) response.Content.Headers.ContentRange = new(from, payload.Length - 1, payload.Length);
                return response;
            }
            resumed = from > 0;
            var fresh = new HttpResponseMessage(from == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
                { Content = new ByteArrayContent(payload[from..]) };
            if (from > 0) fresh.Content.Headers.ContentRange = new(from, payload.Length - 1, payload.Length);
            return fresh;
        })) { Timeout = Timeout.InfiniteTimeSpan };
        var options = Options.Create(new AppOptions { Download = new DownloadOptions
        {
            DefaultSavePath = root, MaxConcurrentDownloads = 1, AutoRecoverDownloads = false,
            FailedRetryIntervalSeconds = 0, RetryCount = 0
        }});
        var requestFactory = new RequestMessageFactory(options);
        var ffmpeg = Substitute.For<IFfmpegAdapter>();
        var repo = Substitute.For<IDownloadRepository>();
        var contexts = Substitute.For<IRequestContextProvider>();
        contexts.CaptureCurrentContext(Arg.Any<Uri?>(), Arg.Any<Uri>()).Returns(RequestContext.CreateEmpty());
        contexts.RefreshContextAsync(Arg.Any<Uri?>(), Arg.Any<Uri>(), Arg.Any<RequestContext?>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(c => c.ArgAt<RequestContext?>(2) ?? RequestContext.CreateEmpty());
        var page = new Uri("https://www.youtube.com/watch?v=idle-test");
        var freshVariant = MediaVariant.FromCombinedTrack("720p", new Uri("https://cdn.example/fresh.mp4"), RequestContext.CreateEmpty(),
            height: 720, container: "mp4", contentLength: payload.Length) with { ContentIdentity = "id:idle-test", RecoveryPageUrl = page };
        var resolver = Substitute.For<IExternalSiteResolver>();
        resolver.IsAvailable.Returns(true);
        resolver.SupportsSite(Arg.Any<string>()).Returns(true);
        resolver.ResolveAsync(Arg.Any<Uri>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DetectedVideo>>([new(Guid.NewGuid(), "youtube", "idle-test", "sample", page,
                MediaFamily.DirectMp4, [freshVariant], false)]));
        var engine = new DownloadEngine(new HttpMediaDownloader(http, requestFactory, options, NullLogger<HttpMediaDownloader>.Instance),
            new M3u8DownloadAdapter(requestFactory, ffmpeg, Substitute.For<IHttpClientFactory>()), ffmpeg,
            new DownloadJobStateMachine(), repo, contexts, new DownloadBackendRouter(), options,
            NullLogger<DownloadEngine>.Instance, new LicenseService(http, options, NullLogger<LicenseService>.Instance),
            new LocalVideoThumbnailStore(ffmpeg, NullLogger<LocalVideoThumbnailStore>.Instance), new MemoryLibraryKindCatalog(), [resolver])
            { ProgressIdleTimeout = TimeSpan.FromMilliseconds(150) };
        try
        {
            var variant = freshVariant with { Tracks = [freshVariant.Tracks[0] with { SourceUrl = new Uri("https://cdn.example/old.mp4") }] };
            var id = await engine.EnqueueAsync(variant, "sample", page, durationSec: 60);
            var deadline = DateTime.UtcNow.AddSeconds(12);
            DownloadJob job;
            do
            {
                await Task.Delay(20);
                job = engine.GetActiveJobs().Single(j => j.Id == id);
            } while ((job.Status is not (DownloadStatus.Completed or DownloadStatus.Failed) || engine.FreeDownloadSlots != 1) && DateTime.UtcNow < deadline);
            Assert.Equal(1, engine.FreeDownloadSlots);
            Assert.True(oldRequests > 0, "Transfer never started: " + job.LastErrorCode);
            Assert.Equal(2, oldRequests);
            if (renew)
            {
                Assert.Equal(DownloadStatus.Completed, job.Status);
                Assert.True(resumed);
                Assert.Equal(payload, await File.ReadAllBytesAsync(job.TargetPath));
                await resolver.Received(1).ResolveAsync(Arg.Any<Uri>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>());
            }
            else
            {
                Assert.Equal(DownloadStatus.Failed, job.Status);
                Assert.Equal(ErrorCodes.NoProgress, job.LastErrorCode);
                Assert.True(File.Exists(job.TargetPath + ".part"));
            }
        }
        finally { engine.Dispose(); Directory.Delete(root, true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class StalledStream(byte[] prefix) : MemoryStream(prefix)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
