using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Errors;
using VideoDownloader.Core.Models;
using VideoDownloader.Core.Naming;
using VideoDownloader.Infrastructure.Persistence;

namespace VideoDownloader.Infrastructure.Download;

/// <summary>
/// Consumes Queued auto-session items into the download engine, limited by FreeDownloadSlots.
/// Independent of feed advance timing.
/// </summary>
public sealed class AutoDownloadScheduler : IDisposable
{
    private readonly IAutoSessionRepository _sessions;
    private readonly IDownloadEngine _engine;
    private readonly ILogger<AutoDownloadScheduler> _logger;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Guid? _activeSessionId;
    private Guid? _producerSessionId;
    private bool _sequentialEnabled;
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, byte> _visitedSessions = new();
    private readonly ConcurrentDictionary<Guid, byte> _resumeAttempts = new();
    private readonly ConcurrentDictionary<Guid, byte> _retryItems = new();

    public AutoDownloadScheduler(
        IAutoSessionRepository sessions,
        IDownloadEngine engine,
        ILogger<AutoDownloadScheduler> logger)
    {
        _sessions = sessions;
        _engine = engine;
        _logger = logger;
    }

    public event EventHandler? StateChanged;

    public Guid? ActiveSessionId
    {
        get { lock (_gate) return _activeSessionId; }
    }

    /// <summary>Keep status reconciliation alive even without an active producer session.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is { IsCompleted: false }) return;
            _cts?.Dispose();
            var cts = new CancellationTokenSource();
            _cts = cts;
            _loop = Task.Run(() => RunAsync(cts.Token));
        }
    }

    /// <summary>Startup runs once after engine recovery; it never restarts browser feed collection.</summary>
    public async Task InitializeAsync(CancellationToken token = default)
    {
        await _tickGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Completed videos leave the task. Unfinished tasks stay, with their remaining items.
            foreach (var session in await _sessions.GetSessionsAsync(token).ConfigureAwait(false))
                await SyncItemStatusesAsync(session.Id, token).ConfigureAwait(false);
            await _sessions.DeleteCompletedItemsAsync(token).ConfigureAwait(false);
            await _sessions.DeleteEmptySessionsAsync(token).ConfigureAwait(false);
            lock (_gate) _sequentialEnabled = true;
        }
        finally { _tickGate.Release(); }
        Start();
    }

    /// <summary>Pauses every in-flight download in the queue. Holds the scheduler tick so it cannot resume them mid-pause.</summary>
    public async Task PauseAllListedDownloadsAsync(CancellationToken token = default)
    {
        await _tickGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await PauseListedDownloadsCoreAsync(token).ConfigureAwait(false);
        }
        finally { _tickGate.Release(); }
    }

    public void SetActiveSession(Guid? sessionId)
    {
        lock (_gate)
        {
            _activeSessionId = sessionId;
            _producerSessionId = sessionId;
            _sequentialEnabled = sessionId is not null;
            _resumeAttempts.Clear();
            _retryItems.Clear();
            if (sessionId is Guid id) _visitedSessions.TryRemove(id, out _);
        }
        Start();
    }

    // An explicit user stop suspends chaining, while status observation remains alive.
    public void Stop() => SetActiveSession(null);

    public void FinishProducer()
    {
        lock (_gate)
        {
            _producerSessionId = null;
            _sequentialEnabled = true;
        }
        Start();
    }

    public async Task ActivateDownloadSessionAsync(Guid sessionId, CancellationToken token = default)
    {
        await _tickGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var session = await _sessions.GetSessionAsync(sessionId, token).ConfigureAwait(false);
            if (session is null) return;
            await PauseOtherAutoWorkAsync(sessionId, token).ConfigureAwait(false);
            lock (_gate)
            {
                _activeSessionId = sessionId;
                _producerSessionId = null;
                _sequentialEnabled = true;
                _resumeAttempts.Clear();
                _retryItems.Clear();
                _visitedSessions.TryRemove(sessionId, out _);
            }
            session.Status = AutoSessionStatus.Running;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            session.EndedAt = null;
            await _sessions.SaveSessionAsync(session, token).ConfigureAwait(false);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _tickGate.Release(); }
        Start();
    }

    /// <summary>One auto task runs. Starting it pauses every other task and every in-flight download.</summary>
    private async Task PauseOtherAutoWorkAsync(Guid sessionToRun, CancellationToken token)
    {
        await PauseListedDownloadsCoreAsync(token).ConfigureAwait(false);
        foreach (var other in await _sessions.GetSessionsAsync(token).ConfigureAwait(false))
        {
            if (other.Id == sessionToRun || other.Status != AutoSessionStatus.Running)
                continue;
            other.Status = AutoSessionStatus.Paused;
            other.UpdatedAt = DateTimeOffset.UtcNow;
            await _sessions.SaveSessionAsync(other, token).ConfigureAwait(false);
        }
    }

    private async Task PauseListedDownloadsCoreAsync(CancellationToken token)
    {
        foreach (var job in _engine.GetActiveJobs())
        {
            if (job.Status != DownloadStatus.Downloading)
                continue;
            try { await _engine.PauseAsync(job.Id, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to pause listed download {JobId}", job.Id);
            }
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _tickGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var changed = false;
                    foreach (var session in await _sessions.GetSessionsAsync(token).ConfigureAwait(false))
                    {
                        changed |= await SyncItemStatusesAsync(session.Id, token).ConfigureAwait(false);
                        if (await _sessions.TryDeleteCompletedSessionAsync(session.Id, token).ConfigureAwait(false))
                        {
                            changed = true;
                            _logger.LogInformation("Removed completed auto session {SessionId}; downloaded videos retained", session.Id);
                        }
                    }
                    if (changed) StateChanged?.Invoke(this, EventArgs.Empty);
    
                    await ProcessDownloadQueueAsync(token).ConfigureAwait(false);
                }
                finally { _tickGate.Release(); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto download scheduler tick failed");
            }

            try { await Task.Delay(400, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }

    private async Task ProcessDownloadQueueAsync(CancellationToken token)
    {
        Guid? sid;
        lock (_gate)
        {
            if (!_sequentialEnabled) return;
            sid = _activeSessionId;
        }
        if (sid is null)
        {
            foreach (var candidate in await _sessions.GetSessionsAsync(token).ConfigureAwait(false))
            {
                if (_visitedSessions.ContainsKey(candidate.Id)) continue;
                var candidateItems = await _sessions.GetItemsAsync(candidate.Id, token).ConfigureAwait(false);
                if (!candidateItems.Any(i => i.Status is AutoQueueItemStatus.Queued or AutoQueueItemStatus.Enqueued
                        or AutoQueueItemStatus.Downloading or AutoQueueItemStatus.Failed)) continue;
                await PauseOtherAutoWorkAsync(candidate.Id, token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (!_sequentialEnabled || _activeSessionId is not null) return;
                    _activeSessionId = sid = candidate.Id;
                    _resumeAttempts.Clear();
                    _retryItems.Clear();
                }
                candidate.Status = AutoSessionStatus.Running;
                candidate.UpdatedAt = DateTimeOffset.UtcNow;
                await _sessions.SaveSessionAsync(candidate, token).ConfigureAwait(false);
                _logger.LogInformation("Auto download queue activated session {SessionId}", candidate.Id);
                StateChanged?.Invoke(this, EventArgs.Empty);
                break;
            }
        }
        if (sid is not Guid id) return;
        var session = await _sessions.GetSessionAsync(id, token).ConfigureAwait(false);
        if (session is null)
        {
            lock (_gate) { if (_activeSessionId == id) _activeSessionId = null; }
            return;
        }
        lock (_gate) { if (!_sequentialEnabled || _activeSessionId != id) return; }
        if (session.Status != AutoSessionStatus.Running)
        {
            session.Status = AutoSessionStatus.Running;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await _sessions.SaveSessionAsync(session, token).ConfigureAwait(false);
        }
        var startedWork = false;
        await ReclaimStrandedAsync(id, token).ConfigureAwait(false);
        var items = await _sessions.GetItemsAsync(id, token).ConfigureAwait(false);
        var jobs = _engine.GetActiveJobs().ToDictionary(j => j.Id);
        foreach (var item in items.OrderBy(i => i.Seq))
        {
            lock (_gate) { if (!_sequentialEnabled || _activeSessionId != id) return; }
            if (_engine.FreeDownloadSlots <= 0) break;
            if (item.DownloadJobId is Guid jobId && jobs.TryGetValue(jobId, out var job))
            {
                if (job.Status is DownloadStatus.Pending or DownloadStatus.Paused or DownloadStatus.Failed &&
                    _resumeAttempts.TryAdd(jobId, 0))
                {
                    await _engine.ResumeAsync(jobId, token).ConfigureAwait(false);
                    startedWork = true;
                }
            }
            else if (item.Status is AutoQueueItemStatus.Enqueued or AutoQueueItemStatus.Downloading ||
                     item.Status == AutoQueueItemStatus.Failed && _retryItems.TryAdd(item.Id, 0))
            {
                item.DownloadJobId = null;
                item.Status = AutoQueueItemStatus.Queued;
                item.ErrorCode = null;
                await _sessions.SaveItemAsync(item, token).ConfigureAwait(false);
            }
        }
        lock (_gate) { if (!_sequentialEnabled || _activeSessionId != id) return; }
        startedWork |= await TryEnqueueNextAsync(id, token).ConfigureAwait(false);
        if (startedWork) return;
        // Re-read engine state after starting work. Never advance while an item is queued or in flight.
        await SyncItemStatusesAsync(id, token).ConfigureAwait(false);
        items = await _sessions.GetItemsAsync(id, token).ConfigureAwait(false);
        lock (_gate) { if (_producerSessionId == id || !_sequentialEnabled || _activeSessionId != id) return; }
        if (items.Any(i => i.Status is AutoQueueItemStatus.Queued or AutoQueueItemStatus.Enqueued or AutoQueueItemStatus.Downloading)) return;
        if (items.Any(i => i.Status == AutoQueueItemStatus.Failed &&
            (i.DownloadJobId is Guid jobId && jobs.ContainsKey(jobId)
                ? !_resumeAttempts.ContainsKey(jobId) : !_retryItems.ContainsKey(i.Id)))) return;
        session.Status = items.All(i => i.Status == AutoQueueItemStatus.Completed)
            ? AutoSessionStatus.Completed : AutoSessionStatus.Failed;
        session.EndedAt = session.UpdatedAt = DateTimeOffset.UtcNow;
        await _sessions.SaveSessionAsync(session, token).ConfigureAwait(false);
        _visitedSessions.TryAdd(id, 0);
        await _sessions.TryDeleteCompletedSessionAsync(id, token).ConfigureAwait(false);
        lock (_gate) { if (_activeSessionId == id) _activeSessionId = null; }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ReclaimStrandedAsync(Guid sessionId, CancellationToken token)
    {
        var items = await _sessions.GetItemsAsync(sessionId, token).ConfigureAwait(false);
        foreach (var item in items.Where(i =>
                     i.Status == AutoQueueItemStatus.Enqueued && i.DownloadJobId is null))
        {
            item.Status = AutoQueueItemStatus.Queued;
            await _sessions.SaveItemAsync(item, token).ConfigureAwait(false);
        }
    }

    private async Task<bool> TryEnqueueNextAsync(Guid sessionId, CancellationToken token)
    {
        if (_engine.FreeDownloadSlots <= 0)
            return false;

        var session = await _sessions.GetSessionAsync(sessionId, token).ConfigureAwait(false);
        if (session is null || session.Status != AutoSessionStatus.Running)
            return false;

        var item = await _sessions.TryClaimNextQueuedAsync(sessionId, token).ConfigureAwait(false);
        if (item is null)
            return false;

        try
        {
            var variant = DownloadJobMapper.DeserializeVariant(
                item.SourceUrl, item.VariantMetaJson, item.VariantSecret);
            // Prefer the session-selected quality when present; keep the full ladder as siblings.
            if (!string.IsNullOrWhiteSpace(item.SelectedVariantId) &&
                !string.Equals(variant.VariantId, item.SelectedVariantId, StringComparison.Ordinal) &&
                variant.Alternatives.FirstOrDefault(v =>
                    string.Equals(v.VariantId, item.SelectedVariantId, StringComparison.Ordinal)) is { } chosen)
            {
                variant = MediaVariantAlternatives.WithLadder(chosen,
                    variant.Alternatives.Prepend(variant));
            }

            var pageUrl = Uri.TryCreate(item.PageUrl, UriKind.Absolute, out var page) ? page : null;
            var displayName = DownloadFileNameBuilder.ClampStem(
                string.IsNullOrWhiteSpace(item.Caption) ? "auto" : item.Caption!);
            var siblings = variant.Alternatives.Count > 0
                ? variant.Alternatives
                : null;
            var jobId = await _engine.EnqueueAsync(
                variant,
                displayName,
                pageUrl,
                item.Caption,
                durationSec: null,
                siblingVariants: siblings,
                author: item.Author,
                ct: token).ConfigureAwait(false);

            item.DownloadJobId = jobId;
            item.Status = AutoQueueItemStatus.Enqueued;
            item.ErrorCode = null;
            await _sessions.SaveItemAsync(item, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enqueue auto queue item {ItemId}", item.Id);
            item.Status = AutoQueueItemStatus.Failed;
            item.ErrorCode = ex is DownloadException de ? de.ErrorCode : "auto_enqueue_failed";
            await _sessions.SaveItemAsync(item, token).ConfigureAwait(false);
        }
        return true;
    }

    private async Task<bool> SyncItemStatusesAsync(Guid sessionId, CancellationToken token)
    {
        var items = await _sessions.GetItemsAsync(sessionId, token).ConfigureAwait(false);
        var jobs = _engine.GetActiveJobs().ToDictionary(j => j.Id);
        var changed = false;
        foreach (var item in items)
        {
            if (item.DownloadJobId is not Guid jobId)
                continue;
            if (!jobs.TryGetValue(jobId, out var job))
                continue;

            var next = AutoQueueItemState.FromDownload(job.Status, item.Status);
            if (next == item.Status && job.LastErrorCode == item.ErrorCode)
                continue;

            item.Status = next;
            item.ErrorCode = job.LastErrorCode;
            await _sessions.SaveItemAsync(item, token).ConfigureAwait(false);
            changed = true;
        }
        return changed;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _activeSessionId = null;
            _cts?.Cancel();
        }
        // The loop owns outstanding asynchronous work; its token remains valid after disposal.
        _cts?.Dispose();
    }
}
