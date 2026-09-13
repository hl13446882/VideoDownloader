namespace VideoDownloader.Infrastructure.Browser;

/// <summary>Reports whether any media element is currently audible (playing, unmuted, volume &gt; 0).</summary>
internal static class TabAudibleWatchScript
{
    internal const string Install = """
(() => {
  if (window.__vdTabAudibleWatch) return;
  window.__vdTabAudibleWatch = true;
  let last = null;
  const post = () => {
    try {
      const nodes = document.querySelectorAll('video,audio');
      let audible = false;
      for (const m of nodes) {
        if (!m || m.paused || m.ended) continue;
        if (m.muted || !(m.volume > 0)) continue;
        // Ignore tiny readyState until data exists; still count if already playing.
        audible = true;
        break;
      }
      if (audible === last) return;
      last = audible;
      if (window.chrome && chrome.webview && chrome.webview.postMessage)
        chrome.webview.postMessage({ type: 'vd-tab-audible', playing: !!audible });
    } catch (_) {}
  };
  for (const ev of ['play','playing','pause','ended','volumechange','emptied','loadeddata'])
    document.addEventListener(ev, post, true);
  setInterval(post, 800);
  post();
})();
""";
}
