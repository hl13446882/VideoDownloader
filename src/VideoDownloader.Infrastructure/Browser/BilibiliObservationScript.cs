namespace VideoDownloader.Infrastructure.Browser;

internal static class BilibiliObservationScript
{
    internal const string Body = """
          window.__vdObserve = () => {
            const bvid=(location.pathname.match(/\/video\/(BV[\w]+)/i)||[])[1]
              || document.querySelector('[data-bvid]')?.getAttribute('data-bvid');
            if(!bvid) return null;
            let caption=(document.querySelector('h1.video-title,.video-info-title h1,h1[title]')?.getAttribute('title')
              || document.querySelector('h1.video-title,.video-info-title h1')?.textContent
              || document.querySelector('meta[property="og:title"]')?.getAttribute('content')
              || document.title || '').trim();
            caption=caption.replace(/\s*[_|].*哔哩哔哩.*$/u,'').replace(/\s*[_-]\s*bilibili.*$/i,'').trim();
            const active=[...document.querySelectorAll('video')].find(e=>{const r=e.getBoundingClientRect();return r.width>80&&r.height>80;});
            const durationSec=(active&&Number.isFinite(active.duration)&&active.duration>0)?active.duration:null;
            return { type:'vd-video-identity', identity:location.host+':content:'+bvid, caption, href:location.href, media:[], durationSec };
          };
          window.__vdProbe=()=>{
            const observation=window.__vdObserve(); if(!observation) return null;
            try{
              const playinfo=window.__playinfo__?.data||window.__playinfo__||
                window.__INITIAL_STATE__?.videoData?.playInfo||window.__INITIAL_STATE__?.vp?.dash;
              const dash=playinfo?.dash||playinfo?.result?.dash||playinfo;
              const pushDash=list=>{
                if(!Array.isArray(list)) return;
                for(const item of list.slice(0,16)){
                  const add=u=>{
                    if(typeof u==='string' && /\.(m3u8|mpd|mp4|webm|m4a|mp3|m4s)([?#]|$)/i.test(u))
                      observation.media.push(u);
                  };
                  add(item?.baseUrl||item?.base_url);
                  const backups=item?.backupUrl||item?.backup_url;
                  if(Array.isArray(backups)) backups.forEach(add);
                  else add(backups);
                }
              };
              if(dash){ pushDash(dash.video); pushDash(dash.audio); }
            }catch{}
            observation.media=[...new Set(observation.media||[])];
            return observation;
          };
          let scheduled=false;
          const scan=()=>{ if(scheduled) return; scheduled=true; queueMicrotask(()=>{ scheduled=false; const r=window.__vdObserve(); if(r){ try{ chrome.webview.postMessage(r);}catch{}} }); };
          new MutationObserver(scan).observe(document,{childList:true,subtree:true});
          setInterval(scan,2000); scan();
        """;
}
