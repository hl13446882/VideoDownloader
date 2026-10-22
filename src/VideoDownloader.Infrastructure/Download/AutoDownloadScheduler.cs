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

    public AutoDownloadScheduler(
        IAutoSessionRepository sessions,
        IDownloadEngine engine,
        ILogger<AutoDownloadScheduler> logger)
    {
        _sessions = sessions;
        _engine = engine;
        _logger = logger;
    }

    public Guid? ActiveSessionId
    {
        get { lock (_gate) return _activeSessionId; }
    }

    public void SetActiveSession(Guid? sessionId)
    {
        lock (_gate)
        {
            _activeSessionId = sessionId;
            if (sessionId is null)
            {
                try { _cts?.Cancel(); } catch { /* ignore */ }
                return;
            }

            if (_loop is { IsCompleted: false })
                return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }
    }

    public void Stop() => SetActiveSession(null);

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                Guid? sessionId;
                lock (_gate) sessionId = _activeSessionId;
                if (sessionId is Guid sid)
                {
                    await ReclaimStrandedAsync(sid, token).ConfigureAwait(false);
                    await SyncItemStatusesAsync(sid, token).ConfigureAwait(false);
                    await TryEnqueueNextAsync(sid, token).ConfigureAwait(false);
                }
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

    private async Task TryEnqueueNextAsync(Guid sessionId, CancellationToken token)
    {
        if (_engine.FreeDownloadSlots <= 0)
            return;

        var session = await _sessions.GetSessionAsync(sessionId, token).ConfigureAwait(false);
        if (session is null || session.Status != AutoSessionStatus.Running)
            return;

        var item = await _sessions.TryClaimNextQueuedAsync(sessionId, token).ConfigureAwait(false);
        if (item is null)
            return;

        try
        {
            var variant = DownloadJobMapper.DeserializeVariant(
                item.SourceUrl, item.VariantMetaJson, item.VariantSecret);
            var pageUrl = Uri.TryCreate(item.PageUrl, UriKind.Absolute, out var page) ? page : null;
            var displayName = DownloadFileNameBuilder.ClampStem(
                string.IsNullOrWhiteSpace(item.Caption) ? "auto" : item.Caption!);
            var jobId = await _engine.EnqueueAsync(
                variant,
                displayName,
                pageUrl,
                item.Caption,
                durationSec: null,
                siblingVariants: null,
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
    }

    private async Task SyncItemStatusesAsync(Guid sessionId, CancellationToken token)
    {
        var items = await _sessions.GetItemsAsync(sessionId, token).ConfigureAwait(false);
        var jobs = _engine.GetActiveJobs().ToDictionary(j => j.Id);
        foreach (var item in items)
        {
            if (item.DownloadJobId is not Guid jobId)
                continue;
            if (!jobs.TryGetValue(jobId, out var job))
                continue;

            var next = job.Status switch
            {
                DownloadStatus.Downloading or DownloadStatus.Preparing or DownloadStatus.Muxing
                    => AutoQueueItemStatus.Downloading,
                DownloadStatus.Completed => AutoQueueItemStatus.Completed,
                DownloadStatus.Failed => AutoQueueItemStatus.Failed,
                DownloadStatus.Cancelled or DownloadStatus.Removed => AutoQueueItemStatus.Cancelled,
                DownloadStatus.Paused => item.Status == AutoQueueItemStatus.Downloading
                    ? AutoQueueItemStatus.Enqueued
                    : item.Status,
                _ => item.Status
            };
            if (next == item.Status &&
                (job.LastErrorCode is null || job.LastErrorCode == item.ErrorCode))
                continue;

            item.Status = next;
            item.ErrorCode = job.LastErrorCode;
            await _sessions.SaveItemAsync(item, token).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
