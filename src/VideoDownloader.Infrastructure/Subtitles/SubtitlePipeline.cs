using VideoDownloader.Core.Subtitles;
using VideoDownloader.Core.Subtitles.Contracts;

namespace VideoDownloader.Infrastructure.Subtitles;

public sealed class SubtitlePipeline : ISubtitlePipeline
{
    private readonly ISpeechRecognizer _speechRecognizer;
    private readonly ISubtitleTimeline _timeline;
    private readonly ISubtitleTranslator _translator;
    private readonly ISubtitleCacheStore _cache;
    private readonly SemaphoreSlim _recognitionGate = new(1, 1);
    private readonly SemaphoreSlim _translationGate = new(1, 1);
    private readonly object _coverageSync = new();
    private readonly List<SubtitleCoverageRange> _coverage = new();
    private CancellationTokenSource? _sessionCts;
    private string? _mediaIdentity;

    public SubtitlePipeline(
        ISpeechRecognizer speechRecognizer,
        ISubtitleTimeline timeline,
        ISubtitleTranslator translator,
        ISubtitleCacheStore cache)
    {
        _speechRecognizer = speechRecognizer;
        _timeline = timeline;
        _translator = translator;
        _cache = cache;
    }

    public string? ActiveSessionId { get; private set; }

    public async Task StartSessionAsync(
        string sessionId,
        string? mediaIdentity,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopSessionCore();
        ActiveSessionId = sessionId;
        _mediaIdentity = mediaIdentity;
        _timeline.Clear();
        lock (_coverageSync)
            _coverage.Clear();
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (string.IsNullOrWhiteSpace(mediaIdentity))
            return;

        try
        {
            var cached = await _cache.LoadAsync(mediaIdentity, _sessionCts.Token).ConfigureAwait(false);
            if (!string.Equals(sessionId, ActiveSessionId, StringComparison.Ordinal))
                return;

            _timeline.AddOrUpdate(cached.Segments);
            ReplaceCoverage(cached.Coverage);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A newer subtitle session replaced this one while the cache was loading.
        }
        catch
        {
            // Cache corruption or IO failure must never block a fresh subtitle session.
        }
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

            if (!string.Equals(sessionId, ActiveSessionId, StringComparison.Ordinal))
                return;

            _timeline.AddOrUpdate(result);
            AddCoverage(chunk.MediaStart, chunk.MediaEnd);
            await PersistAsync(sessionId, linked.Token).ConfigureAwait(false);
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

            if (mode == SubtitleMode.Bilingual)
            {
                await TranslateMissingSideAsync(sessionId, "zh", start, end, linked.Token).ConfigureAwait(false);
                await TranslateMissingSideAsync(sessionId, "en", start, end, linked.Token).ConfigureAwait(false);
                return;
            }

            var target = mode == SubtitleMode.Chinese ? "zh" : "en";
            await TranslateMissingSideAsync(sessionId, target, start, end, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _translationGate.Release();
        }
    }

    private async Task TranslateMissingSideAsync(
        string sessionId,
        string target,
        TimeSpan start,
        TimeSpan end,
        CancellationToken cancellationToken)
    {
        var pending = new List<SubtitleSegment>();
        foreach (var segment in _timeline.Snapshot().Where(x => x.End > start && x.Start < end))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasSideText(segment, target) || IsSameLanguage(segment.SourceLanguage, target))
            {
                if (IsSameLanguage(segment.SourceLanguage, target))
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
            var results = await _translator.TranslateBatchAsync(requests, cancellationToken)
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

                if (target == "zh")
                    segment.ChineseText = text;
                else
                    segment.EnglishText = text;
                segment.State = SubtitleSegmentState.Ready;
            }

            await PersistAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Translation is optional. Failed target segments still display OriginalText.
            foreach (var segment in pending)
                segment.State = SubtitleSegmentState.Failed;
        }
    }

    public SubtitleSegment? GetCurrent(TimeSpan mediaTime, SubtitleMode mode)
    {
        _ = mode;
        return _timeline.Find(mediaTime);
    }

    public TimeSpan? GetCoveredUntil(TimeSpan mediaTime)
    {
        lock (_coverageSync)
        {
            foreach (var range in _coverage)
            {
                if (mediaTime < range.Start)
                    break;
                if (mediaTime >= range.Start && mediaTime <= range.End)
                    return range.End;
            }
        }
        return null;
    }

    public Task StopSessionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopSessionCore();
        return Task.CompletedTask;
    }

    private void ReplaceCoverage(IEnumerable<SubtitleCoverageRange> ranges)
    {
        lock (_coverageSync)
        {
            _coverage.Clear();
            foreach (var range in ranges.Where(x => x.End > x.Start).OrderBy(x => x.Start))
                AddCoverageLocked(range.Start, range.End);
        }
    }

    private void AddCoverage(TimeSpan start, TimeSpan end)
    {
        if (end <= start)
            return;
        lock (_coverageSync)
            AddCoverageLocked(start, end);
    }

    private void AddCoverageLocked(TimeSpan start, TimeSpan end)
    {
        var mergedStart = start;
        var mergedEnd = end;
        var tolerance = TimeSpan.FromMilliseconds(750);

        for (var i = _coverage.Count - 1; i >= 0; i--)
        {
            var current = _coverage[i];
            if (current.End + tolerance < mergedStart || current.Start - tolerance > mergedEnd)
                continue;

            if (current.Start < mergedStart)
                mergedStart = current.Start;
            if (current.End > mergedEnd)
                mergedEnd = current.End;
            _coverage.RemoveAt(i);
        }

        _coverage.Add(new SubtitleCoverageRange(mergedStart, mergedEnd));
        _coverage.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    private SubtitleCacheSnapshot SnapshotForCache()
    {
        SubtitleCoverageRange[] coverage;
        lock (_coverageSync)
            coverage = _coverage.ToArray();
        return new SubtitleCacheSnapshot(_timeline.Snapshot(), coverage);
    }

    private async Task PersistAsync(string sessionId, CancellationToken cancellationToken)
    {
        var identity = _mediaIdentity;
        if (string.IsNullOrWhiteSpace(identity) ||
            !string.Equals(sessionId, ActiveSessionId, StringComparison.Ordinal))
            return;

        await _cache.SaveAsync(identity, SnapshotForCache(), cancellationToken).ConfigureAwait(false);
    }

    private static bool HasSideText(SubtitleSegment segment, string target) =>
        target == "zh"
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
