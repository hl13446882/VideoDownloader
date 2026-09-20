using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Core.Subtitles;
using VideoDownloader.Core.Subtitles.Contracts;
using VideoDownloader.Infrastructure.Configuration;

namespace VideoDownloader.Infrastructure.Subtitles;

/// <summary>
/// When no local player is using ASR, walks completed library media and pre-builds subtitle caches
/// so the first playback already has coverage.
/// </summary>
public sealed class SubtitleIdlePrewarmService : IAsyncDisposable
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan IdleBeforeWork = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan BetweenFilesDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ScanPause = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CoverageCompleteSlack = TimeSpan.FromSeconds(1.5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDownloadRepository _repository;
    private readonly ISubtitleCacheStore _cache;
    private readonly IMediaAudioDecoder _audioDecoder;
    private readonly SubtitleOptions _options;
    private readonly SubtitleAsrActivity _activity;
    private readonly ILogger<SubtitleIdlePrewarmService> _logger;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Task? _loopTask;
    private int _started;

    public SubtitleIdlePrewarmService(
        IServiceScopeFactory scopeFactory,
        IDownloadRepository repository,
        ISubtitleCacheStore cache,
        IMediaAudioDecoder audioDecoder,
        SubtitleOptions options,
        SubtitleAsrActivity activity,
        ILogger<SubtitleIdlePrewarmService> logger)
    {
        _scopeFactory = scopeFactory;
        _repository = repository;
        _cache = cache;
        _audioDecoder = audioDecoder;
        _options = options;
        _activity = activity;
        _logger = logger;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;
        _loopTask = Task.Run(() => RunAsync(_lifetimeCts.Token));
        _logger.LogInformation("Subtitle idle prewarm started");
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(StartupDelay, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_options.Enabled || !IsWhisperModelInstalled())
                {
                    await Task.Delay(ScanPause, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await WaitUntilIdleAsync(cancellationToken).ConfigureAwait(false);
                if (_activity.IsPlaybackActive)
                    continue;

                var jobs = await LoadCandidatesAsync(cancellationToken).ConfigureAwait(false);
                if (jobs.Count == 0)
                {
                    await Task.Delay(ScanPause, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                foreach (var job in jobs)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;
                    if (_activity.IsPlaybackActive)
                        break;

                    await WaitUntilIdleAsync(cancellationToken).ConfigureAwait(false);
                    if (_activity.IsPlaybackActive)
                        break;

                    try
                    {
                        await PrewarmOneAsync(job, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        // Playback interrupted this file; move on after idle returns.
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Subtitle idle prewarm skipped job={JobId}", job.Id);
                    }

                    await Task.Delay(BetweenFilesDelay, cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(ScanPause, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Subtitle idle prewarm loop failed");
        }
    }

    private async Task WaitUntilIdleAsync(CancellationToken cancellationToken)
    {
        while (_activity.IsPlaybackActive)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged(object? _, EventArgs __) => tcs.TrySetResult();
            _activity.Changed += OnChanged;
            try
            {
                if (!_activity.IsPlaybackActive)
                    break;
                await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken))
                    .ConfigureAwait(false);
            }
            finally
            {
                _activity.Changed -= OnChanged;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        await Task.Delay(IdleBeforeWork, cancellationToken).ConfigureAwait(false);
        if (_activity.IsPlaybackActive)
            await WaitUntilIdleAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<DownloadJob>> LoadCandidatesAsync(CancellationToken cancellationToken)
    {
        var all = await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return all
            .Where(j => j.Status == DownloadStatus.Completed &&
                        !string.IsNullOrWhiteSpace(j.TargetPath) &&
                        File.Exists(j.TargetPath))
            .OrderByDescending(j => j.CompletedAt ?? j.UpdatedAt)
            .ToArray();
    }

    private async Task PrewarmOneAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        var source = LocalPlaybackMediaSourceResolver.FromCompletedJob(job);
        if (source is null)
            return;

        var snapshot = await _cache.LoadAsync(source.CacheIdentity, cancellationToken).ConfigureAwait(false);
        var covered = GetSequentialCoveredUntil(snapshot.Coverage);
        var duration = await MediaDurationProbe.ProbeAsync(source.FilePath, cancellationToken)
            .ConfigureAwait(false);
        if (duration is { } total && covered + CoverageCompleteSlack >= total)
        {
            _logger.LogDebug(
                "Subtitle idle prewarm already complete job={JobId} covered={Covered:g}/{Duration:g}",
                job.Id,
                covered,
                total);
            return;
        }

        if (_activity.IsPlaybackActive)
            return;

        using var scope = _scopeFactory.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<ISubtitlePipeline>();
        await pipeline.StartSessionAsync(
            "idle:" + job.Id.ToString("N"),
            source.CacheIdentity,
            cancellationToken).ConfigureAwait(false);

        // Session load may have restored cache; re-check after start.
        covered = pipeline.GetSequentialCoveredUntil();
        if (duration is { } total2 && covered + CoverageCompleteSlack >= total2)
        {
            await pipeline.StopSessionAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation(
            "Subtitle idle prewarm begin job={JobId} covered={Covered:g} duration={Duration}",
            job.Id,
            covered,
            duration?.ToString("g") ?? "(unknown)");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void OnPlayback(object? _, EventArgs __)
        {
            if (_activity.IsPlaybackActive)
                linked.Cancel();
        }

        _activity.Changed += OnPlayback;
        try
        {
            if (_activity.IsPlaybackActive)
                return;

            var windowSize = TimeSpan.FromSeconds(Math.Clamp(_options.PreloadAheadSeconds, 10, 90));
            await SubtitleSequentialAsr.RunAsync(
                source.FilePath,
                pipeline,
                _audioDecoder,
                SubtitleMode.Original, // recognition only; translations wait for playback mode
                windowSize,
                duration,
                liveDuration: null,
                shouldContinue: () => !_activity.IsPlaybackActive,
                _logger,
                "idle:" + job.Id.ToString("N"),
                linked.Token).ConfigureAwait(false);

            _logger.LogInformation(
                "Subtitle idle prewarm end job={JobId} covered={Covered:g}",
                job.Id,
                pipeline.GetSequentialCoveredUntil());
        }
        finally
        {
            _activity.Changed -= OnPlayback;
            try
            {
                await pipeline.StopSessionAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Session stop is best-effort on idle interrupt.
            }
        }
    }

    private static TimeSpan GetSequentialCoveredUntil(IReadOnlyList<SubtitleCoverageRange> coverage)
    {
        var progress = TimeSpan.Zero;
        var tolerance = TimeSpan.FromMilliseconds(750);
        foreach (var range in coverage.OrderBy(x => x.Start))
        {
            if (range.Start > progress + tolerance)
                break;
            if (range.End > progress)
                progress = range.End;
        }

        return progress;
    }

    private bool IsWhisperModelInstalled()
    {
        try
        {
            var path = PathExpander.Expand(_options.WhisperModelPath);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;
            return new FileInfo(path).Length > 100L * 1024 * 1024;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetimeCts.Cancel();
        if (_loopTask is not null)
        {
            try { await _loopTask.ConfigureAwait(false); }
            catch { /* ignore */ }
        }

        _lifetimeCts.Dispose();
    }
}
