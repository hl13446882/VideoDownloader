using Microsoft.Extensions.Logging;
using VideoDownloader.Core.Contracts;
using VideoDownloader.Core.Detection;
using VideoDownloader.Core.Models;
using VideoDownloader.Core.Sites;

namespace VideoDownloader.Infrastructure.Detection;

/// <summary>
/// Routes page detection to an exclusive site detector or Generic Unified pipeline.
/// Exclusive sites never fall back to Unified/Generic.
/// </summary>
public sealed class RoutedMediaDetectionPipeline : IMediaDetectionPipeline
{
    private readonly UnifiedMediaPipeline _unified;
    private readonly ISiteDetectionRouter _router;
    private readonly IExclusiveSiteMediaDetectorResolver _detectors;
    private readonly ILogger<RoutedMediaDetectionPipeline> _logger;
    private readonly object _gate = new();

    private DetectionSession _session = new();
    private IExclusiveSiteMediaDetector? _active;
    private SiteKind _kind = SiteKind.Other;
    private Uri? _page;
    private IReadOnlyList<DetectedVideo> _lastBuilt = [];
    private bool _exclusiveCompleted;

    public RoutedMediaDetectionPipeline(
        UnifiedMediaPipeline unified,
        ISiteDetectionRouter router,
        IExclusiveSiteMediaDetectorResolver detectors,
        ILogger<RoutedMediaDetectionPipeline> logger)
    {
        _unified = unified;
        _router = router;
        _detectors = detectors;
        _logger = logger;
        _unified.VideoDetected += (_, v) =>
        {
            if (_active is null) VideoDetected?.Invoke(this, v);
        };
        _unified.VideoUpdated += (_, v) =>
        {
            if (_active is null) VideoUpdated?.Invoke(this, v);
        };
        _unified.PageProbed += (_, list) =>
        {
            if (_active is null) PageProbed?.Invoke(this, list);
        };
    }

    public Guid SessionId
    {
        get
        {
            lock (_gate)
                return _active is not null ? _session.Id : _unified.SessionId;
        }
    }

    public bool IsCompleted
    {
        get
        {
            lock (_gate)
                return _active is not null
                    ? _session.Phase == DetectionPhase.Completed
                    : _unified.IsCompleted;
        }
    }

    public event EventHandler<DetectedVideo>? VideoDetected;
    public event EventHandler<DetectedVideo>? VideoUpdated;
    public event EventHandler<IReadOnlyList<DetectedVideo>>? PageProbed;

    public void Clear()
    {
        lock (_gate)
        {
            ClearExclusiveUnlocked();
            _session.Cancel();
            _session = new();
            _page = null;
            _kind = SiteKind.Other;
            _lastBuilt = [];
            _exclusiveCompleted = false;
            _unified.Clear();
        }
    }

    public void UpdateCaption(Guid sessionId, string caption)
    {
        lock (_gate)
        {
            if (_active is null)
                _unified.UpdateCaption(sessionId, caption);
        }
    }

    public IDiscoveryScope? BeginDiscovery(Guid sessionId)
    {
        lock (_gate)
        {
            if (_active is not null)
            {
                if (sessionId != _session.Id) return null;
                var lease = _session.TryEnter();
                return lease is null ? null : new ExclusiveScope(this, sessionId, lease);
            }

            return _unified.BeginDiscovery(sessionId);
        }
    }

    public Task ProcessAsync(NormalizedNetworkEvent normalized, CancellationToken ct)
    {
        IExclusiveSiteMediaDetector? exclusive;
        lock (_gate)
        {
            var page = normalized.PageUrl ?? _page;
            if (page is not null)
                EnsureRouteUnlocked(page);

            exclusive = _active;
            if (exclusive is not null)
            {
                if (normalized.SessionId != Guid.Empty && normalized.SessionId != _session.Id)
                    return Task.CompletedTask;
            }
        }

        if (exclusive is not null)
            return exclusive.ProcessNetworkAsync(normalized, ct);
        return _unified.ProcessAsync(normalized, ct);
    }

    public Task ProbePageAsync(
        Uri pageUrl,
        string? pageTitle,
        string? pageScriptJson,
        RequestContext context,
        CancellationToken ct,
        bool runExternal = false)
    {
        IExclusiveSiteMediaDetector? exclusive;
        lock (_gate)
        {
            EnsureRouteUnlocked(pageUrl);
            exclusive = _active;
        }

        if (exclusive is not null)
            return exclusive.ProcessPageObservationAsync(pageUrl, pageTitle, pageScriptJson, context, ct);
        return _unified.ProbePageAsync(pageUrl, pageTitle, pageScriptJson, context, ct, runExternal);
    }

    public async Task CompleteDiscoveryAsync(CancellationToken ct)
    {
        IExclusiveSiteMediaDetector? exclusive;
        lock (_gate)
        {
            exclusive = _active;
            if (exclusive is null)
            {
                // fall through outside lock
            }
            else
            {
                // seal exclusive session then complete detector
            }
        }

        if (exclusive is null)
        {
            await _unified.CompleteDiscoveryAsync(ct).ConfigureAwait(false);
            return;
        }

        Task idle;
        lock (_gate)
            idle = _session.CompleteAsync(ct);

        await exclusive.CompleteAsync(ct).ConfigureAwait(false);
        await idle.ConfigureAwait(false);

        lock (_gate)
        {
            if (_exclusiveCompleted) return;
            _exclusiveCompleted = true;

            if (exclusive.Failed)
            {
                _logger.LogWarning(
                    "Exclusive detection failed site={Site} detector={Detector} reason={Reason} (GenericPipeline=NotUsed)",
                    exclusive.Site, exclusive.Name, exclusive.FailureReason);
                _lastBuilt = [];
                PageProbed?.Invoke(this, []);
                return;
            }

            // Descriptors may already have been emitted via event; rebuild if empty.
            if (_lastBuilt.Count == 0)
            {
                _logger.LogInformation(
                    "Exclusive detection completed with no descriptors site={Site} detector={Detector}",
                    exclusive.Site, exclusive.Name);
                PageProbed?.Invoke(this, []);
            }
            else
            {
                PageProbed?.Invoke(this, _lastBuilt);
            }
        }
    }

    private void EnsureRouteUnlocked(Uri pageUrl)
    {
        var kind = _router.Resolve(pageUrl);
        var detector = kind == SiteKind.Other ? null : _detectors.Resolve(pageUrl);

        if (_page is not null &&
            string.Equals(_page.Host, pageUrl.Host, StringComparison.OrdinalIgnoreCase) &&
            _kind == kind &&
            ReferenceEquals(_active, detector))
        {
            _page = pageUrl;
            return;
        }

        // Host / site kind changed — reset exclusive state; keep unified cleared for exclusive.
        if (_active is not null || detector is not null)
        {
            ClearExclusiveUnlocked();
            _session.Cancel();
            _session = new();
            _lastBuilt = [];
            _exclusiveCompleted = false;
            _unified.Clear();
        }

        _page = pageUrl;
        _kind = kind;
        _active = detector;

        if (_active is not null)
        {
            _active.DescriptorsReady -= OnDescriptorsReady;
            _active.DescriptorsReady += OnDescriptorsReady;
            _active.BeginSession(pageUrl, _session.Id);
            _logger.LogInformation(
                "[DetectionRouter] Site={Site} Detector={Detector} Exclusive=true GenericPipeline=Bypassed page={Path}",
                _router.Describe(kind), _active.Name, pageUrl.AbsolutePath);
        }
        else
        {
            _logger.LogInformation(
                "[DetectionRouter] Site=Other Detector=UnifiedMediaPipeline Exclusive=false GenericPipeline=Active page={Path}",
                pageUrl.AbsolutePath);
        }
    }

    private void ClearExclusiveUnlocked()
    {
        if (_active is not null)
        {
            _active.DescriptorsReady -= OnDescriptorsReady;
            _active.Clear();
            _active = null;
        }

        foreach (var d in _detectors.All)
            d.Clear();
    }

    private void OnDescriptorsReady(object? sender, IReadOnlyList<MediaDescriptor> descriptors)
    {
        List<DetectedVideo> videos;
        lock (_gate)
        {
            videos = descriptors
                .Select(d => MediaDescriptorMapper.ToDetectedVideo(d, _session.Id))
                .ToList();
            _lastBuilt = videos;
        }

        foreach (var video in videos)
            VideoDetected?.Invoke(this, video);
    }

    private sealed class ExclusiveScope(RoutedMediaDetectionPipeline owner, Guid sessionId, IDisposable lease) : IDiscoveryScope
    {
        private bool _disposed;

        public IDiscoveryScope? Fork()
        {
            lock (owner._gate)
            {
                if (_disposed || sessionId != owner._session.Id || owner._active is null) return null;
                var child = owner._session.TryEnter(continuation: true);
                return child is null ? null : new ExclusiveScope(owner, sessionId, child);
            }
        }

        public Task ProcessAsync(NormalizedNetworkEvent network, CancellationToken ct)
        {
            lock (owner._gate)
            {
                if (_disposed || sessionId != owner._session.Id || owner._active is null)
                    return Task.CompletedTask;
                return owner._active.ProcessNetworkAsync(network, ct);
            }
        }

        public Task SubmitAsync(Uri page, string json, RequestContext context, CancellationToken ct)
        {
            lock (owner._gate)
            {
                if (_disposed || sessionId != owner._session.Id || owner._active is null)
                    return Task.CompletedTask;
                return owner._active.ProcessPageObservationAsync(page, null, json, context, ct);
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                if (_disposed) return;
                _disposed = true;
                lease.Dispose();
            }
        }
    }
}
