namespace VideoDownloader.Infrastructure.Browser;

internal static class VideoObservationScript
{
    internal const string Install = """
        (() => {
          if (window.__vdObserve) return;
          const visible = el => {
            const style=getComputedStyle(el);
            if(style.visibility==='hidden'||style.display==='none') return 0;
            const r = el.getBoundingClientRect();
            const area = Math.max(0, Math.min(r.bottom, innerHeight) - Math.max(r.top, 0)) *
                   Math.max(0, Math.min(r.right, innerWidth) - Math.max(r.left, 0));
            if(area<=0) return 0;
            // Feed players (TikTok etc.) often keep the real media element at opacity 0 while
            // painting through canvas. Treat in-viewport media with a source as active.
            if(style.opacity==='0'){
              if((el.tagName==='VIDEO'||el.tagName==='AUDIO') && (el.currentSrc||el.src)) return area;
              return 0;
            }
            return area;
          };
          const ids = ['videoId','video_id','aweme_id','modal_id','itemId','item_id','bvid','aid','v','id'];
          const pageKey = url => {
            const u = new URL(url, location.href);
            const query = ids.filter(k => u.searchParams.has(k)).map(k => [k,u.searchParams.get(k)]);
            return u.origin + u.pathname + JSON.stringify(query);
          };
          const playerRecord = active => {
            const seen=new WeakSet();let budget=160;
            const find=(value,depth)=>{
              if(!value||typeof value!=='object'||value instanceof Node||seen.has(value)||depth>4||--budget<0)return null;
              seen.add(value);
              if((value.id||value.aweme_id||value.itemId||value.videoId) &&
                 (value.video||value.playAddr||value.play_addr) &&
                 (value.desc||value.description||value.title)) return value;
              for(const key of ['item','itemInfo','itemStruct','aweme','awemeInfo','data','videoData','videoInfo','props','children']){
                const child=value[key];
                if(Array.isArray(child)){for(const c of child.slice(0,8)){const r=find(c,depth+1);if(r)return r;}}
                else {const r=find(child,depth+1);if(r)return r;}
              }
              return null;
            };
            for(let el=active,i=0;el&&i<12;el=el.parentElement,i++){
              for(const key of Object.keys(el)){
                let props=null;
                if(key.startsWith('__reactProps$'))props=el[key];
                else if(key.startsWith('__reactFiber$'))props=el[key]?.memoizedProps;
                else if(key==='__vueParentComponent')props=el[key]?.props;
                const record=find(props,0);if(record)return record;
              }
            }
            return null;
          };
          // Live vs VOD: only strong media-timeline / transport traits.
          // Never use caption text. Never treat "playing blob without seekable yet" as live —
          // Douyin/TikTok VOD commonly uses MSE/blob where duration/seekable appear late.
          const isLiveMedia = el => {
            if(!(el instanceof HTMLMediaElement)) return false;
            const src=String(el.currentSrc||el.src||'');
            // HTTP(S) FLV pipes are live. blob:/mediasource: must not use path heuristics.
            if(/^https?:/i.test(src) &&
               (/\.flv([?#]|$)/i.test(src) || /\/flv\//i.test(src) || /[?&](?:mime_type|media_type)=video_flv\b/i.test(src)))
              return true;
            const duration=el.duration;
            if(Number.isFinite(duration) && duration>0) return false;
            if(duration===Infinity) return true;
            let seekEnd=0;
            try{
              if(el.seekable && el.seekable.length>0)
                seekEnd=el.seekable.end(el.seekable.length-1);
            }catch{}
            if(Number.isFinite(seekEnd) && seekEnd>0) return false;
            // Ambiguous (NaN duration on blob/MSE while buffering): not live.
            return false;
          };
          const activePlayer=()=>{
            const preferred=document.querySelector('[data-e2e="feed-active-video"] video,[data-e2e="feed-active-video"] audio');
            const players=[...document.querySelectorAll('video,audio')].filter(e=>visible(e)>0 && !isLiveMedia(e));
            if(preferred && players.includes(preferred)) return preferred;
            players.sort((a,b)=>Number(!b.paused)-Number(!a.paused)||visible(b)-visible(a));
            return players[0];
          };
          window.__vdObserve = () => {
            const active = activePlayer();
            if (!active) return null;
            const record=playerRecord(active);
            let scope = active, explicit = record ? 'content:'+(record.aweme_id||record.itemId||record.videoId||record.id) : '',
                caption = record ? String(record.desc||record.description||record.title||'').trim() : '';
            for (let i=0; scope && i<8; i++, scope=scope.parentElement) {
              for (const attr of ['data-e2e-vid','data-video-id','data-aweme-id','data-item-id','data-bvid','data-id']) {
                const value = scope.getAttribute(attr);
                if(attr==='data-id' && !/^(\d{8,}|BV[0-9A-Za-z]+)$/.test(value||'')) continue;
                if (value && !explicit) explicit = (attr==='data-e2e-vid'?'content:':attr+':') + value;
              }
              const wrap = (scope.id||'').match(/^xgwrapper-\d+-(\d{10,})$/);
              if(wrap && !explicit) explicit = 'content:'+wrap[1];
              const classVid = String(scope.className||'').match(/(?:^|\s)video_(\d{10,})(?:\s|$)/);
              if(classVid && !explicit) explicit = 'content:'+classVid[1];
              const link = scope.querySelector('a[href*="/video/"],a[href*="modal_id="],a[href*="/watch?v="],a[href*="/@"][href*="/video/"]');
              if (link && !explicit) explicit = pageKey(link.href);
              const desc = scope.querySelector('[data-e2e="browse-video-desc"],[data-e2e="video-desc"],[data-e2e="detail-desc"],[data-e2e="new-desc-span"],[data-e2e="video-meta-caption"],[data-video-caption],figcaption');
              if (desc && !caption) caption = desc.textContent.trim();
              if (explicit && caption) break;
              // Never collect metadata from a container containing another visible player.
              if (scope.parentElement && [...scope.parentElement.querySelectorAll('video,audio')].some(e => e !== active && visible(e)>0)) break;
            }
            const base = pageKey(location.href);
            if (!explicit && active.src) {
              try {
                const source = new URL(active.src,location.href);
                for (const field of ['aweme_id','item_id','bvid','target']) {
                  const value=source.searchParams.getAll(field).find(v=>/^(\d{15,}|BV[0-9A-Za-z]+)$/.test(v));
                  if(value){explicit='content:'+value;break;}
                }
              } catch {}
            }
            caption=caption.replace(/(?:展开|收起|See more|See less)\s*$/i,'').trim();
            if(!caption){
              const card=active.closest('[data-e2e="feed-active-video"],[data-e2e="feed-item"],[data-e2e="recommend-list-item-container"],[data-e2e="feed-video"],article,#one-column-item-0')
                || active.closest('section')?.parentElement;
              const desc=card?.querySelector('[data-e2e="browse-video-desc"],[data-e2e="video-desc"],[data-e2e="detail-desc"],[data-e2e="new-desc-span"],[data-e2e="video-meta-caption"],[data-e2e="browse-video-desc-new"]');
              if(desc) caption=desc.textContent.trim().replace(/(?:展开|收起|See more|See less)\s*$/i,'').trim();
            }
            // Transport addresses deliberately never participate in logical identity.
            const stablePage = ids.some(k => new URL(location.href).searchParams.has(k)) || /\/(video|shorts)\/[^/]+/.test(location.pathname);
            // Feed roots without a concrete content id are not a stable logical video yet.
            if(!stablePage && !explicit) return null;
            const identity = stablePage ? base : location.host + ':' + explicit;
            return { type:'vd-video-identity', identity, caption, href:location.href,
              media:[...new Set([active.currentSrc,active.src,...[...active.querySelectorAll('source')].map(e=>e.src)].filter(u=>/^https?:/i.test(u||'')))] };
          };
          const findItemById = id => {
            if(!id) return null;
            const roots=[];
            const push=v=>{if(v&&typeof v==='object')roots.push(v);};
            try{
              const el=document.getElementById('__UNIVERSAL_DATA_FOR_REHYDRATION__')
                || document.getElementById('SIGI_STATE')
                || document.getElementById('__NEXT_DATA__');
              if(el?.textContent) push(JSON.parse(el.textContent));
            }catch{}
            for(const key of ['__UNIVERSAL_DATA_FOR_REHYDRATION__','SIGI_STATE','__NEXT_DATA__','__INITIAL_STATE__']){
              try{push(window[key]);}catch{}
            }
            for(const script of document.querySelectorAll('script[type="application/json"],script#SIGI_STATE,script#__UNIVERSAL_DATA_FOR_REHYDRATION__')){
              try{if(script.textContent)push(JSON.parse(script.textContent));}catch{}
            }
            const seen=new WeakSet();let budget=1200;
            const match=(value,depth)=>{
              if(!value||typeof value!=='object'||value instanceof Node||seen.has(value)||depth>14||--budget<0)return null;
              seen.add(value);
              const vid=String(value.id??value.aweme_id??value.itemId??value.videoId??value.modal_id??'');
              if(vid===id && (value.video||value.playAddr||value.play_addr||value.bit_rate||value.bitrateInfo)) return value;
              if(Array.isArray(value)){
                for(const child of value.slice(0,48)){const hit=match(child,depth+1);if(hit)return hit;}
              }else{
                for(const child of Object.values(value)){const hit=match(child,depth+1);if(hit)return hit;}
              }
              return null;
            };
            for(const root of roots){const hit=match(root,0);if(hit)return hit;}
            return null;
          };
          window.__vdProbe=()=>{
            const observation=window.__vdObserve();if(!observation)return null;
            const record=playerRecord(activePlayer());
            const seen=new WeakSet();let budget=800;
            const visit=(value,depth)=>{
              if(--budget<0||depth>10||value==null)return;
              if(typeof value==='string'){
                if(/^https?:\/\//i.test(value) && !/\.(jpg|jpeg|png|webp|gif|svg)([?#]|$)/i.test(value))observation.media.push(value);
                return;
              }
              if(typeof value!=='object'||value instanceof Node||seen.has(value))return;
              seen.add(value);
              for(const [key,child] of Object.entries(value)){
                if(/cover|avatar|thumbnail|subtitle|music_cover|dynamic_cover/i.test(key))continue;
                visit(child,depth+1);
              }
            };
            if(record){
              if(!observation.caption) observation.caption=String(record.desc||record.description||record.title||'').trim();
              visit(record.video||record,0);
            }
            // Blob players often expose only the content id on the wrapper. Resolve that
            // item from hydration / feed JSON so play addresses bind to the same identity.
            const id=(observation.identity||'').match(/content:(\d{10,}|BV[\w]+)/)?.[1]
              || (activePlayer()?.closest('[id^="xgwrapper-"]')?.id||'').match(/xgwrapper-\d+-(\d{10,})/)?.[1];
            if(id){
              const byId=findItemById(id);
              if(byId){
                if(!observation.caption) observation.caption=String(byId.desc||byId.description||byId.title||'').trim();
                visit(byId.video||byId,0);
              }
            }
            observation.media=[...new Set(observation.media)];
            return observation;
          };
          let scheduled = false;
          const scan = () => {
            if (scheduled) return;
            scheduled = true;
            queueMicrotask(() => {
              scheduled = false;
              const result = window.__vdObserve();
              if (result) { try { chrome.webview.postMessage(result); } catch {} }
            });
          };
          // Document-created scripts run before documentElement exists. Observe the Document itself.
          new MutationObserver(scan).observe(document, {childList:true,subtree:true,attributes:true,characterData:true});
          document.addEventListener('playing',scan,true);
          document.addEventListener('loadedmetadata',scan,true);
          setInterval(scan,1500);
          scan();
        })();
        """;
}
