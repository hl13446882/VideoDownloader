using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Detection;
using VideoDownloader.Infrastructure.Sites.ExternalResolvers;

namespace VideoDownloader.Infrastructure.Tests;

public class DetectionCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearedSession_DiscardsLateResultsAndErrors(bool fail)
    {
        var resolver = new DeferredResolver();
        var pipeline = CreatePipeline(resolver);
        var page = new Uri("https://example.test/watch?id=1");
        var detected = new List<DetectedVideo>();
        pipeline.VideoDetected += (_, video) => detected.Add(video);
        var pending = pipeline.ProbePageAsync(page, "old", null,
            RequestContext.CreateEmpty(), CancellationToken.None, runExternal: true);
        pipeline.Clear();
        if (fail)
            resolver.Completion.SetException(new InvalidOperationException("old failure"));
        else
            resolver.Completion.SetResult([new DetectedVideo(Guid.NewGuid(), SiteIds.Generic, null,
                "old", page, default,
                [MediaVariant.FromCombinedTrack("video", new Uri("https://example.test/movie.mp4"),
                    RequestContext.CreateEmpty(), contentLength: 2_000_000)], false)]);
        await pending;
        // Revisit the same page to expose stale dictionary entries, not just stale events.
        await pipeline.ProbePageAsync(page, "new", null, RequestContext.CreateEmpty(), CancellationToken.None);
        Assert.Empty(detected);
        Assert.Null(pipeline.LastExternalError);
    }

    [Fact]
    public async Task CallerCancellation_IsForwardedAndNotReportedAsFailure()
    {
        var resolver = new DeferredResolver();
        var pipeline = CreatePipeline(resolver);
        using var cancellation = new CancellationTokenSource();
        var pending = pipeline.ProbePageAsync(new Uri("https://example.test/watch?v=abc123"), null, null,
            RequestContext.CreateEmpty(), cancellation.Token, runExternal: true);
        cancellation.Cancel();
        Assert.True(resolver.Token.IsCancellationRequested);
        resolver.Completion.SetResult([]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Null(pipeline.LastExternalError);
    }

    [Fact]
    public async Task Resolver_AlreadyCanceled_DoesNotCheckOrLaunchTool()
    {
        var resolver = new YtDlpResolver(Options.Create(new AppOptions()), NullLogger<YtDlpResolver>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(
            new Uri("https://example.test/watch"), RequestContext.CreateEmpty(), new CancellationToken(true)));
        Assert.Null(resolver.LastError);
    }

    private static UnifiedMediaPipeline CreatePipeline(IExternalSiteResolver resolver) =>
        new(Substitute.For<IRequestMessageFactory>(), [resolver], Options.Create(new AppOptions()));

    private sealed class DeferredResolver : IExternalSiteResolver
    {
        public TaskCompletionSource<IReadOnlyList<DetectedVideo>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public bool IsAvailable => true;
        public bool SupportsSite(string siteId) => true;
        public string? LastError => "old resolver error";
        public bool LastFailureIsHumanVerification => false;
        public Task<IReadOnlyList<DetectedVideo>> ResolveAsync(Uri pageUrl, RequestContext context, CancellationToken ct)
        {
            Token = ct;
            return Completion.Task;
        }
    }
}
