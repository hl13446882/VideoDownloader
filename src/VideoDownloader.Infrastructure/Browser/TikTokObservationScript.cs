namespace VideoDownloader.Infrastructure.Browser;

/// <summary>TikTok-only page observation. Independent of Douyin album helpers.</summary>
internal static class TikTokObservationScript
{
    internal const string Body = """
          const visible = el => {
            const style=getComputedStyle(el);
            if(style.visibility==='hidden'||style.display==='none') return 0;
            const r = el.getBoundingClientRect();
            return Math.max(0, Math.min(r.bottom, innerHeight) - Math.max(r.top, 0)) *
                   Math.max(0, Math.min(r.right, innerWidth) - Math.max(r.left, 0));
          };
          const pageKey = url => {
            const u = new URL(url, location.href);
            const id = u.searchParams.get('item_id') || u.pathname.match(/\/video\/(\d{10,})/)?.[1];
            return u.origin + u.pathname + (id ? JSON.stringify([['id',id]]) : '[]');
          };
          const activePlayer=()=>{
            const preferred=document.querySelector('[data-e2e="feed-active-video"] video,[data-e2e="feed-active-video"] audio');
            const players=[...document.querySelectorAll('video,audio')].filter(e=>visible(e)>0);
            if(preferred && players.includes(preferred)) return preferred;
            players.sort((a,b)=>Number(!b.paused)-Number(!a.paused)||visible(b)-visible(a));
            return players[0];
          };
          const collectTikTokImages = record => {
            const urls=[];
            const push=v=>{
              if(typeof v==='string' && /^https?:/i.test(v) &&
                 (/\.(jpg|jpeg|png|webp)([?#]|$)/i.test(v) || /tiktokcdn/i.test(v)))
                urls.push(v);
              else if(v&&typeof v==='object'){
                for(const k of ['urlList','url_list','display_image','url']){
                  const child=v[k];
                  if(Array.isArray(child)){ const first=child.find(u=>typeof u==='string'); if(first) push(first); }
                  else if(typeof child==='string') push(child);
                }
              }
            };
            for(const key of ['images','imagePost','image_post_info','image_list']){
              const block=record?.[key];
              if(Array.isArray(block)) block.forEach(push);
              else if(block?.images) block.images.forEach(push);
            }
            return [...new Set(urls)];
          };
          const observeTikTokAlbum = () => {
            const id=(location.pathname.match(/\/video\/(\d{10,})/)||[])[1];
            if(!id) return null;
            const roots=[];
            const push=v=>{if(v&&typeof v==='object')roots.push(v);};
            try{ const el=document.getElementById('SIGI_STATE')||document.getElementById('__UNIVERSAL_DATA_FOR_REHYDRATION__'); if(el?.textContent) push(JSON.parse(el.textContent)); }catch{}
            for(const key of ['SIGI_STATE','__UNIVERSAL_DATA_FOR_REHYDRATION__','__NEXT_DATA__']){ try{push(window[key]);}catch{} }
            const seen=new WeakSet(); let budget=900;
            const find=(value,depth)=>{
              if(!value||typeof value!=='object'||value instanceof Node||seen.has(value)||depth>12||--budget<0) return null;
              seen.add(value);
              const hasImages=!!(value.images||value.imagePost||value.image_post_info||value.image_list);
              const vid=String(value.id||value.itemId||value.videoId||value.aweme_id||'');
              if(hasImages && vid===id) return value;
              if(Array.isArray(value)){ for(const c of value.slice(0,40)){const r=find(c,depth+1); if(r) return r;} }
              else { for(const c of Object.values(value)){const r=find(c,depth+1); if(r) return r;} }
              return null;
            };
            let record=null;
            for(const root of roots){ record=find(root,0); if(record) break; }
            if(!record) return null;
            const images=collectTikTokImages(record);
            if(images.length<1) return null;
            const music=record.music||{};
            const audio=[];
            const pushAudio=v=>{
              if(typeof v==='string' && /^https?:/i.test(v) && !/\.(jpg|jpeg|png|webp)([?#]|$)/i.test(v)) audio.push(v);
              else if(v&&typeof v==='object'){ for(const k of ['playUrl','play_url','urlList','url_list','url']){ const c=v[k]; if(Array.isArray(c)) c.forEach(pushAudio); else pushAudio(c);} }
            };
            pushAudio(music.playUrl||music.play_url||music);
            const caption=String(record.desc||record.description||record.title||'').trim();
            return { type:'vd-video-identity', identity:location.host+':content:'+id, caption, href:location.href,
              media:[...new Set(audio)], images, album:true };
          };
          window.__vdObserve = () => {
            const album = observeTikTokAlbum();
            if (album?.images?.length > 0) return album;
            const active = activePlayer();
            if (!active) return null;
            let explicit='', caption='';
            for (let scope=active,i=0; scope && i<8; i++, scope=scope.parentElement) {
              const value = scope.getAttribute('data-e2e-vid') || scope.getAttribute('data-video-id');
              if (value && !explicit) explicit = 'content:' + value;
              const wrap = (scope.id||'').match(/^xgwrapper-\d+-(\d{10,})$/);
              if(wrap && !explicit) explicit = 'content:'+wrap[1];
              const desc = scope.querySelector('[data-e2e="browse-video-desc"],[data-e2e="video-desc"],[data-e2e="new-desc-span"]');
              if (desc && !caption) caption = (desc.textContent||'').trim();
            }
            const pathId=(location.pathname.match(/\/video\/(\d{10,})/)||[])[1];
            if(pathId && !explicit) explicit='content:'+pathId;
            if(!explicit){
              const wrapId=(document.querySelector('[id^="xgwrapper-"]')?.id||'').match(/xgwrapper-\d+-(\d{10,})/)?.[1];
              if(wrapId) explicit='content:'+wrapId;
            }
            if(!explicit) return null;
            return { type:'vd-video-identity', identity: location.host+':'+explicit, caption, href:location.href,
              media:[...new Set([active.currentSrc,active.src].filter(u=>/^https?:/i.test(u||'')))] };
          };
          window.__vdProbe=()=>{
            const observation=window.__vdObserve(); if(!observation) return null;
            if(!observation.images?.length){
              const album=observeTikTokAlbum();
              if(album?.images?.length){ observation.images=album.images; observation.album=true;
                for(const u of album.media||[]) observation.media.push(u); }
            }
            observation.media=[...new Set(observation.media||[])];
            return observation;
          };
          let scheduled=false;
          const scan=()=>{ if(scheduled) return; scheduled=true; queueMicrotask(()=>{ scheduled=false; const r=window.__vdObserve(); if(r){ try{ chrome.webview.postMessage(r);}catch{}} }); };
          new MutationObserver(scan).observe(document,{childList:true,subtree:true,attributes:true});
          document.addEventListener('playing',scan,true);
          setInterval(scan,1500); scan();
        """;
}
