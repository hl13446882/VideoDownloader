using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Models;
using VideoDownloader.Core.Subtitles;
using VideoDownloader.Core.Subtitles.Contracts;
using VideoDownloader.Infrastructure.Configuration;

namespace VideoDownloader.Infrastructure.Subtitles;

/// <summary>
/// When the local play page is not open, walks completed library media that are not yet fully
/// recognized and pre-builds subtitle caches. Opening /play (playing or paused) blocks idle work.
/// </summary>
public sealed class SubtitleIdlePrewarmService : IAsyncDisposable
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan IdleBeforeWork = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BetweenFilesDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ScanPause = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CoverageCompleteSlack = TimeSpan.FromSeconds(1.5);
    /// <summary>Do not monopolize Whisper on one long video; rotate after this wall time.</summary>
    private static readonly TimeSpan PerFileSlice = TimeSpan.FromMinutes(3);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDownloadRepository _repository;
    private readonly IDownloadEngine _engine;
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
        IDownloadEngine engine,
        ISubtitleCacheStore cache,
        IMediaAudioDecoder audioDecoder,
        SubtitleOptions options,
        SubtitleAsrActivity activity,
        ILogger<SubtitleIdlePrewarmService> logger)
    {
        _scopeFactory = scopeFactory;
        _repository = repository;
        _engine = engine;
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
                    _logger.LogDebug(
                        "Subtitle idle prewarm waiting enabled={Enabled} modelReady={ModelReady}",
                        _options.Enabled,
                        IsWhisperModelInstalled());
                    await Task.Delay(ScanPause, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await WaitUntilIdleAsync(cancellationToken).ConfigureAwait(false);
                if (_activity.IsPlaybackActive)
                    continue;

                var jobs = await LoadCandidatesAsync(cancellationToken).ConfigureAwait(false);
                if (jobs.Count == 0)
                {
                    _logger.LogDebug("Subtitle idle prewarm: no unfinished library media");
                    await Task.Delay(ScanPause, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _logger.LogInformation("Subtitle idle prewarm scan candidates={Count}", jobs.Count);

                var multiFile = jobs.Count > 1;
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
                        await PrewarmOneAsync(job, multiFile, cancellationToken).ConfigureAwait(false);
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
        // Prefer shorter videos so the gallery shows progress; long titles continue later via slices.
        return all
            .Where(j => j.Status == DownloadStatus.Completed &&
                        !j.SubtitleRecognized &&
                        !string.IsNullOrWhiteSpace(j.TargetPath) &&
                        File.Exists(j.TargetPath))
            .OrderBy(j => j.DurationSec is > 0 ? j.DurationSec.Value : double.MaxValue)
            .ThenBy(j => TryFileLength(j.TargetPath))
            .ThenByDescending(j => j.CompletedAt ?? j.UpdatedAt)
            .ToArray();
    }

    private static long TryFileLength(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? long.MaxValue : new FileInfo(path).Length;
        }
        catch
        {
            return long.MaxValue;
        }
    }

    private async Task PrewarmOneAsync(DownloadJob job, bool multiFile, CancellationToken cancellationToken)
    {
        var source = LocalPlaybackMediaSourceResolver.FromCompletedJob(job);
        if (source is null)
            return;

        var snapshot = await _cache.LoadAsync(source.CacheIdentity, cancellationToken).ConfigureAwait(false);
        var covered = GetSequentialCoveredUntil(snapshot.Coverage);
        var duration = job.DurationSec is > 0
            ? TimeSpan.FromSeconds(job.DurationSec.Value)
            : await MediaDurationProbe.ProbeAsync(source.FilePath, cancellationToken).ConfigureAwait(false);

        if (duration is { } total && covered + CoverageCompleteSlack >= total)
        {
            await _engine.SetSubtitleRecognizedAsync(
                job.Id,
                recognized: true,
                durationSec: total.TotalSeconds,
                cancellationToken).ConfigureAwait(false);
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
            await _engine.SetSubtitleRecognizedAsync(
                job.Id,
                recognized: true,
                durationSec: total2.TotalSeconds,
                cancellationToken).ConfigureAwait(false);
            await pipeline.StopSessionAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation(
            "Subtitle idle prewarm begin job={JobId} covered={Covered:g} duration={Duration}",
            job.Id,
            covered,
            duration?.ToString("g") ?? "(unknown)");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Only rotate when other unfinished files are waiting; sole long videos run continuously.
        var sliceDeadline = multiFile
            ? DateTime.UtcNow + PerFileSlice
            : DateTime.MaxValue;
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
            var finished = await SubtitleSequentialAsr.RunAsync(
                source.FilePath,
                pipeline,
                _audioDecoder,
                SubtitleMode.Original, // recognition only; translations wait for playback mode
                windowSize,
                duration,
                liveDuration: null,
                shouldContinue: () =>
                    !_activity.IsPlaybackActive && DateTime.UtcNow < sliceDeadline,
                _logger,
                "idle:" + job.Id.ToString("N"),
                linked.Token).ConfigureAwait(false);

            if (finished)
            {
                await _engine.SetSubtitleRecognizedAsync(
                    job.Id,
                    recognized: true,
                    durationSec: duration?.TotalSeconds,
                    cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Subtitle idle prewarm end job={JobId} covered={Covered:g} finished={Finished} sliced={Sliced}",
                job.Id,
                pipeline.GetSequentialCoveredUntil(),
                finished,
                multiFile && !finished && DateTime.UtcNow >= sliceDeadline);
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
