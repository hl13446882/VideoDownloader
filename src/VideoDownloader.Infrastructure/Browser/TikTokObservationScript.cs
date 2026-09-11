namespace VideoDownloader.Infrastructure.Browser;

/// <summary>TikTok-only page observation. Mirrors Douyin feed strategies without sharing Douyin code.</summary>
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
          const isStrongPlayUrl=v=>{
            if(typeof v!=='string'||!/^https?:/i.test(v)) return false;
            if(/\.(jpg|jpeg|png|webp|gif)([?#]|$)/i.test(v)) return false;
            // Prefer durable CDN; still keep webapp-prime as last resort.
            return /tiktokcdn|byteoversea|muscdn|tiktokv\.com|\/video\/tos\//i.test(v);
          };
          const scavengePlayUrlsFromTree=(root, expectedId)=>{
            const urls=[];
            if(!root||typeof root!=='object') return urls;
            const seen=new WeakSet(); let budget=1800;
            const visit=(value,depth,matched)=>{
              if(--budget<0||depth>14||value==null) return;
              if(typeof value==='string'){
                if(matched && isStrongPlayUrl(value)) urls.push(value);
                return;
              }
              if(typeof value!=='object'||value instanceof Node||seen.has(value)) return;
              seen.add(value);
              const id=value.id||value.itemId||value.videoId||value.aweme_id||value.awemeId;
              const nextMatched=matched || (!!expectedId && id!=null && String(id)===String(expectedId));
              for(const [key,child] of Object.entries(value)){
                if(/cover|avatar|thumbnail|subtitle|icon|logo|image/i.test(key)) continue;
                visit(child,depth+1,nextMatched);
              }
            };
            visit(root,0,false);
            return [...new Set(urls)];
          };
          const scavengePlayUrlsFromPlayer=(active, expectedId)=>{
            const urls=[];
            if(!active||!expectedId) return urls;
            for(let el=active,i=0;el&&i<16;el=el.parentElement,i++){
              for(const key of Object.keys(el)){
                let props=null;
                if(key.startsWith('__reactProps$')) props=el[key];
                else if(key.startsWith('__reactFiber$')) props=el[key]?.memoizedProps||el[key]?.pendingProps||el[key];
                for(const u of scavengePlayUrlsFromTree(props, expectedId)) urls.push(u);
              }
            }
            return [...new Set(urls)];
          };
          const scavengePlayUrlsFromPerf=expectedId=>{
            try{
              return performance.getEntriesByType('resource').map(e=>e.name).filter(value=>{
                if(!isStrongPlayUrl(value)) return false;
                try{
                  const u=new URL(value);
                  const id=u.searchParams.get('item_id')||u.searchParams.get('aweme_id')||
                           u.searchParams.get('video_id')||u.searchParams.get('__vid');
                  return !!id && id===String(expectedId);
                }catch{ return false; }
              });
            }catch{ return []; }
          };
          const pushPlay=(urls,v)=>{
            if(typeof v==='string' && /^https?:/i.test(v) && !/\.(jpg|jpeg|png|webp|gif)([?#]|$)/i.test(v))
              urls.push(v);
            else if(v&&typeof v==='object'){
              for(const k of ['urlList','url_list','playAddr','play_addr','downloadAddr','download_addr','playApi','play_api','url','uri']){
                const c=v[k];
                if(Array.isArray(c)) c.forEach(x=>pushPlay(urls,x));
                else pushPlay(urls,c);
              }
            }
          };
          const collectPlayUrls=record=>{
            const urls=[];
            if(!record) return urls;
            pushPlay(urls, record.video);
            pushPlay(urls, record.video?.playAddr||record.video?.play_addr);
            pushPlay(urls, record.video?.downloadAddr||record.video?.download_addr);
            const rates=record.video?.bitrateInfo||record.video?.bit_rate||record.bit_rate||record.bitrateInfo;
            if(Array.isArray(rates)) rates.forEach(r=>pushPlay(urls,r));
            return [...new Set(urls)];
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
          const findItemRecordById=expectedId=>{
            if(!expectedId) return null;
            try{
              const module=window.SIGI_STATE?.ItemModule?.[expectedId];
              if(module && (module.video||module.playAddr||module.play_addr)) return module;
            }catch{}
            try{
              const detail=window.__UNIVERSAL_DATA_FOR_REHYDRATION__?.__DEFAULT_SCOPE__?.['webapp.video-detail']?.itemInfo?.itemStruct;
              if(detail && String(detail.id||detail.itemId||'')===expectedId) return detail;
            }catch{}
            const roots=[];
            const push=v=>{if(v&&typeof v==='object')roots.push(v);};
            try{ const el=document.getElementById('SIGI_STATE')||document.getElementById('__UNIVERSAL_DATA_FOR_REHYDRATION__'); if(el?.textContent) push(JSON.parse(el.textContent)); }catch{}
            for(const key of ['SIGI_STATE','__UNIVERSAL_DATA_FOR_REHYDRATION__','__NEXT_DATA__']){ try{push(window[key]);}catch{} }
            const seen=new WeakSet(); let budget=1200;
            const find=(value,depth)=>{
              if(!value||typeof value!=='object'||value instanceof Node||seen.has(value)||depth>14||--budget<0) return null;
              seen.add(value);
              const vid=String(value.id||value.itemId||value.videoId||value.aweme_id||'');
              const hasVideo=!!(value.video||value.playAddr||value.play_addr||value.bitrateInfo||value.bit_rate);
              if(hasVideo && vid===expectedId) return value;
              if(Array.isArray(value)){ for(const c of value.slice(0,48)){const r=find(c,depth+1); if(r) return r;} }
              else { for(const c of Object.values(value)){const r=find(c,depth+1); if(r) return r;} }
              return null;
            };
            for(const root of roots){ const r=find(root,0); if(r) return r; }
            return null;
          };
          const cardCaption=active=>{
            const card=active?.closest?.('[data-e2e="recommend-list-item-container"],[data-e2e="feed-video"],article,section');
            const selectors=[
              '[data-e2e="browse-video-desc"]','[data-e2e="video-desc"]','[data-e2e="new-desc-span"]',
              '[data-e2e="video-desc-span"]','[data-e2e="browse-video-desc-new"]','[data-e2e="video-meta-caption"]',
              '[data-e2e="detail-desc"]','h1','[class*="Desc"]','[class*="desc"]'
            ];
            for(const sel of selectors){
              const el=(card||document).querySelector(sel);
              const text=(el?.getAttribute?.('title')||el?.textContent||'').trim().replace(/(?:展开|收起|See more|See less)\s*$/i,'').trim();
              if(text && text.length>=2 && text!=='视频' && !/^TikTok/i.test(text)) return text.slice(0,240);
            }
            return '';
          };
          const observeTikTokAlbum = () => {
            const id=(location.pathname.match(/\/video\/(\d{10,})/)||[])[1];
            if(!id) return null;
            const record=findItemRecordById(id);
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
            for (let scope=active,i=0; scope && i<12; i++, scope=scope.parentElement) {
              const value = scope.getAttribute('data-e2e-vid') || scope.getAttribute('data-video-id');
              if (value && !explicit) explicit = 'content:' + value;
              const wrap = (scope.id||'').match(/^xgwrapper-\d+-(\d{10,})$/);
              if(wrap && !explicit) explicit = 'content:'+wrap[1];
              const desc = scope.querySelector('[data-e2e="browse-video-desc"],[data-e2e="video-desc"],[data-e2e="new-desc-span"],[data-e2e="video-desc-span"],[data-e2e="browse-video-desc-new"]');
              if (desc && !caption) caption = (desc.getAttribute('title')||desc.textContent||'').trim();
              if (explicit && caption) break;
            }
            const pathId=(location.pathname.match(/\/video\/(\d{10,})/)||[])[1];
            if(pathId && !explicit) explicit='content:'+pathId;
            if(!explicit){
              const wrap=(active.closest('[id^="xgwrapper-"]')?.id||'').match(/xgwrapper-\d+-(\d{10,})/);
              if(wrap) explicit='content:'+wrap[1];
            }
            if(!explicit){
              const card=active.closest('[data-e2e="feed-active-video"],[data-e2e="recommend-list-item-container"]');
              const vid=card?.getAttribute('data-e2e-vid')||card?.querySelector('[data-e2e-vid]')?.getAttribute('data-e2e-vid');
              if(vid && /^\d{10,}$/.test(vid)) explicit='content:'+vid;
            }
            if(!explicit) return null;
            const id=explicit.replace(/^content:/,'');
            const record=findItemRecordById(id);
            if(record){
              const fromData=String(record.desc||record.description||record.title||'').trim();
              if(fromData) caption=fromData;
            }
            if(!caption) caption=cardCaption(active);
            caption=(caption||'').replace(/(?:展开|收起|See more|See less)\s*$/i,'').trim();
            const media=[...new Set([
              ...collectPlayUrls(record),
              ...scavengePlayUrlsFromPlayer(active, id),
              ...scavengePlayUrlsFromPerf(id),
              active.currentSrc, active.src,
              ...[...active.querySelectorAll('source')].map(e=>e.src)
            ].filter(u=>/^https?:/i.test(u||'')))];
            // Prefer durable CDN URLs ahead of blob/webapp-prime crumbs.
            media.sort((a,b)=>Number(isStrongPlayUrl(b))-Number(isStrongPlayUrl(a))
              -Number(/webapp-prime/i.test(b))+Number(/webapp-prime/i.test(a)));
            const durationSec=(Number.isFinite(active.duration)&&active.duration>0)?active.duration:null;
            return { type:'vd-video-identity', identity: location.host+':'+explicit, caption, href:location.href,
              media, durationSec };
          };
          window.__vdProbe=()=>{
            const observation=window.__vdObserve(); if(!observation) return null;
            if(!observation.images?.length){
              const album=observeTikTokAlbum();
              if(album?.images?.length){ observation.images=album.images; observation.album=true;
                for(const u of album.media||[]) observation.media.push(u); }
            }
            if(!observation.caption){
              const id=(observation.identity||'').match(/(\d{10,})/)?.[1];
              const record=findItemRecordById(id);
              if(record) observation.caption=String(record.desc||record.description||record.title||'').trim();
            }
            if(!observation.caption) observation.caption=cardCaption(activePlayer());
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
