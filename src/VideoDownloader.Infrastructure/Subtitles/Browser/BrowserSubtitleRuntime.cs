using VideoDownloader.Core.Models;
using VideoDownloader.Core.Subtitles;
using VideoDownloader.Core.Subtitles.Contracts;

namespace VideoDownloader.Infrastructure.Subtitles.Browser;

/// <summary>
/// Per-WebView subtitle runtime. The first integration path handles direct http(s) media URLs.
/// MSE/blob playback is intentionally left for the detected MediaVariant integration path.
/// </summary>
public sealed class BrowserSubtitleRuntime : IAsyncDisposable
{
    private static readonly TimeSpan WindowSize = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RefillThreshold = TimeSpan.FromSeconds(12);

    private readonly WebViewSubtitleBridge _bridge;
    private readonly ISubtitlePipeline _pipeline;
    private readonly IMediaAudioDecoder _audioDecoder;
    private readonly SemaphoreSlim _scheduleGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private CancellationTokenSource? _workCts;
    private Task? _workTask;
    private string? _mediaKey;
    private MediaVariant? _variant;
    private TimeSpan _coveredUntil;
    private SubtitleMode _mode = SubtitleMode.Chinese;
    private string? _lastDisplayed;

    public BrowserSubtitleRuntime(
        WebViewSubtitleBridge bridge,
        ISubtitlePipeline pipeline,
        IMediaAudioDecoder audioDecoder)
    {
        _bridge = bridge;
        _pipeline = pipeline;
        _audioDecoder = audioDecoder;
        _bridge.PlaybackStateChanged += OnPlaybackStateChanged;
    }

    public SubtitleMode Mode
    {
        get => _mode;
        set => _mode = value;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _bridge.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _bridge.ApplyStyleAsync(new SubtitleStyleOptions(), cancellationToken).ConfigureAwait(false);
    }

    private void OnPlaybackStateChanged(object? sender, SubtitlePlaybackState state)
    {
        _ = HandlePlaybackAsync(state, _lifetimeCts.Token);
    }

    private async Task HandlePlaybackAsync(SubtitlePlaybackState state, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(state.MediaKey))
            {
                await SetDisplayedAsync(null, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!string.Equals(_mediaKey, state.MediaKey, StringComparison.Ordinal))
                await SwitchMediaAsync(state.MediaKey, cancellationToken).ConfigureAwait(false);

            var segment = _pipeline.GetCurrent(state.CurrentTime, _mode);
            await SetDisplayedAsync(segment?.GetDisplayText(_mode), cancellationToken).ConfigureAwait(false);

            if (_variant is null || state.Paused || state.Seeking)
                return;

            if (state.CurrentTime + RefillThreshold >= _coveredUntil)
                await EnsureWindowAsync(state.CurrentTime, state.Duration, cancellationToken).ConfigureAwait(false);
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

    private async Task SwitchMediaAsync(string mediaKey, CancellationToken cancellationToken)
    {
        _workCts?.Cancel();
        _workCts?.Dispose();
        _workCts = null;
        _workTask = null;
        _coveredUntil = TimeSpan.Zero;
        _mediaKey = mediaKey;
        _variant = CreateDirectVariant(mediaKey);
        _lastDisplayed = null;

        await _bridge.ClearSubtitleAsync(cancellationToken).ConfigureAwait(false);
        await _pipeline.StartSessionAsync(
            "webview:" + Guid.NewGuid().ToString("N"),
            mediaKey,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureWindowAsync(
        TimeSpan currentTime,
        TimeSpan? duration,
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
            var remaining = duration is { } total ? total - start : WindowSize;
            if (remaining <= TimeSpan.Zero)
                return;

            var length = remaining < WindowSize ? remaining : WindowSize;
            if (length < TimeSpan.FromSeconds(1))
                return;

            _workCts?.Dispose();
            _workCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);
            var token = _workCts.Token;
            var variant = _variant;
            var mediaKey = _mediaKey;
            var mode = _mode;

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
                    // Expected on media/session change.
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

    private async Task SetDisplayedAsync(string? text, CancellationToken cancellationToken)
    {
        text = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (string.Equals(text, _lastDisplayed, StringComparison.Ordinal))
            return;
        _lastDisplayed = text;
        await _bridge.SetSubtitleAsync(text, cancellationToken).ConfigureAwait(false);
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
        _workCts?.Cancel();
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
