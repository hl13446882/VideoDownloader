using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Tests;

public class DetectionResultBufferTests
{
    [Fact]
    public async Task StagedResultsPublishOnceOnlyAfterTheSharedProbeExit()
    {
        var session = Guid.NewGuid();
        var buffer = new DetectionResultBuffer(session);
        IReadOnlyList<DetectedVideo>? displayed = null;
        var extracted = Video(session, 1080);
        await DetectionProbeScheduler.RunAsync(_ => Task.CompletedTask,
            _ => { Assert.Null(displayed); buffer.Stage(extracted); return Task.CompletedTask; },
            () => buffer.HasUsableResult, () => buffer.HasLiveResult,
            TimeSpan.Zero, TimeSpan.FromSeconds(2), false, default,
            TimeSpan.FromMilliseconds(1));
        Assert.Null(displayed);
        displayed = buffer.Complete();
        Assert.Equal(extracted, Assert.Single(displayed));
        Assert.Empty(buffer.Complete());
        Assert.False(buffer.Stage(Video(session, 2160)));
    }

    [Fact]
    public void SwitchOrManualReplacementCannotPublishPreviousSessionsResults()
    {
        var oldId = Guid.NewGuid();
        var oldRun = new DetectionResultBuffer(oldId);
        oldRun.Stage(Video(oldId, 1080));
        oldRun.Cancel();
        var newId = Guid.NewGuid();
        var newRun = new DetectionResultBuffer(newId);
        Assert.Empty(oldRun.Complete());
        Assert.False(oldRun.Stage(Video(oldId, 2160)));
        Assert.False(newRun.Stage(Video(oldId, 2160)));
        newRun.Stage(Video(newId, 720));
        Assert.Equal(720, Assert.Single(newRun.Complete()).Variants.Single().Height);
    }

    [Fact]
    public void PartialAndLateLowerQualityCannotReplaceTheCompleteLadder()
    {
        var id = Guid.NewGuid();
        var run = new DetectionResultBuffer(id);
        Assert.False(run.Stage(Video(id, 360) with { IsFormatDiscoveryPending = true }));
        Assert.False(run.HasUsableResult);
        Assert.True(run.Stage(Video(id, 1080)));
        Assert.False(run.Stage(Video(id, 360)));
        Assert.Equal(1080, Assert.Single(run.Complete()).Variants.Single().Height);
    }

    private static DetectedVideo Video(Guid session, int height) => new(
        new Guid("00000000-0000-0000-0000-000000000001"), SiteIds.YouTube, "video-id", "test",
        new Uri("https://www.youtube.com/watch?v=video-id"), MediaFamily.DirectMp4,
        [MediaVariant.FromCombinedTrack("video", new Uri("https://cdn.example/video.mp4"), RequestContext.CreateEmpty(), height: height)],
        false) { SessionId = session };
}
