using VideoDownloader.Core.Subtitles;
using VideoDownloader.Core.Subtitles.Contracts;
using VideoDownloader.Infrastructure.Configuration;
using VideoDownloader.Infrastructure.LocalLibrary;

namespace VideoDownloader.Infrastructure.Subtitles.Browser;

/// <summary>
/// Subtitle runtime for the application's local-library player.
/// WebView2 is used only as the playback clock/overlay; speech recognition reads the completed
/// media file directly from disk. Ordinary web pages never start ASR or translation work.
/// </summary>
public sealed class BrowserSubtitleRuntime : IAsyncDisposable
{
    private static readonly TimeSpan FastWarmupWindow = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan StartSnapThreshold = TimeSpan.FromSeconds(2);
    private const string ModelMissingHint = "请先在设置中安装字幕模型";

    private readonly WebViewSubtitleBridge _bridge;
    private readonly ISubtitlePipeline _pipeline;
    private readonly IMediaAudioDecoder _audioDecoder;
    private readonly LocalPlaybackMediaSourceResolver _localSourceResolver;
    private readonly SubtitleOptions _options;
    private readonly SemaphoreSlim _scheduleGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly HashSet<string> _translationRequests = new(StringComparer.Ordinal);
    private readonly object _translationSync = new();
    private CancellationTokenSource? _workCts;
    private Task? _workTask;
    private string? _mediaKey;
    private string? _pageUrl;
    private LocalPlaybackMediaSource? _localSource;
    private TimeSpan? _lastPlaybackTime;
    private string? _lastDisplayed;
    private string? _styleFingerprint;
    private bool _sessionActive;
    private bool _modelMissingNotified;

    public BrowserSubtitleRuntime(
        WebViewSubtitleBridge bridge,
        ISubtitlePipeline pipeline,
        IMediaAudioDecoder audioDecoder,
        LocalPlaybackMediaSourceResolver localSourceResolver,
        SubtitleOptions options)
    {
        _bridge = bridge;
        _pipeline = pipeline;
        _audioDecoder = audioDecoder;
        _localSourceResolver = localSourceResolver;
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
            if (!IsLocalPlayback(state.PageUrl))
            {
                await LeaveLocalPlaybackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

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

            if (_localSource is null)
                return;

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

            var display = segment?.GetDisplayText(_options.Mode);
            if (string.IsNullOrWhiteSpace(display) && _modelMissingNotified)
                display = ModelMissingHint;
            await SetDisplayedAsync(display, cancellationToken).ConfigureAwait(false);

            // Local files are immediately seekable, so warm the first subtitle window even while
            // the HTML video is still paused/buffering. This keeps ASR/translation ahead of playback.
            if (state.Seeking)
                return;

            var windowSize = TimeSpan.FromSeconds(Math.Clamp(_options.PreloadAheadSeconds, 10, 90));
            var refillThreshold = TimeSpan.FromSeconds(Math.Max(5, windowSize.TotalSeconds / 3));
            var coveredUntil = _pipeline.GetCoveredUntil(state.CurrentTime);
            if (coveredUntil is null || state.CurrentTime + refillThreshold >= coveredUntil.Value)
                await EnsureWindowAsync(state.CurrentTime, state.Duration, windowSize, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Session switch / application shutdown.
        }
        catch
        {
            // Subtitle failures must never interrupt local video playback.
        }
    }

    private static bool IsLocalPlayback(string? pageUrl) =>
        Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri) && LocalLibraryHost.IsLocalPlayerUrl(uri);

    private async Task LeaveLocalPlaybackAsync(CancellationToken cancellationToken)
    {
        if (!_sessionActive && _localSource is null && _lastDisplayed is null)
            return;

        CancelActiveWindow();
        _workCts?.Dispose();
        _workCts = null;
        _workTask = null;
        _mediaKey = null;
        _pageUrl = null;
        _localSource = null;
        _lastPlaybackTime = null;
        _modelMissingNotified = false;
        lock (_translationSync)
            _translationRequests.Clear();

        if (_sessionActive)
        {
            await _pipeline.StopSessionAsync(cancellationToken).ConfigureAwait(false);
            _sessionActive = false;
        }
        await SetDisplayedAsync(null, cancellationToken).ConfigureAwait(false);
    }

    private void RequestMissingTranslation(
        SubtitleSegment segment,
        SubtitleMode mode,
        CancellationToken cancellationToken)
    {
        if (mode == SubtitleMode.Original || HasTargetText(segment, mode))
            return;

        if ((mode is SubtitleMode.Chinese or SubtitleMode.English) &&
            IsSameLanguage(segment.SourceLanguage, mode))
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
        _lastPlaybackTime = null;
        _modelMissingNotified = false;
        _mediaKey = mediaKey;
        _pageUrl = pageUrl;
        _localSource = await _localSourceResolver.ResolveAsync(pageUrl, cancellationToken).ConfigureAwait(false);
        _lastDisplayed = null;
        lock (_translationSync)
            _translationRequests.Clear();

        await _bridge.ClearSubtitleAsync(cancellationToken).ConfigureAwait(false);

        if (_sessionActive)
        {
            await _pipeline.StopSessionAsync(cancellationToken).ConfigureAwait(false);
            _sessionActive = false;
        }

        if (_localSource is null)
            return;

        await _pipeline.StartSessionAsync(
            "local:" + Guid.NewGuid().ToString("N"),
            _localSource.CacheIdentity,
            cancellationToken).ConfigureAwait(false);
        _sessionActive = true;
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
        var localSource = _localSource;
        if (localSource is null)
            return;

        await _scheduleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_workTask is { IsCompleted: false })
                return;

            if (!IsWhisperModelInstalled())
            {
                _modelMissingNotified = true;
                await SetDisplayedAsync(ModelMissingHint, cancellationToken).ConfigureAwait(false);
                return;
            }

            var coveredUntil = _pipeline.GetCoveredUntil(currentTime);
            var start = coveredUntil ?? (currentTime <= StartSnapThreshold ? TimeSpan.Zero : currentTime);
            var desiredWindow = coveredUntil is null && windowSize > FastWarmupWindow
                ? FastWarmupWindow
                : windowSize;
            var remaining = duration is { } total ? total - start : desiredWindow;
            if (remaining <= TimeSpan.Zero)
                return;

            var length = remaining < desiredWindow ? remaining : desiredWindow;
            if (length < TimeSpan.FromSeconds(1))
                return;

            _workCts?.Dispose();
            _workCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);
            var token = _workCts.Token;
            var mediaKey = _mediaKey;
            var cacheIdentity = localSource.CacheIdentity;
            var mode = _options.Mode;

            _workTask = Task.Run(async () =>
            {
                try
                {
                    var audio = await _audioDecoder.DecodeLocalFileAsync(
                        localSource.FilePath,
                        start,
                        length,
                        token).ConfigureAwait(false);

                    if (!string.Equals(mediaKey, _mediaKey, StringComparison.Ordinal) ||
                        !string.Equals(cacheIdentity, _localSource?.CacheIdentity, StringComparison.Ordinal))
                        return;

                    await _pipeline.SubmitAudioAsync(audio, token).ConfigureAwait(false);
                    await _pipeline.PrepareTranslationsAsync(mode, audio.MediaStart, audio.MediaEnd, token)
                        .ConfigureAwait(false);
                    _modelMissingNotified = false;
                }
                catch (OperationCanceledException)
                {
                    // Expected on seek/media/session change.
                }
                catch (FileNotFoundException)
                {
                    _modelMissingNotified = true;
                    try
                    {
                        await SetDisplayedAsync(ModelMissingHint, token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Overlay update is best-effort.
                    }
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

    private bool IsWhisperModelInstalled()
    {
        try
        {
            var path = PathExpander.Expand(_options.WhisperModelPath);
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
        catch
        {
            return false;
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
        MaxLines = Math.Max(_options.MaxLines, _options.Mode == SubtitleMode.Bilingual ? 2 : 1),
        MaxWidthPercent = _options.MaxWidthPercent
    };

    private static bool HasTargetText(SubtitleSegment segment, SubtitleMode mode) => mode switch
    {
        SubtitleMode.Chinese => !string.IsNullOrWhiteSpace(segment.ChineseText) ||
                                IsSameLanguage(segment.SourceLanguage, SubtitleMode.Chinese),
        SubtitleMode.English => !string.IsNullOrWhiteSpace(segment.EnglishText) ||
                                IsSameLanguage(segment.SourceLanguage, SubtitleMode.English),
        SubtitleMode.Bilingual =>
            (!string.IsNullOrWhiteSpace(segment.ChineseText) ||
             IsSameLanguage(segment.SourceLanguage, SubtitleMode.Chinese)) &&
            (!string.IsNullOrWhiteSpace(segment.EnglishText) ||
             IsSameLanguage(segment.SourceLanguage, SubtitleMode.English)),
        _ => true
    };

    private static bool IsSameLanguage(string source, SubtitleMode mode)
    {
        source = source?.Trim().ToLowerInvariant() ?? string.Empty;
        return mode == SubtitleMode.Chinese
            ? source is "zh" or "zh-cn" or "chinese"
            : source is "en" or "en-us" or "en-gb" or "english";
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
        if (_sessionActive)
            await _pipeline.StopSessionAsync().ConfigureAwait(false);
        await _bridge.DisposeAsync().ConfigureAwait(false);
        _workCts?.Dispose();
        _lifetimeCts.Dispose();
        _scheduleGate.Dispose();
    }
}
