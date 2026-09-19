using VideoDownloader.Core.Subtitles;
using VideoDownloader.Core.Subtitles.Contracts;

namespace VideoDownloader.Infrastructure.Subtitles;

public sealed class SubtitlePipeline : ISubtitlePipeline
{
    private readonly ISpeechRecognizer _speechRecognizer;
    private readonly ISubtitleTimeline _timeline;
    private readonly ISubtitleTranslator _translator;
    private readonly SemaphoreSlim _recognitionGate = new(1, 1);
    private readonly SemaphoreSlim _translationGate = new(1, 1);
    private CancellationTokenSource? _sessionCts;
    private string? _mediaIdentity;

    public SubtitlePipeline(
        ISpeechRecognizer speechRecognizer,
        ISubtitleTimeline timeline,
        ISubtitleTranslator translator)
    {
        _speechRecognizer = speechRecognizer;
        _timeline = timeline;
        _translator = translator;
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

    public async Task PrepareTranslationsAsync(
        SubtitleMode mode,
        TimeSpan start,
        TimeSpan end,
        CancellationToken cancellationToken = default)
    {
        if (mode == SubtitleMode.Original || end <= start)
            return;

        var sessionId = ActiveSessionId;
        var sessionCts = _sessionCts;
        if (sessionId is null || sessionCts is null)
            return;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            sessionCts.Token);
        await _translationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (!string.Equals(sessionId, ActiveSessionId, StringComparison.Ordinal))
                return;

            var target = mode == SubtitleMode.Chinese ? "zh" : "en";
            var pending = new List<SubtitleSegment>();
            foreach (var segment in _timeline.Snapshot().Where(x => x.End > start && x.Start < end))
            {
                linked.Token.ThrowIfCancellationRequested();
                if (IsAlreadyReady(segment, mode) || IsSameLanguage(segment.SourceLanguage, target))
                {
                    segment.State = SubtitleSegmentState.Ready;
                    continue;
                }

                segment.State = SubtitleSegmentState.Translating;
                pending.Add(segment);
            }

            if (pending.Count == 0)
                return;

            try
            {
                var requests = pending
                    .Select(segment => new TranslationRequest(
                        segment.OriginalText,
                        segment.SourceLanguage,
                        target))
                    .ToArray();
                var results = await _translator.TranslateBatchAsync(requests, linked.Token)
                    .ConfigureAwait(false);

                if (results.Count != pending.Count)
                    throw new InvalidOperationException("Subtitle translation batch size mismatch.");
                if (!string.Equals(sessionId, ActiveSessionId, StringComparison.Ordinal))
                    return;

                for (var i = 0; i < pending.Count; i++)
                {
                    var segment = pending[i];
                    var text = results[i].Text?.Trim();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        segment.State = SubtitleSegmentState.Failed;
                        continue;
                    }

                    if (mode == SubtitleMode.Chinese)
                        segment.ChineseText = text;
                    else
                        segment.EnglishText = text;
                    segment.State = SubtitleSegmentState.Ready;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Translation is optional. All failed target segments still display OriginalText.
                foreach (var segment in pending)
                    segment.State = SubtitleSegmentState.Failed;
            }
        }
        finally
        {
            _translationGate.Release();
        }
    }

    public SubtitleSegment? GetCurrent(TimeSpan mediaTime, SubtitleMode mode)
    {
        _ = mode;
        return _timeline.Find(mediaTime);
    }

    public Task StopSessionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopSessionCore();
        return Task.CompletedTask;
    }

    private static bool IsAlreadyReady(SubtitleSegment segment, SubtitleMode mode) =>
        mode == SubtitleMode.Chinese
            ? !string.IsNullOrWhiteSpace(segment.ChineseText)
            : !string.IsNullOrWhiteSpace(segment.EnglishText);

    private static bool IsSameLanguage(string source, string target)
    {
        if (string.IsNullOrWhiteSpace(source))
            return false;
        source = source.Trim().ToLowerInvariant();
        return target == "zh"
            ? source is "zh" or "zh-cn" or "chinese"
            : source is "en" or "en-us" or "en-gb" or "english";
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
