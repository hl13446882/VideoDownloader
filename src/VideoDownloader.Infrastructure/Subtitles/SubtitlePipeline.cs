using VideoDownloader.Core.Subtitles;
using VideoDownloader.Core.Subtitles.Contracts;

namespace VideoDownloader.Infrastructure.Subtitles;

public sealed class SubtitlePipeline : ISubtitlePipeline
{
    private readonly ISpeechRecognizer _speechRecognizer;
    private readonly ISubtitleTimeline _timeline;
    private readonly SemaphoreSlim _recognitionGate = new(1, 1);
    private CancellationTokenSource? _sessionCts;
    private string? _mediaIdentity;

    public SubtitlePipeline(ISpeechRecognizer speechRecognizer, ISubtitleTimeline timeline)
    {
        _speechRecognizer = speechRecognizer;
        _timeline = timeline;
    }

    public string? ActiveSessionId { get; private set; }

    public Task StartSessionAsync(
        string sessionId,
        string? mediaIdentity,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopSessionCore();
        ActiveSessionId = sessionId;
        _mediaIdentity = mediaIdentity;
        _timeline.Clear();
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        return Task.CompletedTask;
    }

    public async Task SubmitAudioAsync(
        AudioChunk chunk,
        CancellationToken cancellationToken = default)
    {
        var sessionId = ActiveSessionId;
        var sessionCts = _sessionCts;
        if (sessionId is null || sessionCts is null || chunk.Pcm16Mono16Khz.IsEmpty)
            return;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            sessionCts.Token);
        await _recognitionGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (!string.Equals(sessionId, ActiveSessionId, StringComparison.Ordinal))
                return;

            var result = await _speechRecognizer.RecognizeAsync(
                chunk,
                new SpeechRecognitionContext(sessionId, _mediaIdentity),
                linked.Token).ConfigureAwait(false);

            if (string.Equals(sessionId, ActiveSessionId, StringComparison.Ordinal))
                _timeline.AddOrUpdate(result);
        }
        finally
        {
            _recognitionGate.Release();
        }
    }

    public SubtitleSegment? GetCurrent(TimeSpan mediaTime, SubtitleMode mode)
    {
        _ = mode; // display selection is intentionally outside the timeline lookup.
        return _timeline.Find(mediaTime);
    }

    public Task StopSessionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopSessionCore();
        return Task.CompletedTask;
    }

    private void StopSessionCore()
    {
        ActiveSessionId = null;
        _mediaIdentity = null;
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = null;
    }
}
