using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Download;
using VideoDownloader.Infrastructure.Persistence;

namespace VideoDownloader.Infrastructure.Tests;

public sealed class AutoDownloadQueueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vd-auto-queue-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteAutoSessionRepository _repo;
    private readonly IDownloadEngine _engine = Substitute.For<IDownloadEngine>();
    private readonly ConcurrentDictionary<Guid, DownloadJob> _jobs = new();
    private readonly ConcurrentQueue<Guid> _resumed = new();
    private readonly AutoDownloadScheduler _scheduler;
    private int _freeSlots = 1;

    public AutoDownloadQueueTests()
    {
        Directory.CreateDirectory(_root);
        _repo = new SqliteAutoSessionRepository(Options.Create(new AppOptions
        {
            Database = { Path = Path.Combine(_root, "downloads.db") }
        }));
        _engine.GetActiveJobs().Returns(_ => _jobs.Values.ToArray());
        _engine.FreeDownloadSlots.Returns(_ => Volatile.Read(ref _freeSlots));
        _engine.ResumeAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var id = call.ArgAt<Guid>(0);
            _resumed.Enqueue(id);
            SetStatus(_jobs[id], DownloadStatus.Downloading);
            Interlocked.Exchange(ref _freeSlots, 0);
            return Task.CompletedTask;
        });
        _scheduler = new AutoDownloadScheduler(_repo, _engine, NullLogger<AutoDownloadScheduler>.Instance);
    }

    [Fact]
    public async Task Startup_DropsCompletedVideosFromTasks_AndKeepsUnfinishedOnes()
    {
        await _repo.InitializeAsync();
        var unfinished = await AddSession(-1, withItem: false);
        var doneJob = new DownloadJob { Id = Guid.NewGuid(), DisplayName = "done", TargetPath = "done.mp4" };
        SetStatus(doneJob, DownloadStatus.Completed);
        _jobs[doneJob.Id] = doneJob;
        var stale = await _repo.TryAddQueuedItemAsync(new AutoQueueItem
        {
            Id = Guid.NewGuid(), SessionId = unfinished.Session.Id, PageUrl = "https://example.test/done",
            SourceUrl = "https://example.test/done", VariantMetaJson = "{}", Fingerprint = "done",
            ProbedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow
        });
        stale!.Status = AutoQueueItemStatus.Downloading;
        stale.DownloadJobId = doneJob.Id;
        await _repo.SaveItemAsync(stale);
        await _repo.TryAddQueuedItemAsync(new AutoQueueItem
        {
            Id = Guid.NewGuid(), SessionId = unfinished.Session.Id, PageUrl = "https://example.test/next",
            SourceUrl = "https://example.test/next", VariantMetaJson = "{}", Fingerprint = "next",
            ProbedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow
        });
        var finished = await AddSession(-2, status: DownloadStatus.Completed);

        await _scheduler.InitializeAsync();

        Assert.Null(await _repo.GetSessionAsync(finished.Session.Id));
        Assert.NotNull(await _repo.GetSessionAsync(unfinished.Session.Id));
        var kept = await _repo.GetItemsAsync(unfinished.Session.Id);
        Assert.DoesNotContain(kept, i => i.DownloadJobId == doneJob.Id);
        Assert.Contains(kept, i => i.Fingerprint == "next");
        Assert.Contains(doneJob.Id, _jobs.Keys);
        Assert.Contains(finished.Job!.Id, _jobs.Keys);
    }

    [Fact]
    public async Task Activate_PausesListedDownloadsAndOtherRunningSessions()
    {
        await _repo.InitializeAsync();
        var running = await AddSession(-1, status: DownloadStatus.Downloading);
        running.Session.Status = AutoSessionStatus.Running;
        await _repo.SaveSessionAsync(running.Session);
        var manual = new DownloadJob { Id = Guid.NewGuid(), DisplayName = "manual", TargetPath = "manual.mp4" };
        SetStatus(manual, DownloadStatus.Downloading);
        _jobs[manual.Id] = manual;
        var next = await AddSession(-2, status: DownloadStatus.Paused);
        var pausedIds = new System.Collections.Concurrent.ConcurrentQueue<Guid>();
        _engine.PauseAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var id = call.ArgAt<Guid>(0);
            pausedIds.Enqueue(id);
            if (_jobs.TryGetValue(id, out var job))
                SetStatus(job, DownloadStatus.Paused);
            return Task.CompletedTask;
        });

        await _scheduler.ActivateDownloadSessionAsync(next.Session.Id);

        Assert.Contains(running.Job!.Id, pausedIds);
        Assert.Contains(manual.Id, pausedIds);
        Assert.Equal(AutoSessionStatus.Paused, (await _repo.GetSessionAsync(running.Session.Id))!.Status);
        Assert.Equal(AutoSessionStatus.Running, (await _repo.GetSessionAsync(next.Session.Id))!.Status);
        Assert.Equal(next.Session.Id, _scheduler.ActiveSessionId);
    }

    [Fact]
    public async Task Startup_RemovesEmptySessionsAndRunsNewestFirst_ThenChainsAfterCompletion()
    {
        await _repo.InitializeAsync();
        var older = await AddSession(-2);
        var newer = await AddSession(-1);
        var empty = await AddSession(0, withItem: false);
        await _scheduler.InitializeAsync();
        await Until(() => Task.FromResult(_resumed.Count == 1));
        Assert.Null(await _repo.GetSessionAsync(empty.Session.Id));
        Assert.Equal(newer.Job!.Id, _resumed.Single());
        Assert.Equal(newer.Session.Id, _scheduler.ActiveSessionId);
        await Task.Delay(900);
        Assert.Single(_resumed);
        SetStatus(newer.Job, DownloadStatus.Completed);
        Interlocked.Exchange(ref _freeSlots, 1);
        await Until(() => Task.FromResult(_resumed.Count == 2));
        Assert.Equal(new[] { newer.Job.Id, older.Job!.Id }, _resumed.ToArray());
        Assert.Null(await _repo.GetSessionAsync(newer.Session.Id));
        SetStatus(older.Job, DownloadStatus.Completed);
        Interlocked.Exchange(ref _freeSlots, 1);
        await Until(async () => await _repo.GetSessionAsync(older.Session.Id) is null);
        Assert.Equal(2, _jobs.Count);
    }

    [Fact]
    public async Task ZeroSlots_DoesNotSkipFailedFirstTask_AndFailureDoesNotLoopForever()
    {
        await _repo.InitializeAsync();
        var older = await AddSession(-2);
        var failed = await AddSession(-1, status: DownloadStatus.Failed);
        _engine.ResumeAsync(failed.Job!.Id, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _resumed.Enqueue(failed.Job.Id);
            return Task.CompletedTask; // One recovery attempt fails again.
        });
        Interlocked.Exchange(ref _freeSlots, 0);
        await _scheduler.InitializeAsync();
        await Until(() => Task.FromResult(_scheduler.ActiveSessionId == failed.Session.Id));
        await Task.Delay(900);
        Assert.Empty(_resumed);
        Interlocked.Exchange(ref _freeSlots, 1);
        await Until(() => Task.FromResult(_resumed.Count == 2));
        Assert.Equal(new[] { failed.Job.Id, older.Job!.Id }, _resumed.ToArray());
        await Task.Delay(900);
        Assert.Equal(2, _resumed.Count);
        Assert.Equal(AutoSessionStatus.Failed, (await _repo.GetSessionAsync(failed.Session.Id))!.Status);
    }

    [Fact]
    public async Task UserStop_PreventsStartingNextTask_ButStillReconcilesCompletion()
    {
        await _repo.InitializeAsync();
        var older = await AddSession(-2);
        var newer = await AddSession(-1);
        await _scheduler.InitializeAsync();
        await Until(() => Task.FromResult(_resumed.Count == 1));
        _scheduler.Stop();
        SetStatus(newer.Job!, DownloadStatus.Completed);
        Interlocked.Exchange(ref _freeSlots, 1);
        await Task.Delay(1200);
        Assert.Single(_resumed);
        Assert.NotEqual(AutoSessionStatus.Running, (await _repo.GetSessionAsync(older.Session.Id))!.Status);
    }

    [Fact]
    public async Task ProducingEmptySession_IsNotDeletedOrAdvanced_UntilCollectionEnds()
    {
        await _repo.InitializeAsync();
        var older = await AddSession(-2);
        var producing = await AddSession(-1, withItem: false);
        producing.Session.Status = AutoSessionStatus.Running;
        await _repo.SaveSessionAsync(producing.Session);
        _scheduler.SetActiveSession(producing.Session.Id);
        await Task.Delay(900);
        Assert.NotNull(await _repo.GetSessionAsync(producing.Session.Id));
        Assert.Empty(_resumed);
        producing.Session.Status = AutoSessionStatus.Completed;
        await _repo.SaveSessionAsync(producing.Session);
        _scheduler.FinishProducer();
        await Until(() => Task.FromResult(_resumed.Count == 1));
        Assert.Equal(older.Job!.Id, _resumed.Single());
    }

    [Fact]
    public async Task CollectionLimit_DrainsItsQueuedItemsBeforeAdvancing()
    {
        await _repo.InitializeAsync();
        var older = await AddSession(-2);
        var ended = await AddSession(-1);
        ended.Session.Status = AutoSessionStatus.Completed;
        await _repo.SaveSessionAsync(ended.Session);
        _scheduler.SetActiveSession(ended.Session.Id);
        _scheduler.FinishProducer();
        await Until(() => Task.FromResult(_resumed.Count == 1));
        Assert.Equal(ended.Job!.Id, _resumed.Single());
        Assert.Equal(AutoSessionStatus.Running, (await _repo.GetSessionAsync(ended.Session.Id))!.Status);
        Assert.DoesNotContain(older.Job!.Id, _resumed);
    }

    [Fact]
    public async Task QueuedVideos_DrainInSequenceBeforeStartingTheNextSession()
    {
        await _repo.InitializeAsync();
        var older = await AddSession(-2);
        var queued = await AddSession(-1, withItem: false);
        var variant = MediaVariant.FromCombinedTrack("test", new Uri("https://example.test/test.mp4"), RequestContext.CreateEmpty());
        var saved = DownloadJobMapper.SerializeVariant(variant);
        foreach (var fingerprint in new[] { "one", "two" })
            await _repo.TryAddQueuedItemAsync(new AutoQueueItem { Id = Guid.NewGuid(), SessionId = queued.Session.Id,
                PageUrl = "https://example.test/video", SourceUrl = variant.SourceUrl.AbsoluteUri, VariantMetaJson = saved.MetaJson,
                VariantSecret = saved.Secret, Fingerprint = fingerprint, Caption = fingerprint,
                ProbedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow });
        var enqueued = new ConcurrentQueue<DownloadJob>();
        _engine.EnqueueAsync(Arg.Any<MediaVariant>(), Arg.Any<string>(), Arg.Any<Uri?>(), Arg.Any<string?>(),
            Arg.Any<double?>(), Arg.Any<IReadOnlyList<MediaVariant>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var job = new DownloadJob { Id = Guid.NewGuid(), DisplayName = call.ArgAt<string>(1), TargetPath = "test.mp4" };
            SetStatus(job, DownloadStatus.Downloading);
            _jobs[job.Id] = job;
            enqueued.Enqueue(job);
            Interlocked.Exchange(ref _freeSlots, 0);
            return Task.FromResult(job.Id);
        });
        await _scheduler.InitializeAsync();
        await Until(() => Task.FromResult(enqueued.Count == 1));
        Assert.Empty(_resumed);
        SetStatus(enqueued.Single(), DownloadStatus.Completed);
        Interlocked.Exchange(ref _freeSlots, 1);
        await Until(() => Task.FromResult(enqueued.Count == 2));
        Assert.Empty(_resumed);
        Assert.Equal(new[] { "one", "two" }, enqueued.Select(j => j.DisplayName));
        SetStatus(enqueued.Last(), DownloadStatus.Completed);
        Interlocked.Exchange(ref _freeSlots, 1);
        await Until(() => Task.FromResult(_resumed.Count == 1));
        Assert.Equal(older.Job!.Id, _resumed.Single());
    }

    private async Task<(AutoSession Session, DownloadJob? Job)> AddSession(int order, bool withItem = true,
        DownloadStatus status = DownloadStatus.Paused)
    {
        var session = new AutoSession { Id = Guid.NewGuid(), Name = "session " + order,
            Status = AutoSessionStatus.Paused, StartedAt = DateTimeOffset.UtcNow.AddMinutes(order), UpdatedAt = DateTimeOffset.UtcNow };
        await _repo.SaveSessionAsync(session);
        if (!withItem) return (session, null);
        var job = new DownloadJob { Id = Guid.NewGuid(), DisplayName = "video", TargetPath = "test.mp4" };
        SetStatus(job, status);
        _jobs[job.Id] = job;
        var item = await _repo.TryAddQueuedItemAsync(new AutoQueueItem { Id = Guid.NewGuid(), SessionId = session.Id,
            PageUrl = "https://example.test/video", SourceUrl = "https://example.test/video", VariantMetaJson = "{}",
            Fingerprint = job.Id.ToString(), ProbedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow });
        item!.Status = AutoQueueItemStatus.Downloading;
        item.DownloadJobId = job.Id;
        await _repo.SaveItemAsync(item);
        return (session, job);
    }

    private static void SetStatus(DownloadJob job, DownloadStatus status) =>
        typeof(DownloadJob).GetProperty(nameof(DownloadJob.Status))!.SetValue(job, status);

    private static async Task Until(Func<Task<bool>> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(8);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < end, "Automatic queue did not reach the expected state");
            await Task.Delay(50);
        }
    }

    public void Dispose() => _scheduler.Dispose();
}
