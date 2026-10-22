using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Tests;

public sealed class DetectionProbeSchedulerTests
{
    private static Task Run(Func<CancellationToken, Task> observe,
        Func<CancellationToken, Task>? extract, Func<bool> ready, CancellationToken ct = default,
        Func<bool>? stop = null, int delayMs = 0, int budgetMs = 2000) =>
        DetectionProbeScheduler.RunAsync(observe, extract, ready, stop ?? (() => false),
            TimeSpan.FromMilliseconds(delayMs), TimeSpan.FromMilliseconds(budgetMs), ct,
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(30));

    [Fact]
    public async Task UsablePassiveResult_StillRunsExternalOnceForCompleteFormats()
    {
        var calls = 0;
        await Run(_ => Task.CompletedTask, _ => { calls++; return Task.CompletedTask; }, () => true);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task BrowserResult_DoesNotCancelRunningExternal_AndCancellationJoinsCleanup()
    {
        var started = false;
        var cleaned = false;
        var calls = 0;
        await Run(_ => { started = true; return Task.CompletedTask; }, async ct =>
        {
            calls++;
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { await Task.Yield(); cleaned = true; }
        }, () => started, budgetMs: 80);
        Assert.True(started);
        Assert.True(cleaned);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task UsablePassiveResult_DoesNotStartOneExternalRoundPerPoll()
    {
        var calls = 0;
        var observations = 0;
        await Run(_ => { observations++; return Task.CompletedTask; },
            _ => { calls++; return Task.CompletedTask; }, () => true);
        Assert.Equal(1, calls);
        Assert.Equal(1, observations);
    }

    [Fact]
    public async Task EmptyExternalResult_IsNotRepeatedInAnotherFullRound()
    {
        var calls = 0;
        await Run(_ => Task.CompletedTask, _ => { calls++; return Task.CompletedTask; }, () => false);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task BrowserUsableBeforeDelay_StartsExternalImmediately()
    {
        var observations = 0;
        var calls = 0;
        await Run(_ => { observations++; return Task.CompletedTask; },
            _ => { calls++; return Task.CompletedTask; }, () => observations >= 2, delayMs: 500);
        Assert.Equal(1, calls);
        Assert.Equal(2, observations);
    }

    [Fact]
    public async Task LiveSignal_StopsBeforeExternalExtraction()
    {
        var calls = 0;
        await Run(_ => Task.CompletedTask, _ => { calls++; return Task.CompletedTask; },
            () => false, stop: () => true);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ReadyExtraction_DoesNotWaitForPollOrTrailingWindow()
    {
        var extraction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observations = 0;
        var run = DetectionProbeScheduler.RunAsync(
            _ => { observations++; return Task.CompletedTask; },
            _ => { started.SetResult(); return extraction.Task; },
            () => false, () => false, TimeSpan.Zero, TimeSpan.FromSeconds(10), default,
            pollInterval: TimeSpan.FromSeconds(5), trailingObservation: TimeSpan.Zero);
        await started.Task;
        extraction.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, observations);
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
