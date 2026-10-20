using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Tests;

public sealed class DetectionProbeSchedulerTests
{
    private static Task Run(Func<CancellationToken, Task> observe,
        Func<CancellationToken, Task>? extract, Func<bool> ready, CancellationToken ct = default,
        bool enrich = false, Func<bool>? stop = null, int delayMs = 0, int budgetMs = 2000) =>
        DetectionProbeScheduler.RunAsync(observe, extract, ready, stop ?? (() => false),
            TimeSpan.FromMilliseconds(delayMs), TimeSpan.FromMilliseconds(budgetMs), enrich, ct,
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(30));

    [Fact]
    public async Task ExistingUsableAddress_ReturnsWithoutStartingExternalProcess()
    {
        var calls = 0;
        await Run(_ => Task.CompletedTask, _ => { calls++; return Task.CompletedTask; }, () => true);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task BrowserCanWinWhileExternalIsRunning_AndCancellationJoinsItsCleanup()
    {
        var started = false;
        var cleaned = false;
        var ready = false;
        var calls = 0;
        await Run(_ => { ready |= started; return Task.CompletedTask; }, async ct =>
        {
            calls++;
            started = true;
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { await Task.Yield(); cleaned = true; }
        }, () => ready);
        Assert.True(ready);
        Assert.True(cleaned);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Success_StopsEvenWhenEnrichmentWasRequested()
    {
        var calls = 0;
        await Run(_ => Task.CompletedTask,
            _ => { calls++; return Task.CompletedTask; }, () => true, enrich: true);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task EmptyExternalResult_IsNotRepeatedInAnotherFullRound()
    {
        var calls = 0;
        await Run(_ => Task.CompletedTask, _ => { calls++; return Task.CompletedTask; }, () => false);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PassiveGrace_EndsAsSoonAsBrowserFindsUsableAddress()
    {
        var observations = 0;
        var calls = 0;
        await Run(_ => { observations++; return Task.CompletedTask; },
            _ => { calls++; return Task.CompletedTask; }, () => observations >= 2, delayMs: 500);
        Assert.Equal(0, calls);
        Assert.Equal(2, observations);
    }

    [Fact]
    public async Task LiveSignal_StopsBeforeExternalExtraction_EvenInEnrichmentMode()
    {
        var calls = 0;
        await Run(_ => Task.CompletedTask, _ => { calls++; return Task.CompletedTask; },
            () => false, enrich: true, stop: () => true);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task PageBudget_CancelsAndJoinsStalledProcess()
    {
        var cleaned = false;
        await Run(_ => Task.CompletedTask, async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { cleaned = true; }
        }, () => false, budgetMs: 80);
        Assert.True(cleaned);
    }

    [Fact]
    public async Task PageSwitch_CancellationIsPropagatedAfterExternalCleanup()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = false;
        var pending = Run(_ => Task.CompletedTask, async ct =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { cleaned = true; }
        }, () => false, cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(cleaned);
    }

    [Theory]
    [InlineData(SiteIds.YouTube, false, false)]
    [InlineData(SiteIds.YouTube, true, true)]
    [InlineData(SiteIds.Bilibili, false, false)]
    [InlineData(SiteIds.Bilibili, true, true)]
    [InlineData(SiteIds.Generic, false, true)]
    public void SeparateVideo_RequiresAudioOnSitesThatUseAdaptivePlayback(string site, bool audio, bool ready)
    {
        var page = new Uri("https://example.test/watch");
        var combined = MediaVariant.FromCombinedTrack("v", page, RequestContext.CreateEmpty());
        var tracks = new List<MediaTrack> { combined.Tracks[0] with { Kind = MediaTrackKind.Video } };
        if (audio) tracks.Add(combined.Tracks[0] with { Kind = MediaTrackKind.Audio });
        var video = new DetectedVideo(Guid.NewGuid(), site, null, "test", page, default,
            [combined with { Tracks = tracks }], false);
        Assert.Equal(ready, DetectionReadiness.IsUsable(video));
        Assert.False(DetectionReadiness.IsUsable(video with { IsLive = true }));
        Assert.False(DetectionReadiness.IsUsable(video with { Availability = MediaAvailabilityKind.VideoDenied }));
    }
}
