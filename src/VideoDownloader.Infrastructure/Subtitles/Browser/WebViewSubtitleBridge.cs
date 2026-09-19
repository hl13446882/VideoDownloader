using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using VideoDownloader.Core.Subtitles;

namespace VideoDownloader.Infrastructure.Subtitles.Browser;

/// <summary>
/// Browser-side subtitle clock and overlay. It is intentionally independent from media detection.
/// All CoreWebView2 calls are marshalled to the WebView dispatcher.
/// </summary>
public sealed class WebViewSubtitleBridge : IAsyncDisposable
{
    private readonly WebView2 _webView;
    private CoreWebView2? _core;
    private bool _initialized;

    public WebViewSubtitleBridge(WebView2 webView)
    {
        _webView = webView;
    }

    public event EventHandler<SubtitlePlaybackState>? PlaybackStateChanged;

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        RunOnUiAsync(async () =>
        {
            if (_initialized)
                return;
            if (_webView.CoreWebView2 is null)
                throw new InvalidOperationException("WebView2 must be initialized before the subtitle bridge.");

            _core = _webView.CoreWebView2;
            _core.WebMessageReceived += OnWebMessageReceived;
            await _core.AddScriptToExecuteOnDocumentCreatedAsync(InstallScript).WaitAsync(cancellationToken);
            await _core.ExecuteScriptAsync(InstallScript).WaitAsync(cancellationToken);
            _initialized = true;
        });

    public Task SetSubtitleAsync(string? text, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(text ?? string.Empty);
        return RunOnUiAsync(async () =>
        {
            if (_core is null)
                return;
            await _core.ExecuteScriptAsync(
                $"window.__vdSubtitle&&window.__vdSubtitle.setText({json});")
                .WaitAsync(cancellationToken);
        });
    }

    public Task ClearSubtitleAsync(CancellationToken cancellationToken = default) =>
        SetSubtitleAsync(string.Empty, cancellationToken);

    public Task ApplyStyleAsync(
        SubtitleStyleOptions style,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(new
        {
            fontFamily = style.FontFamily,
            fontSize = Math.Clamp(style.FontSize, 10, 96),
            bold = style.Bold,
            textColor = style.TextColor,
            outlineColor = style.OutlineColor,
            outlineSize = Math.Clamp(style.OutlineSize, 0, 8),
            backgroundColor = style.BackgroundColor,
            backgroundOpacity = Math.Clamp(style.BackgroundOpacity, 0, 1),
            bottomOffsetPx = Math.Clamp(style.BottomOffsetPx, 0, 1000),
            maxLines = Math.Clamp(style.MaxLines, 1, 4),
            maxWidthPercent = Math.Clamp(style.MaxWidthPercent, 20, 100)
        });

        return RunOnUiAsync(async () =>
        {
            if (_core is null)
                return;
            await _core.ExecuteScriptAsync(
                $"window.__vdSubtitle&&window.__vdSubtitle.setStyle({json});")
                .WaitAsync(cancellationToken);
        });
    }

    private Task RunOnUiAsync(Func<Task> action)
    {
        if (_webView.Dispatcher.CheckAccess())
            return action();
        return _webView.Dispatcher.InvokeAsync(action).Task.Unwrap();
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                var inner = root.GetString();
                if (string.IsNullOrWhiteSpace(inner))
                    return;
                using var nested = JsonDocument.Parse(inner);
                HandleMessage(nested.RootElement);
                return;
            }
            HandleMessage(root);
        }
        catch
        {
            // Other WebView features also use WebMessageReceived; ignore unrelated payloads.
        }
    }

    private void HandleMessage(JsonElement root)
    {
        if (!root.TryGetProperty("type", out var type) ||
            type.GetString() != "vd-subtitle-playback")
            return;

        var current = GetFiniteDouble(root, "currentTime") ?? 0;
        var duration = GetFiniteDouble(root, "duration");
        var rate = GetFiniteDouble(root, "playbackRate") ?? 1;
        var paused = root.TryGetProperty("paused", out var pausedEl) && pausedEl.ValueKind == JsonValueKind.True;
        var seeking = root.TryGetProperty("seeking", out var seekingEl) && seekingEl.ValueKind == JsonValueKind.True;
        var mediaKey = root.TryGetProperty("mediaKey", out var keyEl) ? keyEl.GetString() : null;
        var pageUrl = root.TryGetProperty("pageUrl", out var pageEl) ? pageEl.GetString() : null;

        PlaybackStateChanged?.Invoke(this, new SubtitlePlaybackState(
            mediaKey,
            pageUrl,
            TimeSpan.FromSeconds(Math.Max(0, current)),
            duration is > 0 ? TimeSpan.FromSeconds(duration.Value) : null,
            paused,
            seeking,
            rate > 0 ? rate : 1));
    }

    private static double? GetFiniteDouble(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.Number ||
            !element.TryGetDouble(out var value) ||
            !double.IsFinite(value))
            return null;
        return value;
    }

    public ValueTask DisposeAsync()
    {
        if (_core is not null)
            _core.WebMessageReceived -= OnWebMessageReceived;
        _core = null;
        _initialized = false;
        return ValueTask.CompletedTask;
    }

    private const string InstallScript = """
(() => {
  if (window.__vdSubtitle) return;

  let style = {};
  let lastText = '';

  const hexToRgba = (hex, alpha) => {
    const value = String(hex || '').trim();
    const m = /^#([0-9a-f]{6})$/i.exec(value);
    if (!m) return value || `rgba(0,0,0,${alpha})`;
    const n = parseInt(m[1], 16);
    return `rgba(${(n >> 16) & 255},${(n >> 8) & 255},${n & 255},${alpha})`;
  };

  const chooseMedia = () => {
    const all = [...document.querySelectorAll('video,audio')];
    if (!all.length) return null;
    const playing = all.filter(x => !x.paused && !x.ended);
    const pool = playing.length ? playing : all;
    pool.sort((a,b) => {
      const ar = a.getBoundingClientRect(), br = b.getBoundingClientRect();
      return (br.width * br.height) - (ar.width * ar.height);
    });
    return pool[0] || null;
  };

  // Actual painted frame inside a video element (accounts for object-fit: contain letterboxing).
  const getPictureRect = (video) => {
    const rect = video.getBoundingClientRect();
    const vw = Number(video.videoWidth) || 0;
    const vh = Number(video.videoHeight) || 0;
    if (!vw || !vh || rect.width <= 0 || rect.height <= 0) {
      return { left: rect.left, top: rect.top, width: rect.width, height: rect.height,
        bottom: rect.bottom, right: rect.right };
    }
    const videoRatio = vw / vh;
    const boxRatio = rect.width / rect.height;
    let width, height, left, top;
    if (boxRatio > videoRatio) {
      height = rect.height;
      width = height * videoRatio;
      left = rect.left + (rect.width - width) / 2;
      top = rect.top;
    } else {
      width = rect.width;
      height = width / videoRatio;
      left = rect.left;
      top = rect.top + (rect.height - height) / 2;
    }
    return { left, top, width, height, bottom: top + height, right: left + width };
  };

  const ensureOverlay = () => {
    let el = document.getElementById('vd-subtitle-overlay');
    if (el) return el;
    el = document.createElement('div');
    el.id = 'vd-subtitle-overlay';
    Object.assign(el.style, {
      position: 'fixed', left: '50%', transform: 'translateX(-50%)', bottom: '60px',
      zIndex: '2147483647', pointerEvents: 'none', textAlign: 'center',
      fontFamily: 'Microsoft YaHei, sans-serif', fontSize: '28px', fontWeight: '700',
      color: '#FFFFFF', backgroundColor: 'rgba(0,0,0,0.35)', padding: '4px 10px',
      borderRadius: '4px', maxWidth: '85vw', whiteSpace: 'pre-wrap',
      lineHeight: '1.35', display: 'none', boxSizing: 'border-box',
      textShadow: '-1px -1px 0 #000,1px -1px 0 #000,-1px 1px 0 #000,1px 1px 0 #000'
    });
    (document.body || document.documentElement).appendChild(el);
    return el;
  };

  const layoutOverlay = () => {
    const el = ensureOverlay();
    const media = chooseMedia();
    if (!media || media.tagName !== 'VIDEO') {
      // Fallback: keep window-bottom placement only when no video picture exists.
      const offset = Number.isFinite(style.bottomOffsetPx) ? style.bottomOffsetPx : 60;
      el.style.left = '50%';
      el.style.transform = 'translateX(-50%)';
      el.style.bottom = offset + 'px';
      el.style.top = 'auto';
      el.style.width = 'auto';
      el.style.maxWidth = (Number.isFinite(style.maxWidthPercent) ? style.maxWidthPercent : 85) + 'vw';
      return;
    }

    const picture = getPictureRect(media);
    const offset = Number.isFinite(style.bottomOffsetPx) ? style.bottomOffsetPx : 60;
    const maxPct = Number.isFinite(style.maxWidthPercent) ? style.maxWidthPercent : 85;
    const maxWidth = Math.max(40, picture.width * maxPct / 100);
    // Distance is from the bottom edge of the video picture, not the browser window.
    const bottom = Math.max(0, window.innerHeight - picture.bottom + offset);
    el.style.left = (picture.left + picture.width / 2) + 'px';
    el.style.transform = 'translateX(-50%)';
    el.style.bottom = bottom + 'px';
    el.style.top = 'auto';
    el.style.width = 'auto';
    el.style.maxWidth = maxWidth + 'px';
  };

  const applyStyle = (s) => {
    style = Object.assign(style, s || {});
    const el = ensureOverlay();
    if (style.fontFamily) el.style.fontFamily = style.fontFamily;
    if (Number.isFinite(style.fontSize)) el.style.fontSize = style.fontSize + 'px';
    el.style.fontWeight = style.bold ? '700' : '400';
    if (style.textColor) el.style.color = style.textColor;
    const opacity = Number.isFinite(style.backgroundOpacity) ? style.backgroundOpacity : .35;
    el.style.backgroundColor = hexToRgba(style.backgroundColor || '#000000', opacity);
    const outline = Number.isFinite(style.outlineSize) ? style.outlineSize : 2;
    const oc = style.outlineColor || '#000000';
    el.style.webkitTextStroke = outline > 0 ? outline + 'px ' + oc : '0 transparent';
    layoutOverlay();
  };

  window.__vdSubtitle = {
    setText(text) {
      const el = ensureOverlay();
      const value = String(text || '').trim();
      lastText = value;
      el.textContent = value;
      el.style.display = value ? 'block' : 'none';
      if (value) layoutOverlay();
    },
    setStyle: applyStyle
  };

  let lastSent = 0;
  const mediaKey = (m) => String(m.currentSrc || m.src || location.href || '');
  const tick = (now) => {
    layoutOverlay();
    if (now - lastSent >= 150) {
      lastSent = now;
      const m = chooseMedia();
      if (m) {
        try {
          chrome.webview.postMessage({
            type: 'vd-subtitle-playback',
            mediaKey: mediaKey(m),
            pageUrl: String(location.href || ''),
            currentTime: Number(m.currentTime || 0),
            duration: Number.isFinite(m.duration) ? Number(m.duration) : null,
            paused: !!m.paused,
            seeking: !!m.seeking,
            playbackRate: Number(m.playbackRate || 1)
          });
        } catch {}
      }
    }
    requestAnimationFrame(tick);
  };
  window.addEventListener('resize', layoutOverlay, { passive: true });
  requestAnimationFrame(tick);
})();
""";
}
