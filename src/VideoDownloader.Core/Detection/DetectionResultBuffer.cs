using VideoDownloader.Core.Models;

namespace VideoDownloader.Core.Detection;

/// <summary>One session stages results privately and publishes a single final snapshot.</summary>
public sealed class DetectionResultBuffer(Guid sessionId)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DetectedVideo> _results = new();
    private bool _closed;

    public bool HasUsableResult
    {
        get { lock (_gate) return !_closed && _results.Values.Any(DetectionReadiness.IsUsable); }
    }

    public bool HasLiveResult
    {
        get { lock (_gate) return !_closed && _results.Values.Any(v => v.IsLive); }
    }

    public bool Stage(DetectedVideo video)
    {
        lock (_gate)
        {
            if (_closed || video.SessionId != sessionId || video.IsFormatDiscoveryPending ||
                (video.Variants.Count == 0 && !video.IsLive)) return false;
            if (_results.TryGetValue(video.VideoId, out var previous) &&
                ((DetectionReadiness.IsUsable(previous) && !DetectionReadiness.IsUsable(video)) ||
                 previous.Variants.Select(v => v.Height ?? 0).DefaultIfEmpty().Max() >
                 video.Variants.Select(v => v.Height ?? 0).DefaultIfEmpty().Max())) return false;
            _results[video.VideoId] = video;
            return true;
        }
    }

    public IReadOnlyList<DetectedVideo> Complete()
    {
        lock (_gate)
        {
            if (_closed) return [];
            _closed = true;
            return _results.Values.Where(v => v.IsLive || DetectionReadiness.IsUsable(v)).ToArray();
        }
    }

    public void Cancel()
    {
        lock (_gate) { _closed = true; _results.Clear(); }
    }
}
