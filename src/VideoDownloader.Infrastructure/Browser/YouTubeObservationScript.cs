namespace VideoDownloader.Infrastructure.Browser;

internal static class YouTubeObservationScript
{
    internal const string Body = YouTubeLivePlaybackScript.Definition + "\n" + """
          const visible = el => {
            const r = el.getBoundingClientRect();
            return Math.max(0, Math.min(r.bottom, innerHeight) - Math.max(r.top, 0)) *
                   Math.max(0, Math.min(r.right, innerWidth) - Math.max(r.left, 0));
          };
          window.__vdObserve = () => {
            const v = (location.search.match(/[?&]v=([^&]+)/)||[])[1]
              || (location.pathname.match(/\/shorts\/([^/?#]+)/)||[])[1]
              || (location.hostname.includes('youtu.be') ? location.pathname.replace(/^\//,'') : null);
            if(!v) return null;
            const active=[...document.querySelectorAll('video')].filter(e=>visible(e)>0).sort((a,b)=>visible(b)-visible(a))[0];
            const caption=(document.querySelector('h1.ytd-watch-metadata yt-formatted-string,h1.title')?.textContent
              || document.title || '').replace(/\s*-\s*YouTube\s*$/i,'').trim();
            const author=(document.querySelector('#channel-name a,#upload-info ytd-channel-name a,#owner #channel-name')?.textContent
              || document.querySelector('ytd-channel-name a')?.textContent || '').trim();
            const media=[];
            const combinedMedia=[];
            if(active){ for(const u of [active.currentSrc,active.src]) if(/^https?:/i.test(u||'')) media.push(u); }
            // A blob: video element hides MSE addresses. Read only the active player's
            // response; playlist preload responses must never belong to this video.
            let response;
            try { response=document.getElementById('movie_player')?.getPlayerResponse?.(); } catch {}
            if(response?.videoDetails?.videoId!==v) response=window.ytInitialPlayerResponse;
            if(response?.videoDetails?.videoId===v){
              const data=response.streamingData;
              for(const format of [...(data?.formats||[]),...(data?.adaptiveFormats||[])]){
                if(/^https?:/i.test(format.url||'')) {
                  media.push(format.url);
                  if((format.mimeType||'').startsWith('video/') &&
                     (format.audioQuality || format.audioChannels>0 || /mp4a|opus|vorbis/i.test(format.mimeType||'')))
                    combinedMedia.push(format.url);
                }
              }
            }
            const durationSec=(active&&Number.isFinite(active.duration)&&active.duration>0)?active.duration:null;
            return { type:'vd-video-identity', identity:location.host+':content:youtube:'+v, caption, author, href:location.href, media:[...new Set(media)], combinedMedia:[...new Set(combinedMedia)], durationSec, isLive:__vdYouTubeIsLive() };
          };
          window.__vdProbe=()=>window.__vdObserve();
          let scheduled=false;
          const scan=()=>{ if(scheduled) return; scheduled=true; queueMicrotask(()=>{ scheduled=false; const r=window.__vdObserve(); if(r){ try{ chrome.webview.postMessage(r);}catch{}} }); };
          new MutationObserver(scan).observe(document,{childList:true,subtree:true});
          setInterval(scan,2000); scan();
        """;
}
