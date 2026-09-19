using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using VideoDownloader.Core.Subtitles;

namespace VideoDownloader.Infrastructure.Subtitles.Browser;

/// <summary>
/// Browser-side subtitle clock and overlay. It is intentionally independent from media detection.
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

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
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
    }

    public async Task SetSubtitleAsync(string? text, CancellationToken cancellationToken = default)
    {
        if (_core is null)
            return;
        var json = JsonSerializer.Serialize(text ?? string.Empty);
        await _core.ExecuteScriptAsync(
            $"window.__vdSubtitle&&window.__vdSubtitle.setText({json});")
            .WaitAsync(cancellationToken);
    }

    public async Task ClearSubtitleAsync(CancellationToken cancellationToken = default) =>
        await SetSubtitleAsync(string.Empty, cancellationToken);

    public async Task ApplyStyleAsync(
        SubtitleStyleOptions style,
        CancellationToken cancellationToken = default)
    {
        if (_core is null)
            return;
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
        await _core.ExecuteScriptAsync(
            $"window.__vdSubtitle&&window.__vdSubtitle.setStyle({json});")
            .WaitAsync(cancellationToken);
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

  const ensureOverlay = () => {
    let el = document.getElementById('vd-subtitle-overlay');
    if (el) return el;
    el = document.createElement('div');
    el.id = 'vd-subtitle-overlay';
    Object.assign(el.style, {
      position: 'fixed', left: '50%', transform: 'translateX(-50%)', bottom: '60px',
      zIndex: '2147483647', pointerEvents: 'none', textAlign: 'center',
      fontFamily: 'Microsoft YaHei, sans-serif', fontSize: '28px', fontWeight: '700',
      color: '#FFFFFF', backgroundColor: '#000000', opacity: '1', padding: '4px 10px',
      borderRadius: '4px', maxWidth: '85vw', whiteSpace: 'pre-wrap',
      lineHeight: '1.35', display: 'none', textShadow: '-1px -1px 0 #000,1px -1px 0 #000,-1px 1px 0 #000,1px 1px 0 #000'
    });
    (document.body || document.documentElement).appendChild(el);
    return el;
  };

  let style = {};
  const hexToRgba = (hex, alpha) => {
    const value = String(hex || '').trim();
    const m = /^#([0-9a-f]{6})$/i.exec(value);
    if (!m) return value || `rgba(0,0,0,${alpha})`;
    const n = parseInt(m[1], 16);
    return `rgba(${(n >> 16) & 255},${(n >> 8) & 255},${n & 255},${alpha})`;
  };
  const applyStyle = (s) => {
    style = Object.assign(style, s || {});
    const el = ensureOverlay();
    if (style.fontFamily) el.style.fontFamily = style.fontFamily;
    if (Number.isFinite(style.fontSize)) el.style.fontSize = style.fontSize + 'px';
    el.style.fontWeight = style.bold ? '700' : '400';
    if (style.textColor) el.style.color = style.textColor;
    if (Number.isFinite(style.bottomOffsetPx)) el.style.bottom = style.bottomOffsetPx + 'px';
    if (Number.isFinite(style.maxWidthPercent)) el.style.maxWidth = style.maxWidthPercent + 'vw';
    const opacity = Number.isFinite(style.backgroundOpacity) ? style.backgroundOpacity : .35;
    el.style.backgroundColor = hexToRgba(style.backgroundColor || '#000000', opacity);
    const outline = Number.isFinite(style.outlineSize) ? style.outlineSize : 2;
    const oc = style.outlineColor || '#000000';
    el.style.webkitTextStroke = outline > 0 ? outline + 'px ' + oc : '0 transparent';
  };

  window.__vdSubtitle = {
    setText(text) {
      const el = ensureOverlay();
      const value = String(text || '').trim();
      el.textContent = value;
      el.style.display = value ? 'block' : 'none';
    },
    setStyle: applyStyle
  };

  let lastSent = 0;
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

  const mediaKey = (m) => String(m.currentSrc || m.src || location.href || '');
  const tick = (now) => {
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
  requestAnimationFrame(tick);
})();
""";
}
