namespace VideoDownloader.Infrastructure.Browser;

internal static class MediaAddressDiscoveryScript
{
    // Address-only observation: never supplies caption or content identity.
    internal const string Expression = """
        (() => {
          const urls = new Set();
          const add = (value, base) => {
            if (typeof value !== 'string' || !value.trim() || urls.size >= 64) return;
            try { const u = new URL(value, base); if (/^https?:$/.test(u.protocol)) urls.add(u.href); } catch {}
          };
          const visit = (value, base, depth = 0, key = '') => {
            if (depth > 16 || urls.size >= 64) return;
            if (typeof value === 'string' && /url|src|play|video|audio|stream|file/i.test(key) &&
                /\.(mp4|webm|m4a|mp3|m3u8|mpd|aac|ogg)(?:[?#]|$)/i.test(value)) add(value, base);
            else if (value && typeof value === 'object')
              for (const [k,v] of Object.entries(value).slice(0, 512)) visit(v, base, depth + 1, Array.isArray(value) ? key : k);
          };
          const scan = (doc, depth) => {
            if (depth > 4) return;
            for (const element of Array.from(doc.querySelectorAll('video,audio,video source,audio source')).slice(0,64))
              add(element.currentSrc || element.src || element.getAttribute('src'), doc.baseURI);
            for (const script of Array.from(doc.querySelectorAll('script[type="application/json"],script[type="application/ld+json"]')).slice(0,16)) {
              if (script.textContent.length > 2097152) continue;
              try { visit(JSON.parse(script.textContent), doc.baseURI); } catch {}
            }
            for (const frame of Array.from(doc.querySelectorAll('iframe,frame')).slice(0,16))
              try { if (frame.contentDocument) scan(frame.contentDocument, depth + 1); } catch {}
          };
          scan(document, 0);
          return Array.from(urls).map(url => ({url}));
        })()
        """;
}
