using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Download;
using VideoDownloader.Core.Errors;
using VideoDownloader.Core.Models;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.Download;
using VideoDownloader.Infrastructure.Ffmpeg;
using VideoDownloader.Infrastructure.Http;
using VideoDownloader.Infrastructure.Licensing;
using VideoDownloader.Infrastructure.LocalLibrary;
using VideoDownloader.Infrastructure.Persistence;

namespace VideoDownloader.Infrastructure.Tests;

public class RecoverOnStartupConcurrencyTests
{
    [Fact]
    public async Task SavedYouTubeLiveCannotConsumeStartupRecoveryCapacityOrBeEnqueued()
    {
        var repo = new MemoryDownloadRepository();
        var live = MakeJob("old-live", "https://manifest.googlevideo.com/api/manifest/hls_playlist/source/yt_live_broadcast/live/1/index.m3u8",
            DateTimeOffset.UtcNow, DownloadStatus.Downloading);
        await repo.SaveAsync(live);
        using var engine = CreateEngine(repo, 1, true);
        await engine.RecoverOnStartupAsync();
        Assert.Equal(DownloadStatus.Failed, live.Status);
        Assert.Equal(ErrorCodes.LiveNotSupported, live.LastErrorCode);
        Assert.Equal(1, engine.FreeDownloadSlots);
        var error = await Assert.ThrowsAsync<DownloadException>(() => engine.EnqueueAsync(live.Variant, "live"));
        Assert.Equal(ErrorCodes.LiveNotSupported, error.ErrorCode);
    }

    [Fact]
    public async Task RecoverOnStartup_DoesNotMarkStalePausedAsContextExpired()
    {
        var repo = new MemoryDownloadRepository();
        var stale = MakeJob(
            "stale-generic",
            "https://cdn.example.com/video.mp4?expire=1",
            updatedAt: DateTimeOffset.UtcNow.AddHours(-2));
        await repo.SaveAsync(stale);

        using var engine = CreateEngine(repo, maxConcurrent: 1, autoRecover: true);
        await engine.RecoverOnStartupAsync();
        await Task.Delay(400);

        var job = engine.GetActiveJobs().Single(j => j.Id == stale.Id);
        Assert.False(string.Equals(job.LastErrorCode, ErrorCodes.ContextExpired, StringComparison.OrdinalIgnoreCase));
        // May be Downloading / Failed / Paused depending on the quick HTTP probe — never ContextExpired from startup.
        Assert.NotEqual(DownloadStatus.Completed, job.Status);
    }

    [Fact]
    public async Task RecoverOnStartup_StartsPausedAndPending_UpToConcurrency()
    {
        var repo = new MemoryDownloadRepository();
        var paused = MakeJob("paused", "https://upos-sz-mirrorcosov.bilivideo.com/a.m4s?deadline=9999999999",
            DateTimeOffset.UtcNow.AddHours(-5), DownloadStatus.Paused);
        var pending = MakeJob("pending", "https://upos-sz-mirrorcosov.bilivideo.com/b.m4s?deadline=9999999999",
            DateTimeOffset.UtcNow.AddHours(-5), DownloadStatus.Pending);
        var queued = MakeJob("queued", "https://upos-sz-mirrorcosov.bilivideo.com/c.m4s?deadline=9999999999",
            DateTimeOffset.UtcNow.AddHours(-5), DownloadStatus.Paused);
        await repo.SaveAsync(paused);
        await repo.SaveAsync(pending);
        await repo.SaveAsync(queued);

        using var engine = CreateEngine(repo, maxConcurrent: 2, autoRecover: true);
        await engine.RecoverOnStartupAsync();
        await Task.Delay(300);

        var jobs = engine.GetActiveJobs().ToArray();
        Assert.Equal(3, jobs.Length);
        // Two should have been handed to RunJobAsync; one remains waiting.
        Assert.True(jobs.Count(j => j.Status == DownloadStatus.Paused || j.Status == DownloadStatus.Pending) >= 1);
    }

    [Fact]
    public async Task RecoverOnStartup_CapsAutoStart_KeepsExcessPaused_NoContextExpired()
    {
        var repo = new MemoryDownloadRepository();
        for (var i = 0; i < 4; i++)
        {
            await repo.SaveAsync(MakeJob(
                $"job-{i}",
                $"https://upos-sz-mirrorcosov.bilivideo.com/upgcxcode/{i}/x.m4s?deadline=9999999999",
                updatedAt: DateTimeOffset.UtcNow));
        }

        using var engine = CreateEngine(repo, maxConcurrent: 2, autoRecover: true);
        await engine.RecoverOnStartupAsync();
        await Task.Delay(300);

        var jobs = engine.GetActiveJobs().ToArray();
        Assert.Equal(4, jobs.Length);
        Assert.DoesNotContain(
            jobs,
            j => j.Status == DownloadStatus.Failed &&
                 string.Equals(j.LastErrorCode, ErrorCodes.ContextExpired, StringComparison.OrdinalIgnoreCase));
        Assert.True(jobs.Count(j => j.Status == DownloadStatus.Paused) >= 2);
    }

    private static DownloadJob MakeJob(
        string name,
        string url,
        DateTimeOffset updatedAt,
        DownloadStatus status = DownloadStatus.Paused) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = name,
        TargetPath = Path.Combine(Path.GetTempPath(), name + ".mp4"),
        Variant = MediaVariant.FromCombinedTrack(
            "v1",
            new Uri(url),
            RequestContext.CreateEmpty(),
            container: "mp4"),
        Status = status,
        CreatedAt = updatedAt,
        UpdatedAt = updatedAt
    };

    [Fact]
    public async Task Startup_DefersAutomaticSessionJobsToTheSequentialScheduler()
    {
        var repo = new MemoryDownloadRepository();
        var job = MakeJob("auto-owned", "https://example.test/video.mp4", DateTimeOffset.UtcNow, DownloadStatus.Paused);
        await repo.SaveAsync(job);
        var sessions = Substitute.For<IAutoSessionRepository>();
        sessions.GetDownloadJobIdsAsync(Arg.Any<CancellationToken>()).Returns(new[] { job.Id });
        using var engine = CreateEngine(repo, maxConcurrent: 3, autoRecover: true, autoSessions: sessions);
        await engine.RecoverOnStartupAsync();
        await Task.Delay(300);
        Assert.Equal(DownloadStatus.Paused, engine.GetActiveJobs().Single().Status);
    }

    [Fact]
    public async Task Resume_CanStartRecoveredPendingJobs()
    {
        var repo = new MemoryDownloadRepository();
        var job = MakeJob("pending-resume", "https://example.test/video.mp4", DateTimeOffset.UtcNow, DownloadStatus.Pending);
        await repo.SaveAsync(job);
        using var engine = CreateEngine(repo, maxConcurrent: 1, autoRecover: false);
        await engine.RecoverOnStartupAsync();
        Assert.Equal(DownloadStatus.Pending, job.Status);
        await engine.ResumeAsync(job.Id);
        await Task.Delay(300);
        Assert.NotEqual(DownloadStatus.Pending, job.Status);
    }

    private static DownloadEngine CreateEngine(IDownloadRepository repo, int maxConcurrent, bool autoRecover, IAutoSessionRepository? autoSessions = null)
    {
        var options = Options.Create(new AppOptions
        {
            Download = new DownloadOptions
            {
                MaxConcurrentDownloads = maxConcurrent,
                AutoRecoverDownloads = autoRecover,
                DefaultSavePath = Path.GetTempPath(),
                RetryCount = 0
            }
        });

        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMilliseconds(100)
        };
        var downloader = new HttpMediaDownloader(
            http,
            new RequestMessageFactory(options),
            options,
            NullLogger<HttpMediaDownloader>.Instance);
        var ffmpeg = Substitute.For<IFfmpegAdapter>();
        var m3u8 = new M3u8DownloadAdapter(
            new RequestMessageFactory(options),
            ffmpeg,
            Substitute.For<IHttpClientFactory>());
        var contexts = Substitute.For<IRequestContextProvider>();
        contexts.RefreshContextAsync(
                Arg.Any<Uri?>(),
                Arg.Any<Uri>(),
                Arg.Any<RequestContext?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>())
            .Returns(ci => ci.ArgAt<RequestContext?>(2) ?? RequestContext.CreateEmpty());

        return new DownloadEngine(
            downloader,
            m3u8,
            ffmpeg,
            new DownloadJobStateMachine(),
            repo,
            contexts,
            new DownloadBackendRouter(),
            options,
            NullLogger<DownloadEngine>.Instance,
            new LicenseService(new HttpClient(), options, NullLogger<LicenseService>.Instance),
            new LocalVideoThumbnailStore(ffmpeg, NullLogger<LocalVideoThumbnailStore>.Instance),
            new MemoryLibraryKindCatalog(), autoSessions: autoSessions);
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

        public Task UpdateThumbReadyAsync(Guid id, bool ready, CancellationToken ct = default)
        {
            if (_jobs.TryGetValue(id, out var job))
                job.ThumbReady = ready;
            return Task.CompletedTask;
        }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
