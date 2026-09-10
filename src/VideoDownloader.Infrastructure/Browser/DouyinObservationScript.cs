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
            const seen=new WeakSet();let budget=280;
            const find=(value,depth)=>{
              if(!value||typeof value!=='object'||value instanceof Node||seen.has(value)||depth>6||--budget<0)return null;
              seen.add(value);
              const hasId=!!(value.aweme_id||value.awemeId||value.itemId||value.item_id||value.videoId||value.video_id||value.group_id||value.modal_id||value.id);
              const hasVideo=!!(value.video||value.playAddr||value.play_addr||value.bit_rate||value.bitRate||value.images||value.image_list);
              const hasCaption=!!(value.desc||value.description||value.title||value.images||value.image_list);
              // Prefer captioned aweme; still accept playAddr-only nodes (feed MSE often drops desc).
              if(hasId && hasVideo && (hasCaption || value.video || value.play_addr || value.playAddr || value.bit_rate))
                return value;
              for(const key of ['item','itemInfo','aweme','awemeInfo','awemeDetail','data','videoData','props','children','memoizedProps','pendingProps']){
                const child=value[key];
                if(Array.isArray(child)){for(const c of child.slice(0,12)){const r=find(c,depth+1);if(r)return r;}}
                else {const r=find(child,depth+1);if(r)return r;}
              }
              return null;
            };
            for(let el=active,i=0;el&&i<16;el=el.parentElement,i++){
              for(const key of Object.keys(el)){
                let props=null;
                if(key.startsWith('__reactProps$'))props=el[key];
                else if(key.startsWith('__reactFiber$'))props=el[key]?.memoizedProps||el[key]?.pendingProps;
                else if(key==='__vueParentComponent')props=el[key]?.props;
                const record=find(props,0);if(record)return record;
              }
            }
            return null;
          };
          const isStrongVodUrl = value =>
            /^https?:/i.test(value||'') &&
            !/\.(jpg|jpeg|png|webp|gif|svg)([?#]|$)/i.test(value) &&
            !/(?:byteimg|douyinpic)\./i.test(value) &&
            !/^blob:/i.test(value) &&
            (/\/video\/tos\//i.test(value) ||
             /\/aweme\/v1\/play/i.test(value) ||
             /(?:douyinvod|zjcdn|bytecdn|byteicdn|douyincdn)\./i.test(value));
          const scavengePlayUrlsFromTree = (root, expectedId) => {
            const urls=[];
            if(!root||typeof root!=='object') return urls;
            const seen=new WeakSet(); let budget=2200;
            const visit=(value,depth,matched)=>{
              if(--budget<0||depth>16||value==null) return;
              if(typeof value==='string'){
                if(matched && isStrongVodUrl(value)) urls.push(value);
                return;
              }
              if(typeof value!=='object'||value instanceof Node||seen.has(value)) return;
              seen.add(value);
              const id=value.aweme_id||value.awemeId||value.itemId||value.item_id||value.videoId||value.video_id||value.group_id||value.modal_id||value.id;
              const nextMatched=matched || (!!expectedId && id!=null && String(id)===String(expectedId));
              for(const [key,child] of Object.entries(value)){
                if(/cover|avatar|thumbnail|subtitle|music_cover|dynamic_cover|icon|logo/i.test(key)) continue;
                visit(child,depth+1,nextMatched);
              }
            };
            visit(root,0,!expectedId);
            return [...new Set(urls)];
          };
          const scavengePlayUrlsFromPlayer = (active, expectedId) => {
            const urls=[];
            if(!active) return urls;
            for(let el=active,i=0;el&&i<16;el=el.parentElement,i++){
              for(const key of Object.keys(el)){
                let props=null;
                if(key.startsWith('__reactProps$')) props=el[key];
                else if(key.startsWith('__reactFiber$')) props=el[key]?.memoizedProps||el[key]?.pendingProps||el[key];
                else if(key==='__vueParentComponent') props=el[key]?.props;
                for(const u of scavengePlayUrlsFromTree(props, expectedId)) urls.push(u);
              }
            }
            return [...new Set(urls)];
          };
          const scavengePlayUrlsFromPerf = expectedId => {
            try{
              return performance.getEntriesByType('resource').map(e=>e.name).filter(value=>{
                if(!isStrongVodUrl(value)) return false;
                if(/\/media-video-|\/media-audio-/i.test(value)) return false;
                if(!expectedId) return false;
                try{
                  const id=new URL(value).searchParams.get('__vid');
                  // Only claim ownership when the CDN object is stamped for this aweme.
                  return !!id && id===String(expectedId);
                }catch{ return false; }
              });
            }catch{ return []; }
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
          // Explicit album total only (JSON image_count / list length). 0 = unknown.
          const countDouyinAlbumSlots = record => {
            let n=0;
            for(const key of ['images','image_list','imageList','image_infos','photos']){
              const block=record?.[key];
              if(Array.isArray(block)) n=Math.max(n, block.length);
              else if(Array.isArray(block?.images)) n=Math.max(n, block.images.length);
              else if(Array.isArray(block?.image_list)) n=Math.max(n, block.image_list.length);
            }
            const post=record?.image_post_info||record?.imagePost;
            if(Array.isArray(post?.images)) n=Math.max(n, post.images.length);
            if(typeof post?.image_count==='number') n=Math.max(n, post.image_count|0);
            if(typeof record?.image_count==='number') n=Math.max(n, record.image_count|0);
            if(typeof record?.imageCount==='number') n=Math.max(n, record.imageCount|0);
            return n;
          };
          // Page UI pager like "4/17" — denominator is the declared album size.
          const readAlbumPagerTotal = () => {
            let total=0;
            const consider = text => {
              if(!text) return;
              const m=String(text).trim().match(/^(\d{1,2})\s*\/\s*(\d{1,2})$/);
              if(!m) return;
              const cur=+m[1], all=+m[2];
              if(cur>=1 && all>=2 && all<=99 && cur<=all) total=Math.max(total, all);
            };
            for(const el of document.querySelectorAll('span,div,p,li,em,i,label')){
              const t=(el.childNodes.length===1 ? (el.textContent||'') : '').trim();
              if(t.length>=3 && t.length<=8) consider(t);
            }
            if(!total){
              const body=(document.body?.innerText||'').slice(0,8000);
              for(const m of body.matchAll(/(\d{1,2})\s*\/\s*(\d{1,2})/g)){
                const cur=+m[1], all=+m[2];
                if(cur>=1 && all>=2 && all<=99 && cur<=all) total=Math.max(total, all);
              }
            }
            return total;
          };
          const collectDomAlbumImageUrls = () => {
            const urls=[];
            const push=v=>{
              if(typeof v!=='string' || !/^https?:/i.test(v)) return;
              if(!/(?:byteimg|douyinpic)/i.test(v) && !/\.(jpg|jpeg|png|webp)([?#]|$)/i.test(v)) return;
              if(/avatar|emoji|emoticon|badge|logo|\/aweme-avatar\//i.test(v)) return;
              urls.push(v.split(' ')[0]);
            };
            for(const el of document.querySelectorAll('img')){
              push(el.currentSrc||el.src||'');
              push(el.getAttribute('data-src')||'');
              const ss=el.getAttribute('srcset')||'';
              for(const part of ss.split(',')) push(part.trim().split(/\s+/)[0]||'');
            }
            return [...new Set(urls)];
          };
          // Click the album "next" control so lazy slides / CDN URLs appear.
          const clickAlbumNext = () => {
            const candidates=[
              ...document.querySelectorAll('button,[role="button"],div,span')
            ].filter(el=>{
              const label=((el.getAttribute('aria-label')||'')+' '+(el.getAttribute('title')||'')).toLowerCase();
              const cls=(el.className&&typeof el.className==='string'?el.className:'').toLowerCase();
              if(/next|下一|向右|right/.test(label)) return visible(el)>0;
              if(/(?:swiper|slider|slide|carousel|note).*(?:next|right)|(?:next|right).*(?:swiper|slider|slide|arrow)/i.test(cls)
                 && visible(el)>0) return true;
              const t=(el.textContent||'').trim();
              return (t==='>' || t==='›' || t==='→') && visible(el)>0;
            });
            const btn=candidates.find(el=>{
              const r=el.getBoundingClientRect();
              return r.width>8 && r.width<120 && r.height>8 && r.height<120;
            }) || candidates[0];
            if(!btn) return false;
            try{ btn.click(); return true; }catch{ return false; }
          };
          if(!window.__vdAlbumAdvance){
            window.__vdAlbumAdvance = { last:0, clicks:0 };
            setInterval(()=>{
              try{
                if(!/\/note\//i.test(location.pathname) && !document.body?.innerText?.match(/\d+\s*\/\s*\d+/)) return;
                const now=Date.now();
                if(now-(window.__vdAlbumAdvance.last||0)<280) return;
                const pager=readAlbumPagerTotal();
                const have=collectDomAlbumImageUrls().length;
                if(pager>0 && have>=pager && window.__vdAlbumAdvance.clicks>0) return;
                if(window.__vdAlbumAdvance.clicks>=60) return;
                if(clickAlbumNext()){
                  window.__vdAlbumAdvance.last=now;
                  window.__vdAlbumAdvance.clicks++;
                }
              }catch{}
            }, 280);
          }
          const collectDouyinPlayUrls = record => {
            const urls=[];
            const push=v=>{
              if(typeof v==='string' && /^https?:/i.test(v) &&
                 !/\.(jpg|jpeg|png|webp|gif|svg)([?#]|$)/i.test(v) &&
                 !/(?:byteimg|douyinpic)\./i.test(v) &&
                 !/^blob:/i.test(v))
                urls.push(v);
              else if(v&&typeof v==='object'){
                for(const k of ['urlList','url_list','playAddr','play_addr','downloadAddr','download_addr','uri','url','play_url','playUrl']){
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
            const rates=record.video?.bit_rate||record.video?.bitRate||record.bit_rate||record.bitRate;
            if(Array.isArray(rates)) rates.forEach(r=>{ push(r); push(r?.play_addr||r?.playAddr); push(r?.download_addr||r?.downloadAddr); });
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
            try{
              const el=document.getElementById('RENDER_DATA')||document.getElementById('__NEXT_DATA__');
              if(el?.textContent) push(JSON.parse(decodeURIComponent(el.textContent)));
            }catch{}
            try{push(window._ROUTER_DATA);push(window.__INITIAL_STATE__);push(window.RENDER_DATA);}catch{}
            const seen=new WeakSet(); let budget=1800;
            const find=(value,depth)=>{
              if(!value||typeof value!=='object'||value instanceof Node||seen.has(value)||depth>14||--budget<0) return null;
              seen.add(value);
              const id = value.aweme_id||value.awemeId||value.itemId||value.item_id||value.videoId||value.video_id||value.id||value.modal_id||value.note_id||value.group_id||value.groupId;
              const hasVideo = !!(value.video||value.playAddr||value.play_addr||value.bit_rate||value.bitRate);
              if(hasVideo && String(id)===String(expectedId)) return value;
              if(Array.isArray(value)){ for(const c of value.slice(0,48)){const r=find(c,depth+1); if(r) return r;} }
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
            const recordId = record ? String(record.aweme_id||record.awemeId||record.itemId||record.item_id||record.videoId||record.video_id||record.id||'') : '';
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
            try{
              const el=document.getElementById('RENDER_DATA')||document.getElementById('__NEXT_DATA__');
              if(el?.textContent) push(JSON.parse(decodeURIComponent(el.textContent)));
            }catch{}
            try{push(window._ROUTER_DATA);push(window.__INITIAL_STATE__);push(window.RENDER_DATA);}catch{}
            // Prefer the matching aweme with the richest image list (not the first partial hit).
            let best=null, bestSlots=0;
            const seen=new WeakSet(); let budget=1800;
            const walk=(value,depth)=>{
              if(!value||typeof value!=='object'||value instanceof Node||seen.has(value)||depth>14||--budget<0) return;
              seen.add(value);
              const hasImages = !!(value.images||value.image_list||value.image_post_info||value.imagePost||value.image_infos);
              const id = value.aweme_id||value.awemeId||value.itemId||value.item_id||value.videoId||value.video_id||value.id||value.modal_id||value.note_id;
              if(hasImages && String(id)===String(expectedId)){
                const slots=countDouyinAlbumSlots(value);
                if(slots>bestSlots || (!best && slots>=0)){ best=value; bestSlots=slots; }
              }
              if(Array.isArray(value)){ for(const c of value.slice(0,64)) walk(c,depth+1); }
              else { for(const c of Object.values(value)) walk(c,depth+1); }
            };
            for(const root of roots) walk(root,0);
            record=best;
            const pagerTotal=readAlbumPagerTotal();
            const domImages=collectDomAlbumImageUrls();
            if(!record){
              if(!/\/note\//i.test(location.pathname) && pagerTotal<1 && domImages.length<1) return null;
              const images=domImages;
              if(images.length<1) return null;
              const audio=[...document.querySelectorAll('audio,video')].find(e=>/^https?:/i.test(e.currentSrc||e.src||''));
              const id=expectedId;
              const caption=(document.querySelector('[data-e2e="browse-video-desc"],.desc')?.textContent
                || document.title || '').trim().replace(/\s*[_|].*抖音.*$/u,'').trim();
              const media=[]; if(audio) media.push(audio.currentSrc||audio.src);
              // declaredImageCount only when page/JSON states a total; else omit (seal by collected).
              const out={ type:'vd-video-identity', identity: id ? (location.host+':content:'+id) : pageKey(location.href),
                caption, href:location.href, media, images, imageCount: images.length, album:true };
              if(pagerTotal>0) out.declaredImageCount=pagerTotal;
              return out;
            }
            const images=[...new Set([...collectDouyinImages(record), ...domImages])];
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
            const imageCount=images.length;
            // Only emit declared when page pager (e.g. 4/17) or JSON image_count states a total.
            // Do not treat collected URL length alone as declared — that seals "as many as we got".
            const slots=countDouyinAlbumSlots(record);
            const declared=Math.max(pagerTotal,
              (typeof record?.image_count==='number' ? record.image_count|0 : 0),
              (typeof record?.imageCount==='number' ? record.imageCount|0 : 0),
              (typeof (record?.image_post_info||record?.imagePost)?.image_count==='number'
                ? ((record.image_post_info||record.imagePost).image_count|0) : 0),
              // Full hydration lists are an authoritative total when longer than the pager-less DOM set.
              slots >= images.length ? slots : 0);
            const out={ type:'vd-video-identity', identity: id ? (location.host+':content:'+id) : pageKey(location.href),
              caption, href:location.href, media:[...new Set(audioUrls)], images, imageCount, album:true };
            if(declared>0) out.declaredImageCount=declared;
            return out;
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
            const fromFiber=scavengePlayUrlsFromPlayer(active, awemeId);
            const fromPerf=scavengePlayUrlsFromPerf(awemeId);
            const resourceKey=value=>{try{const u=new URL(value);const i=u.pathname.indexOf('/video/tos/');return i>=0 ? u.pathname.slice(i) : u.origin+u.pathname;}catch{return '';}};
            const fromPlayer=samePlayer ? [active.currentSrc,active.src,...[...active.querySelectorAll('source')].map(e=>e.src)].filter(value=>{
              if(!/^https?:/i.test(value||'')) return false;
              if(/\/media-video-|\/media-audio-/i.test(value)) return false;
              try{
                const id=new URL(value).searchParams.get('__vid');
                if(id) return id===awemeId;
              }catch{}
              if(fromData.some(u=>resourceKey(u)===resourceKey(value))) return true;
              // Active player already points at a strong CDN — keep it even without hydration.
              return isStrongVodUrl(value);
            }) : [];
            caption=dataRecord ? String(dataRecord.desc||dataRecord.description||dataRecord.title||'').trim() : (samePlayer ? caption : '');
            const durationSec=(Number.isFinite(active.duration)&&active.duration>0)?active.duration:null;
            return { type:'vd-video-identity', identity:'content:'+awemeId, caption, href:location.href,
              media:[...new Set([...fromPlayer, ...fromData, ...fromFiber, ...fromPerf])], durationSec };
          };
          window.__vdProbe=()=>{
            const observation=window.__vdObserve(); if(!observation) return null;
            const active=activePlayer();
            const record=playerRecord(active);
            const visit=(value,depth,seen,budget)=>{
              if(--budget.n<0||depth>12||value==null)return;
              if(typeof value==='string'){
                if(isStrongVodUrl(value) && !/\/media-video-|\/media-audio-/i.test(value))
                  observation.media.push(value);
                return;
              }
              if(typeof value!=='object'||value instanceof Node||seen.has(value))return;
              seen.add(value);
              for(const [key,child] of Object.entries(value)){
                if(/cover|avatar|thumbnail|subtitle|music_cover|dynamic_cover|icon|logo/i.test(key))continue;
                visit(child,depth+1,seen,budget);
              }
            };
            const observedId=(observation.identity.match(/(\d{10,})/)||[])[1];
            const recordId=record ? String(record.aweme_id||record.awemeId||record.itemId||record.videoId||record.id||'') : '';
            const dataRecord=findAwemeRecordById(observedId) || (record && observedId && recordId===observedId ? record : null);
            if(dataRecord){
              if(!observation.caption) observation.caption=String(dataRecord.desc||dataRecord.description||dataRecord.title||'').trim();
              visit(dataRecord.video||dataRecord,0,new WeakSet(),{n:1200});
              visit(dataRecord.music||{},0,new WeakSet(),{n:200});
            }
            for(const u of scavengePlayUrlsFromPlayer(active, observedId)) observation.media.push(u);
            for(const u of scavengePlayUrlsFromPerf(observedId)) observation.media.push(u);
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
