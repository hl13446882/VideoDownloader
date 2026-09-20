using System.Text.Json;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger _logger;
    private CoreWebView2? _core;
    private bool _initialized;
    private DateTimeOffset _lastPlaybackLog = DateTimeOffset.MinValue;

    public WebViewSubtitleBridge(WebView2 webView, ILogger? logger = null)
    {
        _webView = webView;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public event EventHandler<SubtitlePlaybackState>? PlaybackStateChanged;
    public event EventHandler<SubtitleEditRequestEventArgs>? EditSubtitleRequested;
    public event EventHandler<SubtitleEditCommitEventArgs>? EditSubtitleCommitted;

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        RunOnUiAsync(async () =>
        {
            if (_initialized)
                return;
            if (_webView.CoreWebView2 is null)
                throw new InvalidOperationException("WebView2 must be initialized before the subtitle bridge.");

            _core = _webView.CoreWebView2;
            _core.WebMessageReceived += OnWebMessageReceived;
            _core.NavigationCompleted += OnNavigationCompleted;
            await _core.AddScriptToExecuteOnDocumentCreatedAsync(InstallScript).WaitAsync(cancellationToken);
            await InjectScriptAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
            _logger.LogInformation("Subtitle bridge initialized (script ver={Version})", InstallScriptVersion);
        });

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
            return;
        _ = RunOnUiAsync(async () =>
        {
            try
            {
                await InjectScriptAsync(CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation(
                    "Subtitle script reinjected after navigation uri={Uri}",
                    _core?.Source ?? "(null)");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Subtitle script reinject failed");
            }
        });
    }

    private async Task InjectScriptAsync(CancellationToken cancellationToken)
    {
        if (_core is null)
            return;
        await _core.ExecuteScriptAsync(InstallScript).WaitAsync(cancellationToken);
    }

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

    public Task OpenSubtitleEditorAsync(
        SubtitleEditorOpenModel model,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(new
        {
            original = model.OriginalText ?? string.Empty,
            chinese = model.ChineseText ?? string.Empty,
            english = model.EnglishText ?? string.Empty,
            multilingual = model.Multilingual,
            currentTime = model.CurrentTimeSeconds
        });
        return RunOnUiAsync(async () =>
        {
            if (_core is null)
                return;
            await _core.ExecuteScriptAsync(
                    $"window.__vdSubtitle&&window.__vdSubtitle.openEditor({json});")
                .WaitAsync(cancellationToken);
        });
    }

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
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Subtitle WebMessage parse skipped");
        }
    }

    private void HandleMessage(JsonElement root)
    {
        if (!root.TryGetProperty("type", out var typeEl))
            return;
        var type = typeEl.GetString();
        if (type == "vd-subtitle-boot")
        {
            var ver = root.TryGetProperty("ver", out var verEl) && verEl.TryGetInt32(out var v) ? v : -1;
            var href = root.TryGetProperty("href", out var hrefEl) ? hrefEl.GetString() : null;
            _logger.LogInformation("Subtitle script boot ver={Ver} href={Href}", ver, href);
            return;
        }

        if (type == "vd-edit-subtitle-request")
        {
            var t = GetFiniteDouble(root, "currentTime") ?? 0;
            EditSubtitleRequested?.Invoke(this, new SubtitleEditRequestEventArgs(
                TimeSpan.FromSeconds(Math.Max(0, t))));
            return;
        }

        if (type == "vd-edit-subtitle-commit")
        {
            var t = GetFiniteDouble(root, "currentTime") ?? 0;
            var original = root.TryGetProperty("original", out var o) ? o.GetString() ?? string.Empty : string.Empty;
            var chinese = root.TryGetProperty("chinese", out var c) ? c.GetString() : null;
            var english = root.TryGetProperty("english", out var e) ? e.GetString() : null;
            var multilingual = root.TryGetProperty("multilingual", out var m) &&
                               m.ValueKind is JsonValueKind.True;
            EditSubtitleCommitted?.Invoke(this, new SubtitleEditCommitEventArgs(
                TimeSpan.FromSeconds(Math.Max(0, t)),
                original,
                chinese,
                english,
                multilingual));
            return;
        }

        if (type != "vd-subtitle-playback")
            return;

        var current = GetFiniteDouble(root, "currentTime") ?? 0;
        var duration = GetFiniteDouble(root, "duration");
        var rate = GetFiniteDouble(root, "playbackRate") ?? 1;
        var paused = root.TryGetProperty("paused", out var pausedEl) && pausedEl.ValueKind == JsonValueKind.True;
        var seeking = root.TryGetProperty("seeking", out var seekingEl) && seekingEl.ValueKind == JsonValueKind.True;
        var mediaKey = root.TryGetProperty("mediaKey", out var keyEl) ? keyEl.GetString() : null;
        var pageUrl = root.TryGetProperty("pageUrl", out var pageEl) ? pageEl.GetString() : null;

        var now = DateTimeOffset.UtcNow;
        if (now - _lastPlaybackLog >= TimeSpan.FromSeconds(2))
        {
            _lastPlaybackLog = now;
            _logger.LogInformation(
                "Subtitle playback tick pageUrl={PageUrl} t={Time:0.0}s paused={Paused} mediaKey={MediaKey}",
                pageUrl,
                current,
                paused,
                string.IsNullOrWhiteSpace(mediaKey) ? "(empty)" : (mediaKey.Length > 80 ? mediaKey[..80] + "…" : mediaKey));
        }

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
        {
            _core.WebMessageReceived -= OnWebMessageReceived;
            _core.NavigationCompleted -= OnNavigationCompleted;
        }
        _core = null;
        _initialized = false;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Bump when the injected player script changes. WebView2 accumulates
    /// AddScriptToExecuteOnDocumentCreated handlers across app launches; an old
    /// script that only checks <c>window.__vdSubtitle</c> would permanently block upgrades.
    /// </summary>
    private const int InstallScriptVersion = 5;

    private static string InstallScript => $$"""
(() => {
  const VER = {{InstallScriptVersion}};
  if (window.__vdSubtitleVersion === VER && window.__vdSubtitle) return;

  // Tear down a stale overlay / editor from an older injected script revision.
  try {
    const oldOverlay = document.getElementById('vd-subtitle-overlay');
    if (oldOverlay) oldOverlay.remove();
    const oldMask = document.getElementById('vd-subtitle-edit-mask');
    if (oldMask) oldMask.remove();
  } catch {}

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
      textShadow: '-1px -1px 0 #000,1px -1px 0 #000,-1px 1px 0 #000,1px 1px 0 #000',
      cursor: 'default'
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
    // Keep the cue inside the painted video frame (large bottomOffset must not push it off-screen).
    const clampPad = 8;
    const maxOffset = Math.max(0, picture.height - clampPad - 24);
    const clampedOffset = Math.min(Math.max(0, offset), maxOffset);
    const bottom = Math.max(0, window.innerHeight - picture.bottom + clampedOffset);
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

  let editing = false;
  let editCurrentTime = 0;

  const closeEditor = () => {
    editing = false;
    const mask = document.getElementById('vd-subtitle-edit-mask');
    if (mask) mask.style.display = 'none';
  };

  const ensureEditor = () => {
    let mask = document.getElementById('vd-subtitle-edit-mask');
    if (mask) return mask;
    mask = document.createElement('div');
    mask.id = 'vd-subtitle-edit-mask';
    Object.assign(mask.style, {
      position: 'fixed', inset: '0', zIndex: '2147483647', display: 'none',
      background: 'rgba(0,0,0,0.45)', alignItems: 'center', justifyContent: 'center'
    });
    const panel = document.createElement('div');
    panel.id = 'vd-subtitle-edit-panel';
    Object.assign(panel.style, {
      width: 'min(520px, 92vw)', background: '#111827', color: '#f9fafb',
      border: '1px solid #4b5563', borderRadius: '10px', padding: '16px 16px 12px',
      boxShadow: '0 16px 48px rgba(0,0,0,.5)', fontFamily: 'Microsoft YaHei, sans-serif'
    });
    const title = document.createElement('div');
    title.id = 'vd-subtitle-edit-title';
    title.textContent = '编辑字幕';
    Object.assign(title.style, { fontSize: '15px', fontWeight: '600', marginBottom: '12px' });
    const fields = document.createElement('div');
    fields.id = 'vd-subtitle-edit-fields';
    const actions = document.createElement('div');
    Object.assign(actions.style, { display: 'flex', justifyContent: 'flex-end', gap: '8px', marginTop: '12px' });
    const cancel = document.createElement('button');
    cancel.type = 'button';
    cancel.textContent = '取消';
    Object.assign(cancel.style, {
      padding: '8px 14px', borderRadius: '6px', border: '1px solid #4b5563',
      background: 'transparent', color: '#e5e7eb', cursor: 'pointer'
    });
    const ok = document.createElement('button');
    ok.type = 'button';
    ok.textContent = '确定';
    Object.assign(ok.style, {
      padding: '8px 14px', borderRadius: '6px', border: 'none',
      background: '#2563eb', color: '#fff', cursor: 'pointer'
    });
    cancel.onclick = (ev) => { ev.preventDefault(); closeEditor(); };
    ok.onclick = (ev) => {
      ev.preventDefault();
      const multilingual = mask.dataset.multilingual === '1';
      const originalEl = document.getElementById('vd-edit-original');
      const chineseEl = document.getElementById('vd-edit-chinese');
      const englishEl = document.getElementById('vd-edit-english');
      const original = originalEl ? String(originalEl.value || '').trim() : '';
      if (!original) return;
      const chinese = chineseEl ? String(chineseEl.value || '').trim() : '';
      const english = englishEl ? String(englishEl.value || '').trim() : '';
      try {
        chrome.webview.postMessage({
          type: 'vd-edit-subtitle-commit',
          currentTime: editCurrentTime,
          original,
          chinese: multilingual ? chinese : null,
          english: multilingual ? english : null,
          multilingual
        });
      } catch {}
      closeEditor();
    };
    actions.appendChild(cancel);
    actions.appendChild(ok);
    panel.appendChild(title);
    panel.appendChild(fields);
    panel.appendChild(actions);
    mask.appendChild(panel);
    mask.addEventListener('mousedown', (ev) => {
      if (ev.target === mask) closeEditor();
    });
    (document.body || document.documentElement).appendChild(mask);
    return mask;
  };

  const fieldStyle = {
    width: '100%', boxSizing: 'border-box', marginTop: '6px', marginBottom: '10px',
    padding: '8px 10px', borderRadius: '6px', border: '1px solid #4b5563',
    background: '#0f172a', color: '#f9fafb', fontFamily: 'inherit', fontSize: '14px'
  };

  const makeLabeledInput = (id, label, value, multiline) => {
    const wrap = document.createElement('div');
    const lab = document.createElement('label');
    lab.textContent = label;
    lab.setAttribute('for', id);
    Object.assign(lab.style, { fontSize: '12px', color: '#9ca3af', display: 'block' });
    const input = multiline ? document.createElement('textarea') : document.createElement('input');
    input.id = id;
    if (!multiline) input.type = 'text';
    else { input.rows = 2; input.style.resize = 'vertical'; }
    input.value = value || '';
    Object.assign(input.style, fieldStyle);
    wrap.appendChild(lab);
    wrap.appendChild(input);
    return wrap;
  };

  const openEditor = (payload) => {
    const mask = ensureEditor();
    const fields = document.getElementById('vd-subtitle-edit-fields');
    const title = document.getElementById('vd-subtitle-edit-title');
    if (!fields || !title) return;
    fields.innerHTML = '';
    const multilingual = !!payload.multilingual;
    editCurrentTime = Number(payload.currentTime) || 0;
    mask.dataset.multilingual = multilingual ? '1' : '0';
    title.textContent = multilingual ? '编辑字幕（原文 / 中文 / 英文）' : '编辑字幕';
    if (multilingual) {
      fields.appendChild(makeLabeledInput('vd-edit-original', '原文', payload.original || '', true));
      fields.appendChild(makeLabeledInput('vd-edit-chinese', '中文', payload.chinese || '', true));
      fields.appendChild(makeLabeledInput('vd-edit-english', '英文', payload.english || '', true));
    } else {
      fields.appendChild(makeLabeledInput('vd-edit-original', '字幕', payload.original || '', false));
    }
    editing = true;
    mask.style.display = 'flex';
    const focusEl = document.getElementById('vd-edit-original');
    if (focusEl) {
      focusEl.focus();
      if (focusEl.select) focusEl.select();
    }
  };

  window.__vdSubtitleVersion = VER;
  window.__vdSubtitle = {
    setText(text) {
      if (editing) return;
      const el = ensureOverlay();
      const value = String(text || '').trim();
      lastText = value;
      el.textContent = value;
      el.style.display = value ? 'block' : 'none';
      el.style.pointerEvents = value ? 'auto' : 'none';
      el.style.cursor = value ? 'context-menu' : 'default';
      if (value) layoutOverlay();
    },
    setStyle: applyStyle,
    openEditor
  };

  // Enable hit-testing on the subtitle so right-click can open the editor.
  const bindOverlayEdit = () => {
    const el = ensureOverlay();
    if (el.dataset.editBound === '1') return;
    el.dataset.editBound = '1';
    el.addEventListener('contextmenu', (ev) => {
      try {
        const path = String(location.pathname || '');
        if (!path.startsWith('/play/')) return;
        if (!lastText || editing) return;
        ev.preventDefault();
        ev.stopPropagation();
        const m = chooseMedia();
        const t = m ? Number(m.currentTime || 0) : 0;
        if (m) { try { m.pause(); } catch {} }
        try {
          chrome.webview.postMessage({
            type: 'vd-edit-subtitle-request',
            currentTime: t,
            pageUrl: String(location.href || '')
          });
        } catch {}
      } catch {}
    });
  };
  bindOverlayEdit();

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
  };
  window.addEventListener('resize', layoutOverlay, { passive: true });
  const pump = (now) => {
    if (window.__vdSubtitleAlive !== VER) return;
    tick(now);
    requestAnimationFrame(pump);
  };
  window.__vdSubtitleAlive = VER;
  requestAnimationFrame(pump);
  try {
    chrome.webview.postMessage({
      type: 'vd-subtitle-boot',
      ver: VER,
      href: String(location.href || '')
    });
  } catch {}
})();
""";
}
