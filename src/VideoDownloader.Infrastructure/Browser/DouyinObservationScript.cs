namespace VideoDownloader.Infrastructure.Browser;

/// <summary>Douyin-only page observation (album + video). Not shared with TikTok/Generic.</summary>
internal static class DouyinObservationScript
{
    internal const string Body = """
          const visible = el => {
            const style=getComputedStyle(el);
            if(style.visibility==='hidden'||style.display==='none') return 0;
            const r = el.getBoundingClientRect();
            const area = Math.max(0, Math.min(r.bottom, innerHeight) - Math.max(r.top, 0)) *
                   Math.max(0, Math.min(r.right, innerWidth) - Math.max(r.left, 0));
            if(area<=0) return 0;
            if(style.opacity==='0'){
              if((el.tagName==='VIDEO'||el.tagName==='AUDIO') && (el.currentSrc||el.src)) return area;
              return 0;
            }
            return area;
          };
          const ids = ['videoId','video_id','aweme_id','modal_id','itemId','item_id','id'];
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
                 (value.video||value.playAddr||value.play_addr||value.images||value.image_list) &&
                 (value.desc||value.description||value.title||value.images||value.image_list)) return value;
              for(const key of ['item','itemInfo','aweme','awemeInfo','data','videoData','props','children']){
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
          const isLiveMedia = el => {
            if(!(el instanceof HTMLMediaElement)) return false;
            const src=String(el.currentSrc||el.src||'');
            if(/^https?:/i.test(src) && (/\.flv([?#]|$)/i.test(src) || /\/flv\//i.test(src))) return true;
            const duration=el.duration;
            if(Number.isFinite(duration) && duration>0) return false;
            if(duration===Infinity) return true;
            let seekEnd=0;
            try{ if(el.seekable && el.seekable.length>0) seekEnd=el.seekable.end(el.seekable.length-1); }catch{}
            if(Number.isFinite(seekEnd) && seekEnd>0) return false;
            return false;
          };
          const activePlayer=()=>{
            const players=[...document.querySelectorAll('video,audio')].filter(e=>visible(e)>0 && !isLiveMedia(e));
            players.sort((a,b)=>Number(!b.paused)-Number(!a.paused)||visible(b)-visible(a));
            return players[0];
          };
          const collectDouyinImages = record => {
            const urls=[];
            const push=v=>{
              if(typeof v==='string' && /^https?:/i.test(v) &&
                 (/\.(jpg|jpeg|png|webp)([?#]|$)/i.test(v) || /(?:byteimg|douyinpic).*\/(?:tos-|obj\/|image)/i.test(v)))
                urls.push(v);
              else if(v&&typeof v==='object'){
                for(const k of ['urlList','url_list','download_url_list','display_image','origin','url']){
                  const child=v[k];
                  if(Array.isArray(child)) { const first=child.find(u=>typeof u==='string' && /^https?:/i.test(u)); if(first) push(first); }
                  else if(typeof child==='string') push(child);
                }
              }
            };
            for(const key of ['images','image_list','imageList','image_post_info','imagePost','photos','image_infos']){
              const block=record?.[key];
              if(Array.isArray(block)) block.forEach(push);
              else if(block?.images) block.images.forEach(push);
              else if(block?.image_list) block.image_list.forEach(push);
            }
            return [...new Set(urls)];
          };
          const collectDouyinPlayUrls = record => {
            const urls=[];
            const push=v=>{
              if(typeof v==='string' && /^https?:/i.test(v) &&
                 !/\.(jpg|jpeg|png|webp|gif|svg)([?#]|$)/i.test(v) &&
                 !/(?:byteimg|douyinpic)\./i.test(v))
                urls.push(v);
              else if(v&&typeof v==='object'){
                for(const k of ['urlList','url_list','playAddr','play_addr','downloadAddr','download_addr','uri','url']){
                  const child=v[k];
                  if(Array.isArray(child)) child.forEach(push);
                  else push(child);
                }
              }
            };
            if(!record) return urls;
            push(record.video||{});
            push(record.video?.play_addr||record.video?.playAddr);
            push(record.video?.download_addr||record.video?.downloadAddr);
            push(record.music||{});
            return [...new Set(urls)];
          };
          const findAwemeRecordById = expectedId => {
            if(!expectedId) return null;
            const roots=[];
            const push=v=>{if(v&&typeof v==='object')roots.push(v);};
            try{
              const el=document.getElementById('__UNIVERSAL_DATA_FOR_REHYDRATION__');
              if(el?.textContent) push(JSON.parse(el.textContent));
            }catch{}
            try{push(window.__UNIVERSAL_DATA_FOR_REHYDRATION__);}catch{}
            const seen=new WeakSet(); let budget=1200;
            const find=(value,depth)=>{
              if(!value||typeof value!=='object'||value instanceof Node||seen.has(value)||depth>12||--budget<0) return null;
              seen.add(value);
              const id = value.aweme_id||value.itemId||value.videoId||value.id||value.modal_id||value.note_id;
              const hasVideo = !!(value.video||value.playAddr||value.play_addr);
              if(hasVideo && String(id)===String(expectedId)) return value;
              if(Array.isArray(value)){ for(const c of value.slice(0,40)){const r=find(c,depth+1); if(r) return r;} }
              else { for(const c of Object.values(value)){const r=find(c,depth+1); if(r) return r;} }
              return null;
            };
            for(const root of roots){ const r=find(root,0); if(r) return r; }
            return null;
          };
          const pathAwemeId = () => (location.pathname.match(/\/(?:video|note)\/(\d{10,})/)||[])[1] || null;
          const queryAwemeId = () => (location.search.match(/(?:modal_id|aweme_id|item_id)=(\d{10,})/)||[])[1] || null;
          const playerAwemeId = (active, record) => {
            let explicit = '';
            const recordId = record ? String(record.aweme_id||record.itemId||record.videoId||record.id||'') : '';
            if (active) {
              for (let scope=active,i=0; scope && i<8; i++, scope=scope.parentElement) {
                for (const attr of ['data-e2e-vid','data-video-id','data-aweme-id','data-item-id']) {
                  const value = scope.getAttribute(attr);
                  if (value && !explicit) explicit = value;
                }
                const wrap = (scope.id||'').match(/^xgwrapper-\d+-(\d{10,})$/);
                if (wrap && !explicit) explicit = wrap[1];
                if (explicit) break;
              }
            }
            const fromExplicit = (String(explicit).match(/(\d{10,})/)||[])[1] || '';
            const fromRecord = (recordId.match(/(\d{10,})/)||[])[1] || '';
            return fromExplicit || fromRecord || null;
          };
          // Dedicated /video|/note: pathId wins. Feed/search/home/modal/SPA: playerId wins; query is fallback only.
          // Never prefer query/page id over the active player (stale modal_id freezes identity across swipes).
          const resolveAwemeId = (active, record) => {
            const pathId = pathAwemeId();
            if (pathId) return pathId;
            return playerAwemeId(active, record) || queryAwemeId() || null;
          };
          const observeDouyinAlbum = () => {
            let record=null;
            const active = activePlayer();
            const currentRecord=playerRecord(active);
            const expectedId=resolveAwemeId(active, currentRecord);
            if(!expectedId) return null;
            const roots=[];
            const push=v=>{if(v&&typeof v==='object')roots.push(v);};
            try{
              const el=document.getElementById('__UNIVERSAL_DATA_FOR_REHYDRATION__');
              if(el?.textContent) push(JSON.parse(el.textContent));
            }catch{}
            try{push(window.__UNIVERSAL_DATA_FOR_REHYDRATION__);}catch{}
            const seen=new WeakSet(); let budget=900;
            const find=(value,depth)=>{
              if(!value||typeof value!=='object'||value instanceof Node||seen.has(value)||depth>12||--budget<0) return null;
              seen.add(value);
              const hasImages = !!(value.images||value.image_list||value.image_post_info||value.imagePost||value.image_infos);
              const id = value.aweme_id||value.itemId||value.videoId||value.id||value.modal_id||value.note_id;
              if(hasImages && String(id)===expectedId) return value;
              if(Array.isArray(value)){ for(const c of value.slice(0,40)){const r=find(c,depth+1); if(r) return r;} }
              else { for(const c of Object.values(value)){const r=find(c,depth+1); if(r) return r;} }
              return null;
            };
            for(const root of roots){ record=find(root,0); if(record) break; }
            if(!record){
              if(!/\/note\//i.test(location.pathname)) return null;
              const imgs=[...document.querySelectorAll('img')].filter(e=>{
                const r=e.getBoundingClientRect();
                const src=e.currentSrc||e.src||'';
                return r.width>120 && r.height>120 && visible(e)>0 && /^https?:/i.test(src) &&
                  !/avatar|emoji|emoticon|badge|logo/i.test(src);
              }).slice(0,24);
              const audio=[...document.querySelectorAll('audio,video')].find(e=>/^https?:/i.test(e.currentSrc||e.src||''));
              if(imgs.length<1) return null;
              const id=expectedId;
              const caption=(document.querySelector('[data-e2e="browse-video-desc"],.desc')?.textContent
                || document.title || '').trim().replace(/\s*[_|].*抖音.*$/u,'').trim();
              const media=[]; if(audio) media.push(audio.currentSrc||audio.src);
              return { type:'vd-video-identity', identity: id ? (location.host+':content:'+id) : pageKey(location.href),
                caption, href:location.href, media, images:imgs.map(e=>e.currentSrc||e.src), album:true };
            }
            const images=collectDouyinImages(record);
            if(images.length<1) return null;
            const music=record.music||{};
            const audioUrls=[];
            const pushAudio=v=>{
              if(typeof v==='string' && /^https?:/i.test(v) && !/\.(jpg|jpeg|png|webp|gif)([?#]|$)/i.test(v)) audioUrls.push(v);
              else if(v&&typeof v==='object'){
                for(const k of ['playUrl','play_url','uri','url','urlList','url_list']){
                  const c=v[k]; if(Array.isArray(c)) c.forEach(pushAudio); else pushAudio(c);
                }
              }
            };
            pushAudio(music.play_url||music.playUrl||music);
            const id=String(record.aweme_id||record.itemId||record.videoId||record.id||expectedId||'');
            const caption=String(record.desc||record.description||record.title||'').trim();
            return { type:'vd-video-identity', identity: id ? (location.host+':content:'+id) : pageKey(location.href),
              caption, href:location.href, media:[...new Set(audioUrls)], images, album:true };
          };
          window.__vdObserve = () => {
            const album = observeDouyinAlbum();
            if (album?.images?.length > 0) return album;
            const active = activePlayer();
            if (!active) return null;
            const record=playerRecord(active);
            const recordId=record ? String(record.aweme_id||record.itemId||record.videoId||record.id||'') : '';
            let caption = record ? String(record.desc||record.description||record.title||'').trim() : '';
            for (let scope=active,i=0; scope && i<8; i++, scope=scope.parentElement) {
              const desc = scope.querySelector('[data-e2e="browse-video-desc"],[data-e2e="video-desc"],[data-e2e="detail-desc"]');
              if (desc && !caption) caption = (desc.textContent||'').trim();
              if (caption) break;
            }
            const pathId = pathAwemeId();
            const playerId = playerAwemeId(active, record);
            const queryId = queryAwemeId();
            const awemeId = resolveAwemeId(active, record);
            if(!awemeId) return null;
            // Feed without player/query and without dedicated path cannot claim a stable work id.
            if(!pathId && !playerId && !queryId) return null;
            const samePlayer = !playerId || playerId === awemeId;
            const dataRecord=findAwemeRecordById(awemeId) || (recordId===awemeId ? record : null);
            const fromData=collectDouyinPlayUrls(dataRecord);
            const resourceKey=value=>{try{const u=new URL(value);const i=u.pathname.indexOf('/video/tos/');return i>=0 ? u.pathname.slice(i) : u.origin+u.pathname;}catch{return '';}};
            const fromPlayer=samePlayer ? [active.currentSrc,active.src,...[...active.querySelectorAll('source')].map(e=>e.src)].filter(value=>{
              if(!/^https?:/i.test(value||'')) return false;
              const id=new URL(value).searchParams.get('__vid');
              if(id) return id===awemeId;
              return fromData.some(u=>resourceKey(u)===resourceKey(value));
            }) : [];
            caption=dataRecord ? String(dataRecord.desc||dataRecord.description||dataRecord.title||'').trim() : (samePlayer ? caption : '');
            const durationSec=(Number.isFinite(active.duration)&&active.duration>0)?active.duration:null;
            return { type:'vd-video-identity', identity:'content:'+awemeId, caption, href:location.href,
              media:[...new Set([...fromPlayer, ...fromData])], durationSec };
          };
          window.__vdProbe=()=>{
            const observation=window.__vdObserve(); if(!observation) return null;
            const record=playerRecord(activePlayer());
            const visit=(value,depth,seen,budget)=>{
              if(--budget.n<0||depth>10||value==null)return;
              if(typeof value==='string'){
                if(/^https?:\/\//i.test(value) && !/\.(jpg|jpeg|png|webp|gif|svg)([?#]|$)/i.test(value) &&
                   !/(?:byteimg|douyinpic)\./i.test(value)) observation.media.push(value);
                return;
              }
              if(typeof value!=='object'||value instanceof Node||seen.has(value))return;
              seen.add(value);
              for(const [key,child] of Object.entries(value)){
                if(/cover|avatar|thumbnail|subtitle|music_cover|dynamic_cover/i.test(key))continue;
                visit(child,depth+1,seen,budget);
              }
            };
            const observedId=(observation.identity.match(/(\d{10,})/)||[])[1];
            const recordId=record ? String(record.aweme_id||record.itemId||record.videoId||record.id||'') : '';
            if(record && observedId && recordId===observedId){
              if(!observation.caption) observation.caption=String(record.desc||record.description||record.title||'').trim();
              visit(record.video||record,0,new WeakSet(),{n:800});
              visit(record.music||{},0,new WeakSet(),{n:200});
            }
            if(!observation.images?.length){
              const album=observeDouyinAlbum();
              if(album?.images?.length && album.identity===observation.identity){
                observation.images=album.images; observation.album=true;
                for(const u of album.media||[]) observation.media.push(u);
              }
            }
            observation.media=[...new Set(observation.media)];
            return observation;
          };
          let scheduled=false;
          const scan=()=>{ if(scheduled) return; scheduled=true; queueMicrotask(()=>{ scheduled=false; const r=window.__vdObserve(); if(r){ try{ chrome.webview.postMessage(r);}catch{}} }); };
          new MutationObserver(scan).observe(document,{childList:true,subtree:true,attributes:true,characterData:true});
          document.addEventListener('playing',scan,true);
          document.addEventListener('loadedmetadata',scan,true);
          setInterval(scan,1500); scan();
        """;
}
