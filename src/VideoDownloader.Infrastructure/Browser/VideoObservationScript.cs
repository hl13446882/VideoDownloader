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
          const collectImageUrls = record => {
            const urls=[];
            const push=v=>{
              if(typeof v==='string' && /^https?:/i.test(v) &&
                 (/\.(jpg|jpeg|png|webp)([?#]|$)/i.test(v) ||
                  /(?:byteimg|douyinpic|tiktokcdn).*\/(?:tos-|obj\/|image)/i.test(v)))
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
          const observeAlbumPost = () => {
            // Douyin/TikTok photo mode: carousel images + BGM, often no <video>.
            let record=null;
            const currentRecord=playerRecord(activePlayer());
            const expectedId=(location.search.match(/modal_id=(\d{10,})/)||[])[1]
              || (location.pathname.match(/\/(?:video|note)\/(\d{10,})/)||[])[1]
              || String(currentRecord?.aweme_id||currentRecord?.itemId||currentRecord?.videoId||currentRecord?.id||'');
            if(!expectedId) return null;
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
            const seen=new WeakSet(); let budget=900;
            const find=(value,depth)=>{
              if(!value||typeof value!=='object'||value instanceof Node||seen.has(value)||depth>12||--budget<0) return null;
              seen.add(value);
              const hasImages = !!(value.images||value.image_list||value.image_post_info||value.imagePost||value.image_infos);
              const hasMusic = !!(value.music||value.audio||value.playAddr||value.play_addr);
              const id = value.aweme_id||value.itemId||value.videoId||value.id||value.modal_id||value.note_id;
              if(hasImages && String(id)===expectedId && (hasMusic||id) && (value.desc||value.description||value.title||id)) return value;
              if(Array.isArray(value)){
                for(const c of value.slice(0,40)){const r=find(c,depth+1); if(r) return r;}
              }else{
                for(const c of Object.values(value)){const r=find(c,depth+1); if(r) return r;}
              }
              return null;
            };
            for(const root of roots){ record=find(root,0); if(record) break; }
            if(!record){
              if(!/\/note\//i.test(location.pathname)) return null;
              // Explicit note pages may expose only DOM stills and BGM.
              const imgs=[...document.querySelectorAll('img')].filter(e=>{
                const r=e.getBoundingClientRect();
                const src=e.currentSrc||e.src||'';
                return r.width>120 && r.height>120 && visible(e)>0 && /^https?:/i.test(src) &&
                  !/avatar|emoji|emoticon|badge|logo/i.test(src);
              }).slice(0,24);
              const audio=[...document.querySelectorAll('audio,video')].find(e=>
                /^https?:/i.test(e.currentSrc||e.src||'') &&
                (e.tagName==='AUDIO' || (e.tagName==='VIDEO' && (e.duration>0 || e.seekable?.length))));
              if(imgs.length<2 && !/\/note\//i.test(location.pathname)) return null;
              if(imgs.length<1) return null;
              const modal=(location.search.match(/modal_id=(\d{10,})/)||[])[1];
              const pathId=(location.pathname.match(/\/(?:video|note)\/(\d{10,})/)||[])[1];
              const id=modal||pathId;
              if(!id && !/douyin|tiktok/i.test(location.host)) return null;
              const caption=(document.querySelector('[data-e2e="browse-video-desc"],[data-e2e="video-desc"],[data-e2e="detail-desc"],[data-e2e="note-desc"],.desc')?.textContent
                || document.title || '').trim().replace(/\s*[_|].*抖音.*$/u,'').trim();
              const media=[];
              if(audio) media.push(audio.currentSrc||audio.src);
              return {
                type:'vd-video-identity',
                identity: id ? (location.host+':content:'+id) : pageKey(location.href),
                caption,
                href:location.href,
                media,
                images:imgs.map(e=>e.currentSrc||e.src),
                album:true
              };
            }
            const images=collectImageUrls(record);
            if(images.length<2 && !/\/note\//i.test(location.pathname)) return null;
            if(images.length<1) return null;
            const music=record.music||{};
            const audioUrls=[];
            const pushAudio=v=>{
              if(typeof v==='string' && /^https?:/i.test(v) && !/\.(jpg|jpeg|png|webp|gif)([?#]|$)/i.test(v)) audioUrls.push(v);
              else if(v&&typeof v==='object'){
                for(const k of ['playUrl','play_url','uri','url','urlList','url_list']){
                  const c=v[k];
                  if(Array.isArray(c)) c.forEach(pushAudio);
                  else pushAudio(c);
                }
              }
            };
            pushAudio(music.play_url||music.playUrl||music);
            pushAudio(record.playAddr||record.play_addr);
            const id=String(record.aweme_id||record.itemId||record.videoId||record.id||'');
            const caption=String(record.desc||record.description||record.title||'').trim();
            if(!id && audioUrls.length===0) return null;
            return {
              type:'vd-video-identity',
              identity: id ? (location.host+':content:'+id) : pageKey(location.href),
              caption,
              href:location.href,
              media:[...new Set(audioUrls.filter(u=>/^https?:/i.test(u)))],
              images,
              album:true
            };
          };
          const observeDocumentMeta = () => {
            if(!/bilibili\.com/i.test(location.host)) return null;
            const bvid=(location.pathname.match(/\/video\/(BV[\w]+)/i)||[])[1]
              || document.querySelector('[data-bvid]')?.getAttribute('data-bvid');
            if(!bvid) return null;
            const pick=(...nodes)=> {
              for(const n of nodes){
                if(!n) continue;
                const t=(n.getAttribute?.('title')||n.textContent||'').trim();
                if(t) return t;
              }
              return '';
            };
            let caption=pick(
              document.querySelector('h1.video-title'),
              document.querySelector('.video-info-title h1'),
              document.querySelector('.video-info-title'),
              document.querySelector('h1[title]'),
              document.querySelector('#viewbox_report h1'),
              document.querySelector('.tit'),
              document.querySelector('meta[property="og:title"]'));
            if(!caption && document.querySelector('meta[property="og:title"]'))
              caption=(document.querySelector('meta[property="og:title"]').getAttribute('content')||'').trim();
            if(!caption){
              caption=(document.title||'').replace(/\s*[_|].*哔哩哔哩.*$/u,'').replace(/\s*[_-]\s*bilibili.*$/i,'').trim();
            }
            return {
              type:'vd-video-identity',
              identity: location.host+':content:'+bvid,
              caption,
              href:location.href,
              media:[],
              durationSec:(()=>{const v=[...document.querySelectorAll('video')].find(e=>{const r=e.getBoundingClientRect();return r.width>80&&r.height>80;}); return (v&&Number.isFinite(v.duration)&&v.duration>0)?v.duration:null;})()
            };
          };
          window.__vdObserve = () => {
            // Note / album posts may still mount a tiny <video> for BGM; prefer album when multiple stills exist.
            const albumEarly = observeAlbumPost();
            if (albumEarly?.images?.length > 1 ||
                (albumEarly?.images?.length > 0 && /\/note\//i.test(location.pathname)))
              return albumEarly;
            const active = activePlayer();
            if (!active) {
              const bili = observeDocumentMeta();
              if(bili) return bili;
              if(window.player_aaaa || window.player_data || window.MacPlayer){
                const player=window.player_aaaa||window.player_data||{};
                const heading=document.querySelector('.player-title,h2.title,.title h2,h2,h1');
                const caption=String(player.vod_data?.vod_name||heading?.textContent||document.title||'').trim();
                return {type:'vd-video-identity',identity:pageKey(location.href),caption,href:location.href,media:[],durationSec:null};
              }
              return null;
            }
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
              const desc = scope.querySelector('[data-e2e="browse-video-desc"],[data-e2e="video-desc"],[data-e2e="detail-desc"],[data-e2e="new-desc-span"],[data-e2e="video-meta-caption"],[data-video-caption],figcaption,h1.video-title,.video-info-title,h1[title],.tit,.video-title');
              if (desc && !caption) caption = (desc.getAttribute('title')||desc.textContent||'').trim();
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
              const card=active.closest('[data-e2e="feed-active-video"],[data-e2e="feed-item"],[data-e2e="recommend-list-item-container"],[data-e2e="feed-video"],article,#one-column-item-0,.video-info-container,.video-info')
                || active.closest('section')?.parentElement;
              const desc=card?.querySelector('[data-e2e="browse-video-desc"],[data-e2e="video-desc"],[data-e2e="detail-desc"],[data-e2e="new-desc-span"],[data-e2e="video-meta-caption"],[data-e2e="browse-video-desc-new"],h1.video-title,.video-info-title,h1[title],.tit');
              if(desc) caption=(desc.getAttribute('title')||desc.textContent||'').trim().replace(/(?:展开|收起|See more|See less)\s*$/i,'').trim();
            }
            if(!caption){
              const h1=document.querySelector('h1.video-title,.video-info-title,h1[title]');
              if(h1) caption=(h1.getAttribute('title')||h1.textContent||'').trim();
            }
            // Transport addresses deliberately never participate in logical identity.
            const stablePage = ids.some(k => new URL(location.href).searchParams.has(k)) || /\/(video|shorts|note)\/[^/]+/.test(location.pathname);
            // Feed roots without a concrete content id are not a stable logical video yet.
            if(!stablePage && !explicit) return null;
            const identity = stablePage ? base : location.host + ':' + explicit;
            const durationSec=(Number.isFinite(active.duration)&&active.duration>0)?active.duration:null;
            return { type:'vd-video-identity', identity, caption, href:location.href, durationSec,
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
                if(/^https?:\/\//i.test(value) &&
                   !/\.(jpg|jpeg|png|webp|gif|svg)([?#]|$)/i.test(value) &&
                   !/(?:byteimg|douyinpic)\./i.test(value) &&
                   !/\/(?:emoticon|aweme-image|obj\/tos-cn-i-)/i.test(value))
                  observation.media.push(value);
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
              visit(record.music||{},0);
              visit(record.bit_rate||record.bitrateInfo||{},0);
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
                visit(byId.music||{},0);
                visit(byId.bit_rate||byId.bitrateInfo||{},0);
              }
            }
            // Always harvest album stills when present on the same identity.
            if(!observation.images?.length){
              const album=observeAlbumPost();
              if(album?.images?.length && album.identity===observation.identity){
                observation.images=album.images;
                observation.album=true;
                for(const u of album.media||[]) observation.media.push(u);
              }
            }
            observation.media=[...new Set(observation.media)];
            // Bilibili DASH playinfo (video+audio baseUrl) when yt-dlp is blocked by 412.
            try{
              const playinfo=window.__playinfo__?.data||window.__playinfo__||
                window.__INITIAL_STATE__?.videoData?.playInfo||
                window.__INITIAL_STATE__?.vp?.dash;
              const dash=playinfo?.dash||playinfo?.result?.dash||playinfo;
              const pushDash=list=>{
                if(!Array.isArray(list)) return;
                for(const item of list.slice(0,12)){
                  const u=item?.baseUrl||item?.base_url||item?.backupUrl?.[0]||item?.backup_url?.[0];
                  if(typeof u==='string' && /\.(m3u8|mpd|mp4|webm|m4a|mp3)([?#]|$)/i.test(u)) observation.media.push(u);
                }
              };
              if(dash){ pushDash(dash.video); pushDash(dash.audio); }
            }catch{}
            // MacCMS / generic player bootstrap (player_aaaa / MacPlayer / parse iframe).
            try{
              const player=window.player_aaaa||window.player_data||null;
              if(player){
                if(!observation.caption && player.vod_data?.vod_name)
                  observation.caption=String(player.vod_data.vod_name).trim();
                if(!observation.identity && player.id)
                  observation.identity=location.host+':content:'+player.id;
                for(const key of ['url']){
                  const u=player[key];
                  if(typeof u==='string' && /\.(m3u8|mpd|mp4|webm|m4a|mp3)([?#]|$)/i.test(u)) observation.media.push(u);
                }
              }
              if(window.MacPlayer){
                if(typeof MacPlayer.PlayUrl==='string' && /\.(m3u8|mpd|mp4|webm|m4a|mp3)([?#]|$)/i.test(MacPlayer.PlayUrl))
                  observation.media.push(MacPlayer.PlayUrl);
              }
            }catch{}
            // DPlayer + hls.js (MSE/blob): harvest playlist URL only, never page copy.
            try{
              const pushMedia=u=>{
                if(typeof u==='string' && /\.(m3u8|mpd|mp4|webm|m4a|mp3)([?#]|$)/i.test(u))
                  observation.media.push(u);
              };
              pushMedia(window.dp?.options?.video?.url);
              for(const el of Array.from(document.querySelectorAll('.dplayer')).slice(0,8)){
                const inst=el.dplayer||el.__dplayer||el._dplayer;
                pushMedia(inst?.options?.video?.url);
              }
              for(const entry of performance.getEntriesByType('resource'))
                pushMedia(entry?.name);
            }catch{}
            if(!observation.caption){
              const h=document.querySelector('h1,h2.title,.title h2,.player-title,h2');
              if(h) observation.caption=(h.textContent||'').trim().replace(/\s+/g,' ').slice(0,120);
            }
            // Generic sites: document.title is an allowed caption fallback.
            if(!observation.caption){
              const t=String(document.title||'').trim().replace(/\s+/g,' ');
              if(t && !/\.(m4s|ts|m3u8|mpd|flv)([?#]|$)/i.test(t)) observation.caption=t.slice(0,160);
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
