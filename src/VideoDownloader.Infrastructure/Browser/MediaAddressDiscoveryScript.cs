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
          const looksMedia = value =>
            /\.(mp4|webm|m4a|mp3|m3u8|mpd|aac|ogg|m4s|flv)(?:[?#]|$)/i.test(value) ||
            /\/(?:videoplayback|playurl|index\.m3u8)\b/i.test(value) ||
            /[?&](?:url|src|file|video|path)=https?:/i.test(value);
          const visit = (value, base, depth = 0, key = '') => {
            if (depth > 16 || urls.size >= 64) return;
            if (typeof value === 'string') {
              if (looksMedia(value) ||
                  (/url|src|play|video|audio|stream|file|source|link/i.test(key) && /^https?:\/\//i.test(value)))
                add(value, base);
              return;
            }
            if (value && typeof value === 'object')
              for (const [k,v] of Object.entries(value).slice(0, 512)) visit(v, base, depth + 1, Array.isArray(value) ? key : k);
          };
          const scanScripts = doc => {
            for (const script of Array.from(doc.querySelectorAll('script')).slice(0, 48)) {
              const text = script.textContent || '';
              if (text.length === 0 || text.length > 2097152) continue;
              // Bare m3u8 / mp4 URLs embedded in player bootstrap scripts (MacCMS etc.).
              for (const match of text.matchAll(/https?:\\?\/\\?\/[^\s"'<>\\]{8,512}\.(?:m3u8|mpd|mp4|m4a|m4s)(?:\?[^\s"'<>\\]*)?/ig)) {
                add(match[0].replace(/\\\//g, '/'), doc.baseURI);
              }
              if (script.type === 'application/json' || script.type === 'application/ld+json') {
                try { visit(JSON.parse(text), doc.baseURI); } catch {}
              }
            }
          };
          const scan = (doc, depth) => {
            if (depth > 4) return;
            for (const element of Array.from(doc.querySelectorAll('video,audio,video source,audio source')).slice(0,64))
              add(element.currentSrc || element.src || element.getAttribute('src'), doc.baseURI);
            scanScripts(doc);
            for (const frame of Array.from(doc.querySelectorAll('iframe,frame')).slice(0,16))
              try { if (frame.contentDocument) scan(frame.contentDocument, depth + 1); } catch {}
          };
          scan(document, 0);
          return Array.from(urls).map(url => ({url}));
        })()
        """;
}
