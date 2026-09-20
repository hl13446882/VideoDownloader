using Microsoft.Extensions.Logging;
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
    private static readonly TimeSpan WindowOverlap = TimeSpan.FromSeconds(1.5);
    private const string ModelMissingHint = "请先在设置中安装字幕模型";
    private const string NativeRuntimeHint = "字幕引擎组件缺失，请更新到最新版本";
    private const string FfmpegMissingHint = "未找到 FFmpeg，无法识别字幕";
    private const string SourceMissingHint = "无法定位本地成片文件，字幕未启动";

    private readonly WebViewSubtitleBridge _bridge;
    private readonly ISubtitlePipeline _pipeline;
    private readonly IMediaAudioDecoder _audioDecoder;
    private readonly LocalPlaybackMediaSourceResolver _localSourceResolver;
    private readonly SubtitleOptions _options;
    private readonly ILogger _logger;
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
    private bool _sourceMissingNotified;
    private int _playbackEpoch;

    public BrowserSubtitleRuntime(
        WebViewSubtitleBridge bridge,
        ISubtitlePipeline pipeline,
        IMediaAudioDecoder audioDecoder,
        LocalPlaybackMediaSourceResolver localSourceResolver,
        SubtitleOptions options,
        ILogger? logger = null)
    {
        _bridge = bridge;
        _pipeline = pipeline;
        _audioDecoder = audioDecoder;
        _localSourceResolver = localSourceResolver;
        _options = options;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _bridge.PlaybackStateChanged += OnPlaybackStateChanged;
        _bridge.EditSubtitleRequested += OnEditSubtitleRequested;
        _bridge.EditSubtitleCommitted += OnEditSubtitleCommitted;
        _bridge.EditSubtitleNavigateRequested += OnEditSubtitleNavigateRequested;
        _bridge.ScriptReady += OnScriptReady;
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

    private void OnScriptReady(object? sender, EventArgs e)
    {
        _ = HandleScriptReadyAsync(_lifetimeCts.Token);
    }

    private async Task HandleScriptReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Document reinject resets overlay CSS to script defaults; force-push user style + last cue.
            _styleFingerprint = null;
            await ApplyStyleIfChangedAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(_lastDisplayed))
                await _bridge.SetSubtitleAsync(_lastDisplayed, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Subtitle script-ready restyle skipped");
        }
    }

    private void OnEditSubtitleRequested(object? sender, SubtitleEditRequestEventArgs e)
    {
        _ = HandleEditRequestAsync(e, _lifetimeCts.Token);
    }

    private void OnEditSubtitleCommitted(object? sender, SubtitleEditCommitEventArgs e)
    {
        _ = HandleEditCommitAsync(e, _lifetimeCts.Token);
    }

    private void OnEditSubtitleNavigateRequested(object? sender, SubtitleEditNavigateEventArgs e)
    {
        _ = HandleEditNavigateAsync(e, _lifetimeCts.Token);
    }

    private async Task HandleEditRequestAsync(
        SubtitleEditRequestEventArgs e,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_sessionActive || _localSource is null)
                return;

            var mediaTime = e.CurrentTime - TimeSpan.FromMilliseconds(_options.SubtitleOffsetMs);
            if (mediaTime < TimeSpan.Zero)
                mediaTime = TimeSpan.Zero;

            var segment = _pipeline.GetCurrent(mediaTime, _options.Mode);
            if (segment is null || string.IsNullOrWhiteSpace(segment.OriginalText))
                return;

            // System hints are not editable subtitle rows.
            if (string.Equals(segment.OriginalText, ModelMissingHint, StringComparison.Ordinal) ||
                string.Equals(segment.OriginalText, NativeRuntimeHint, StringComparison.Ordinal) ||
                string.Equals(segment.OriginalText, FfmpegMissingHint, StringComparison.Ordinal) ||
                string.Equals(segment.OriginalText, SourceMissingHint, StringComparison.Ordinal))
                return;

            await OpenEditorForSegmentAsync(segment, segment.GetDisplayText(_options.Mode), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Edit UI is best-effort.
        }
    }

    private async Task HandleEditNavigateAsync(
        SubtitleEditNavigateEventArgs e,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_sessionActive || _localSource is null)
                return;

            var mediaTime = e.CurrentTime - TimeSpan.FromMilliseconds(_options.SubtitleOffsetMs);
            if (mediaTime < TimeSpan.Zero)
                mediaTime = TimeSpan.Zero;

            var adjacent = _pipeline.GetAdjacent(mediaTime, e.Direction);
            if (adjacent is null)
                return;

            var videoTime = adjacent.Start + TimeSpan.FromMilliseconds(_options.SubtitleOffsetMs);
            if (videoTime < TimeSpan.Zero)
                videoTime = TimeSpan.Zero;

            await _bridge.SeekAndPauseAsync(videoTime.TotalSeconds, cancellationToken).ConfigureAwait(false);
            _lastPlaybackTime = videoTime;

            var display = adjacent.GetDisplayText(_options.Mode);
            _lastDisplayed = string.IsNullOrWhiteSpace(display) ? null : display.Trim();
            await _bridge.SetSubtitleAsync(_lastDisplayed, cancellationToken).ConfigureAwait(false);
            await OpenEditorForSegmentAsync(adjacent, display, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Subtitle edit navigate failed");
        }
    }

    private async Task OpenEditorForSegmentAsync(
        SubtitleSegment segment,
        string? overlayText,
        CancellationToken cancellationToken)
    {
        var hasZh = !string.IsNullOrWhiteSpace(segment.ChineseText);
        var hasEn = !string.IsNullOrWhiteSpace(segment.EnglishText);
        var multilingual = hasZh || hasEn;
        var mediaAnchor = segment.Start + TimeSpan.FromMilliseconds(1);
        var videoTime = segment.Start + TimeSpan.FromMilliseconds(_options.SubtitleOffsetMs);
        if (videoTime < TimeSpan.Zero)
            videoTime = TimeSpan.Zero;

        var display = string.IsNullOrWhiteSpace(overlayText)
            ? segment.GetDisplayText(_options.Mode)
            : overlayText.Trim();

        await _bridge.OpenSubtitleEditorAsync(new SubtitleEditorOpenModel
        {
            OriginalText = segment.OriginalText,
            ChineseText = segment.ChineseText,
            EnglishText = segment.EnglishText,
            Multilingual = multilingual,
            CurrentTimeSeconds = videoTime.TotalSeconds,
            HasPrevious = _pipeline.GetAdjacent(mediaAnchor, -1) is not null,
            HasNext = _pipeline.GetAdjacent(mediaAnchor, 1) is not null,
            OverlayText = display
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleEditCommitAsync(
        SubtitleEditCommitEventArgs e,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_sessionActive || _localSource is null)
                return;

            var mediaTime = e.CurrentTime - TimeSpan.FromMilliseconds(_options.SubtitleOffsetMs);
            if (mediaTime < TimeSpan.Zero)
                mediaTime = TimeSpan.Zero;

            SubtitleSegment? updated;
            if (e.Multilingual)
            {
                updated = await _pipeline.ApplyManualEditAsync(
                    mediaTime,
                    e.OriginalText,
                    e.ChineseText,
                    e.EnglishText,
                    preserveExistingTranslations: false,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Single-line edit replaces recognition text; clear MT so missing sides can refill,
                // while the time range stays covered (Whisper still skipped).
                updated = await _pipeline.ApplyManualEditAsync(
                    mediaTime,
                    e.OriginalText,
                    chineseText: null,
                    englishText: null,
                    preserveExistingTranslations: false,
                    cancellationToken).ConfigureAwait(false);
            }

            if (updated is null)
                return;

            lock (_translationSync)
            {
                _translationRequests.Remove(updated.Id + ":" + SubtitleMode.Chinese);
                _translationRequests.Remove(updated.Id + ":" + SubtitleMode.English);
                _translationRequests.Remove(updated.Id + ":" + SubtitleMode.Bilingual);
            }

            var display = updated.GetDisplayText(_options.Mode);
            await SetDisplayedAsync(display, cancellationToken).ConfigureAwait(false);

            // Only request MT for sides still missing after the edit.
            RequestMissingTranslation(updated, _options.Mode, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Keep playback intact if a manual edit fails to persist.
        }
    }

    private void OnPlaybackStateChanged(object? sender, SubtitlePlaybackState state)
    {
        _ = HandlePlaybackAsync(state, _lifetimeCts.Token);
    }

    private async Task HandlePlaybackAsync(SubtitlePlaybackState state, CancellationToken cancellationToken)
    {
        var epoch = Interlocked.Increment(ref _playbackEpoch);
        try
        {
            if (!IsLocalPlayback(state.PageUrl))
            {
                await LeaveLocalPlaybackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            _logger.LogDebug(
                "Subtitle local playback pageUrl={PageUrl} t={Time:g} enabled={Enabled}",
                state.PageUrl,
                state.CurrentTime,
                _options.Enabled);

            await ApplyStyleIfChangedAsync(cancellationToken).ConfigureAwait(false);
            if (epoch != Volatile.Read(ref _playbackEpoch))
                return;

            if (!_options.Enabled)
            {
                if (_sessionActive || _localSource is not null || _lastDisplayed is not null)
                    _logger.LogDebug("Subtitle disabled in settings; clearing overlay");
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
                if (epoch != Volatile.Read(ref _playbackEpoch))
                    return;
            }

            if (_localSource is null)
            {
                if (!_sourceMissingNotified)
                {
                    _sourceMissingNotified = true;
                    _logger.LogWarning(
                        "Subtitle local source unresolved pageUrl={PageUrl} mediaKey={MediaKey}",
                        state.PageUrl,
                        state.MediaKey);
                    await SetDisplayedAsync(SourceMissingHint, cancellationToken).ConfigureAwait(false);
                }
                return;
            }

            _sourceMissingNotified = false;

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
            if (epoch != Volatile.Read(ref _playbackEpoch))
                return;
            await SetDisplayedAsync(display, cancellationToken).ConfigureAwait(false);

            // Local files are immediately seekable, so warm the first subtitle window even while
            // the HTML video is still paused/buffering. This keeps ASR/translation ahead of playback.
            if (state.Seeking || epoch != Volatile.Read(ref _playbackEpoch))
                return;

            var windowSize = TimeSpan.FromSeconds(Math.Clamp(_options.PreloadAheadSeconds, 10, 90));
            // Stay well ahead of the playhead so slow ASR does not leave silent gaps.
            var refillThreshold = TimeSpan.FromSeconds(Math.Clamp(windowSize.TotalSeconds / 2, 8, 30));
            var coveredUntil = _pipeline.GetCoveredUntil(state.CurrentTime);
            if (coveredUntil is null || state.CurrentTime + refillThreshold >= coveredUntil.Value)
                await EnsureWindowAsync(state.CurrentTime, state.Duration, windowSize, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Session switch / application shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Subtitle playback handler failed");
        }
    }

    private static bool IsLocalPlayback(string? pageUrl) =>
        Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri) && LocalLibraryHost.IsLocalPlayerUrl(uri);

    private async Task LeaveLocalPlaybackAsync(CancellationToken cancellationToken)
    {
        if (!_sessionActive && _localSource is null && _lastDisplayed is null)
            return;

        var leavingPage = _pageUrl;
        CancelActiveWindow();
        _workCts?.Dispose();
        _workCts = null;
        _workTask = null;
        _mediaKey = null;
        _pageUrl = null;
        _localSource = null;
        _lastPlaybackTime = null;
        _modelMissingNotified = false;
        _sourceMissingNotified = false;
        lock (_translationSync)
            _translationRequests.Clear();

        if (_sessionActive)
        {
            _logger.LogInformation("Subtitle leave local playback pageUrl={PageUrl}", leavingPage);
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
        _logger.LogInformation(
            "Subtitle session started job={JobId} file={File} identity={Identity}",
            _localSource.JobId,
            _localSource.FilePath,
            _localSource.CacheIdentity);
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
                if (!_modelMissingNotified)
                {
                    _logger.LogWarning(
                        "Subtitle Whisper model not installed path={Path}",
                        PathExpander.Expand(_options.WhisperModelPath));
                }
                _modelMissingNotified = true;
                await SetDisplayedAsync(ModelMissingHint, cancellationToken).ConfigureAwait(false);
                return;
            }

            _modelMissingNotified = false;

            var cursor = _pipeline.GetRecognitionCursor(currentTime);
            var start = cursor ?? (currentTime <= StartSnapThreshold ? TimeSpan.Zero : currentTime);
            // Overlap successive windows so words straddling chunk boundaries are not dropped.
            if (cursor is not null && start > WindowOverlap)
                start -= WindowOverlap;

            var desiredWindow = cursor is null && windowSize > FastWarmupWindow
                ? FastWarmupWindow
                : windowSize;
            var remaining = duration is { } total ? total - start : desiredWindow;
            if (remaining <= TimeSpan.Zero)
                return;

            var length = remaining < desiredWindow ? remaining : desiredWindow;
            if (length < TimeSpan.FromSeconds(1))
                return;

            _logger.LogInformation(
                "Subtitle window scheduled job={JobId} start={Start:g} length={Length:g} cursor={Cursor}",
                localSource.JobId,
                start,
                length,
                cursor?.ToString("g") ?? "(none)");

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
                catch (FileNotFoundException ex)
                {
                    var hint = ClassifyMissingDependency(ex);
                    _modelMissingNotified = string.Equals(hint, ModelMissingHint, StringComparison.Ordinal);
                    _logger.LogWarning(ex, "Subtitle dependency missing hint={Hint}", hint);
                    try
                    {
                        await SetDisplayedAsync(hint, token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Overlay update is best-effort.
                    }
                }
                catch (Exception ex) when (IsNativeLibraryFailure(ex))
                {
                    _logger.LogWarning(ex, "Subtitle native runtime missing");
                    try
                    {
                        await SetDisplayedAsync(NativeRuntimeHint, token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Overlay update is best-effort.
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Subtitle ASR/translation window failed");
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
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;
            // Ignore truncated / failed downloads (full ggml-base.bin is ~141 MiB).
            return new FileInfo(path).Length > 100L * 1024 * 1024;
        }
        catch
        {
            return false;
        }
    }

    private static string ClassifyMissingDependency(FileNotFoundException ex)
    {
        var message = ex.Message ?? string.Empty;
        var name = ex.FileName ?? string.Empty;
        if (message.Contains("Native Library", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Whisper.net.Runtime", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ggml", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("whisper", StringComparison.OrdinalIgnoreCase))
            return NativeRuntimeHint;

        if (message.Contains("FFmpeg", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase))
            return FfmpegMissingHint;

        if (message.Contains("Whisper model", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            return ModelMissingHint;

        return ModelMissingHint;
    }

    private static bool IsNativeLibraryFailure(Exception ex)
    {
        var text = ex.ToString();
        return text.Contains("Native Library not found", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Whisper.net.Runtime", StringComparison.OrdinalIgnoreCase);
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
        _bridge.EditSubtitleRequested -= OnEditSubtitleRequested;
        _bridge.EditSubtitleCommitted -= OnEditSubtitleCommitted;
        _bridge.EditSubtitleNavigateRequested -= OnEditSubtitleNavigateRequested;
        _bridge.ScriptReady -= OnScriptReady;
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
