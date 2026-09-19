using VideoDownloader.Core.Models;
using VideoDownloader.Core.Subtitles;
using VideoDownloader.Core.Subtitles.Contracts;
using VideoDownloader.Infrastructure.Configuration;

namespace VideoDownloader.Infrastructure.Subtitles.Browser;

/// <summary>
/// Per-WebView subtitle runtime. It prefers variants already discovered by the normal detection
/// pipeline and falls back to the browser's direct http(s) currentSrc when available.
/// </summary>
public sealed class BrowserSubtitleRuntime : IAsyncDisposable
{
    private readonly WebViewSubtitleBridge _bridge;
    private readonly ISubtitlePipeline _pipeline;
    private readonly IMediaAudioDecoder _audioDecoder;
    private readonly SubtitleMediaVariantRegistry _variantRegistry;
    private readonly SubtitleOptions _options;
    private readonly SemaphoreSlim _scheduleGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly HashSet<string> _translationRequests = new(StringComparer.Ordinal);
    private readonly object _translationSync = new();
    private CancellationTokenSource? _workCts;
    private Task? _workTask;
    private string? _mediaKey;
    private string? _pageUrl;
    private MediaVariant? _variant;
    private TimeSpan _coveredUntil;
    private TimeSpan? _lastPlaybackTime;
    private string? _lastDisplayed;
    private string? _styleFingerprint;

    public BrowserSubtitleRuntime(
        WebViewSubtitleBridge bridge,
        ISubtitlePipeline pipeline,
        IMediaAudioDecoder audioDecoder,
        SubtitleMediaVariantRegistry variantRegistry,
        SubtitleOptions options)
    {
        _bridge = bridge;
        _pipeline = pipeline;
        _audioDecoder = audioDecoder;
        _variantRegistry = variantRegistry;
        _options = options;
        _bridge.PlaybackStateChanged += OnPlaybackStateChanged;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _bridge.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await ApplyStyleIfChangedAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task ApplyCurrentStyleAsync(CancellationToken cancellationToken = default)
    {
        _styleFingerprint = null;
        return ApplyStyleIfChangedAsync(cancellationToken);
    }

    private void OnPlaybackStateChanged(object? sender, SubtitlePlaybackState state)
    {
        _ = HandlePlaybackAsync(state, _lifetimeCts.Token);
    }

    private async Task HandlePlaybackAsync(SubtitlePlaybackState state, CancellationToken cancellationToken)
    {
        try
        {
            await ApplyStyleIfChangedAsync(cancellationToken).ConfigureAwait(false);

            if (!_options.Enabled)
            {
                await SetDisplayedAsync(null, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrWhiteSpace(state.MediaKey))
            {
                await SetDisplayedAsync(null, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!string.Equals(_mediaKey, state.MediaKey, StringComparison.Ordinal) ||
                !string.Equals(_pageUrl, state.PageUrl, StringComparison.Ordinal))
            {
                await SwitchMediaAsync(state.MediaKey, state.PageUrl, cancellationToken).ConfigureAwait(false);
            }
            else if (_variant is null)
            {
                // Detection may finish after playback begins. Pick it up without restarting the session.
                _variant = _variantRegistry.Resolve(state.PageUrl, state.MediaKey) ?? CreateDirectVariant(state.MediaKey);
            }

            var jumped = _lastPlaybackTime is { } previous &&
                         Math.Abs((state.CurrentTime - previous).TotalSeconds) >= 2.5;
            _lastPlaybackTime = state.CurrentTime;
            if (state.Seeking || jumped)
                CancelActiveWindow();

            var displayTime = state.CurrentTime - TimeSpan.FromMilliseconds(_options.SubtitleOffsetMs);
            if (displayTime < TimeSpan.Zero)
                displayTime = TimeSpan.Zero;
            var segment = _pipeline.GetCurrent(displayTime, _options.Mode);
            if (segment is not null)
                RequestMissingTranslation(segment, _options.Mode, cancellationToken);
            await SetDisplayedAsync(segment?.GetDisplayText(_options.Mode), cancellationToken).ConfigureAwait(false);

            if (_variant is null || state.Paused || state.Seeking)
                return;

            var windowSize = TimeSpan.FromSeconds(Math.Clamp(_options.PreloadAheadSeconds, 10, 90));
            var refillThreshold = TimeSpan.FromSeconds(Math.Max(5, windowSize.TotalSeconds / 3));
            if (state.CurrentTime + refillThreshold >= _coveredUntil)
                await EnsureWindowAsync(state.CurrentTime, state.Duration, windowSize, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Session switch / application shutdown.
        }
        catch
        {
            // Subtitle failures must never interrupt playback.
        }
    }

    private void RequestMissingTranslation(
        SubtitleSegment segment,
        SubtitleMode mode,
        CancellationToken cancellationToken)
    {
        if (mode == SubtitleMode.Original || HasTargetText(segment, mode) || IsSameLanguage(segment.SourceLanguage, mode))
            return;

        var key = segment.Id + ":" + mode;
        lock (_translationSync)
        {
            if (!_translationRequests.Add(key))
                return;
        }

        _ = TranslateExistingAsync(segment, mode, key, cancellationToken);
    }

    private async Task TranslateExistingAsync(
        SubtitleSegment segment,
        SubtitleMode mode,
        string key,
        CancellationToken cancellationToken)
    {
        try
        {
            await _pipeline.PrepareTranslationsAsync(mode, segment.Start, segment.End, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_translationSync)
                _translationRequests.Remove(key);
        }
        catch
        {
            // Original text remains visible.
        }
    }

    private async Task SwitchMediaAsync(
        string mediaKey,
        string? pageUrl,
        CancellationToken cancellationToken)
    {
        CancelActiveWindow();
        _workCts?.Dispose();
        _workCts = null;
        _workTask = null;
        _coveredUntil = TimeSpan.Zero;
        _lastPlaybackTime = null;
        _mediaKey = mediaKey;
        _pageUrl = pageUrl;
        _variant = _variantRegistry.Resolve(pageUrl, mediaKey) ?? CreateDirectVariant(mediaKey);
        _lastDisplayed = null;
        lock (_translationSync)
            _translationRequests.Clear();

        await _bridge.ClearSubtitleAsync(cancellationToken).ConfigureAwait(false);
        await _pipeline.StartSessionAsync(
            "webview:" + Guid.NewGuid().ToString("N"),
            mediaKey,
            cancellationToken).ConfigureAwait(false);
    }

    private void CancelActiveWindow()
    {
        try
        {
            if (_workTask is { IsCompleted: false })
                _workCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A concurrent media switch already disposed the token source.
        }
    }

    private async Task EnsureWindowAsync(
        TimeSpan currentTime,
        TimeSpan? duration,
        TimeSpan windowSize,
        CancellationToken cancellationToken)
    {
        if (_variant is null)
            return;

        await _scheduleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_workTask is { IsCompleted: false })
                return;

            var start = currentTime < _coveredUntil ? _coveredUntil : currentTime;
            var remaining = duration is { } total ? total - start : windowSize;
            if (remaining <= TimeSpan.Zero)
                return;

            var length = remaining < windowSize ? remaining : windowSize;
            if (length < TimeSpan.FromSeconds(1))
                return;

            _workCts?.Dispose();
            _workCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);
            var token = _workCts.Token;
            var variant = _variant;
            var mediaKey = _mediaKey;
            var mode = _options.Mode;

            _workTask = Task.Run(async () =>
            {
                try
                {
                    var audio = await _audioDecoder.DecodeAsync(variant, start, length, token).ConfigureAwait(false);
                    if (!string.Equals(mediaKey, _mediaKey, StringComparison.Ordinal))
                        return;

                    await _pipeline.SubmitAudioAsync(audio, token).ConfigureAwait(false);
                    await _pipeline.PrepareTranslationsAsync(mode, audio.MediaStart, audio.MediaEnd, token)
                        .ConfigureAwait(false);

                    if (string.Equals(mediaKey, _mediaKey, StringComparison.Ordinal))
                        _coveredUntil = audio.MediaEnd;
                }
                catch (OperationCanceledException)
                {
                    // Expected on seek/media/session change.
                }
                catch
                {
                    // Keep playback intact. Translation/ASR failures fall back to no/original subtitles.
                }
            }, token);
        }
        finally
        {
            _scheduleGate.Release();
        }
    }

    private async Task ApplyStyleIfChangedAsync(CancellationToken cancellationToken)
    {
        var fingerprint = string.Join('|',
            _options.FontFamily,
            _options.FontSize,
            _options.Bold,
            _options.TextColor,
            _options.OutlineColor,
            _options.OutlineSize,
            _options.BackgroundColor,
            _options.BackgroundOpacity,
            _options.BottomOffsetPx,
            _options.MaxLines,
            _options.MaxWidthPercent);
        if (string.Equals(fingerprint, _styleFingerprint, StringComparison.Ordinal))
            return;
        await _bridge.ApplyStyleAsync(BuildStyle(), cancellationToken).ConfigureAwait(false);
        _styleFingerprint = fingerprint;
    }

    private async Task SetDisplayedAsync(string? text, CancellationToken cancellationToken)
    {
        text = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (string.Equals(text, _lastDisplayed, StringComparison.Ordinal))
            return;
        _lastDisplayed = text;
        await _bridge.SetSubtitleAsync(text, cancellationToken).ConfigureAwait(false);
    }

    private SubtitleStyleOptions BuildStyle() => new()
    {
        FontFamily = _options.FontFamily,
        FontSize = _options.FontSize,
        Bold = _options.Bold,
        TextColor = _options.TextColor,
        OutlineColor = _options.OutlineColor,
        OutlineSize = _options.OutlineSize,
        BackgroundColor = _options.BackgroundColor,
        BackgroundOpacity = _options.BackgroundOpacity,
        BottomOffsetPx = _options.BottomOffsetPx,
        MaxLines = _options.MaxLines,
        MaxWidthPercent = _options.MaxWidthPercent
    };

    private static bool HasTargetText(SubtitleSegment segment, SubtitleMode mode) =>
        mode == SubtitleMode.Chinese
            ? !string.IsNullOrWhiteSpace(segment.ChineseText)
            : !string.IsNullOrWhiteSpace(segment.EnglishText);

    private static bool IsSameLanguage(string source, SubtitleMode mode)
    {
        source = source?.Trim().ToLowerInvariant() ?? string.Empty;
        return mode == SubtitleMode.Chinese
            ? source is "zh" or "zh-cn" or "chinese"
            : source is "en" or "en-us" or "en-gb" or "english";
    }

    private static MediaVariant? CreateDirectVariant(string mediaKey)
    {
        if (!Uri.TryCreate(mediaKey, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;

        return MediaVariant.FromCombinedTrack(
            "subtitle-direct",
            uri,
            RequestContext.CreateEmpty());
    }

    public async ValueTask DisposeAsync()
    {
        _bridge.PlaybackStateChanged -= OnPlaybackStateChanged;
        _lifetimeCts.Cancel();
        CancelActiveWindow();
        if (_workTask is not null)
        {
            try { await _workTask.ConfigureAwait(false); } catch { }
        }
        await _pipeline.StopSessionAsync().ConfigureAwait(false);
        await _bridge.DisposeAsync().ConfigureAwait(false);
        _workCts?.Dispose();
        _lifetimeCts.Dispose();
        _scheduleGate.Dispose();
    }
}
