namespace VideoDownloader.Core.Detection;

/// <summary>Invalidates displayed discoveries while a same-document feed changes content.</summary>
public sealed class FeedDetectionDisplayState
{
    private long _epoch;
    private bool _waiting;
    private string? _previousPage;
    private string? _previousContent;
    private string? _previousMedia;

    public long Epoch => Interlocked.Read(ref _epoch);
    public bool IsSuspended => _waiting;

    public void Suspend(string page, string? content, string? media)
    {
        _previousPage = page;
        _previousContent = content;
        _previousMedia = media;
        _waiting = true;
        Interlocked.Increment(ref _epoch);
    }

    public bool CanBeginSession(string page, string? content, string? media)
    {
        if (!_waiting) return true;
        // URL/query jitter for the retired work must not revive its session.
        if (!string.IsNullOrEmpty(content) && !string.IsNullOrEmpty(_previousContent))
            return !string.Equals(content, _previousContent, StringComparison.Ordinal);
        if (!string.Equals(page, _previousPage, StringComparison.Ordinal)) return true;
        if (!string.IsNullOrEmpty(content)) return true;
        return !string.IsNullOrEmpty(media) &&
               !string.Equals(media, _previousMedia, StringComparison.Ordinal);
    }

    public void BeginSession()
    {
        _waiting = false;
        Interlocked.Increment(ref _epoch);
    }

    public bool CanPublish(long capturedEpoch, Guid resultSession, Guid currentSession) =>
        !_waiting && capturedEpoch == Epoch &&
        resultSession == currentSession;
}
