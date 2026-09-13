namespace VideoDownloader.Infrastructure.LocalLibrary;

internal static class LocalLibraryPages
{
    public const string GalleryHtml = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1"/>
<title>本地视频</title>
<style>
  :root { color-scheme: dark; --bg:#0f172a; --card:#1e293b; --muted:#94a3b8; --fg:#e2e8f0; --accent:#38bdf8; }
  * { box-sizing: border-box; }
  body { margin:0; font-family: "Segoe UI", system-ui, sans-serif; background:var(--bg); color:var(--fg); }
  header { display:flex; flex-wrap:wrap; gap:12px; align-items:center; padding:16px 20px; border-bottom:1px solid #334155; position:sticky; top:0; background:rgba(15,23,42,.92); backdrop-filter:blur(8px); z-index:2; }
  h1 { font-size:18px; margin:0; font-weight:600; }
  .tools { display:flex; gap:8px; margin-left:auto; flex-wrap:wrap; }
  button.tool { background:#334155; color:var(--fg); border:0; border-radius:8px; padding:8px 12px; cursor:pointer; font-size:13px; }
  button.tool.active { background:var(--accent); color:#0f172a; font-weight:600; }
  main { padding:16px 20px 40px; }
  .group { margin-bottom:20px; }
  .group > summary { list-style:none; cursor:pointer; font-weight:600; padding:8px 0; color:var(--accent); }
  .group > summary::-webkit-details-marker { display:none; }
  .group > summary::before { content:'▸ '; }
  .group[open] > summary::before { content:'▾ '; }
  .grid { display:grid; grid-template-columns:repeat(auto-fill,minmax(220px,1fr)); gap:14px; }
  .card { background:var(--card); border-radius:12px; overflow:hidden; border:1px solid #334155; display:flex; flex-direction:column; }
  .cover { display:block; aspect-ratio:16/10; background:#0b1220; cursor:pointer; position:relative; overflow:hidden; }
  .cover img { width:100%; height:100%; object-fit:cover; object-position:center; display:block; }
  .meta { padding:10px 12px 12px; display:flex; flex-direction:column; gap:4px; min-height:96px; }
  .row { display:flex; align-items:center; gap:6px; flex-wrap:wrap; }
  .time { color:var(--muted); font-size:12px; line-height:1.2; }
  button.edit { background:transparent; border:0; color:var(--accent); font-size:12px; line-height:1.2; padding:0; cursor:pointer; }
  button.edit:hover { text-decoration:underline; }
  select.kind { background:#0b1220; color:var(--fg); border:1px solid #475569; border-radius:4px; font-size:12px; line-height:1.2; padding:1px 4px; max-width:7.5em; }
  .name { font-size:13px; font-weight:600; word-break:break-all; }
  .name .meta-sfx { color:var(--muted); font-weight:500; }
  .caption { font-size:12px; color:#cbd5e1; word-break:break-word; display:-webkit-box; -webkit-line-clamp:2; -webkit-box-orient:vertical; overflow:hidden; }
  .empty { color:var(--muted); padding:40px 8px; text-align:center; }
  dialog { border:1px solid #475569; border-radius:12px; background:#1e293b; color:var(--fg); padding:0; width:min(440px,92vw); }
  dialog::backdrop { background:rgba(2,6,23,.65); }
  .dlg { padding:16px 18px 18px; display:flex; flex-direction:column; gap:10px; }
  .dlg h2 { margin:0; font-size:16px; }
  .dlg label { font-size:12px; color:var(--muted); display:flex; flex-direction:column; gap:4px; }
  .dlg input, .dlg textarea { background:#0b1220; color:var(--fg); border:1px solid #475569; border-radius:8px; padding:8px 10px; font:inherit; }
  .dlg textarea { min-height:72px; resize:vertical; }
  .fname-row { display:flex; align-items:center; gap:4px; flex-wrap:wrap; }
  .fname-row input { flex:1; min-width:120px; }
  .fname-row .locked { color:var(--muted); font-size:12px; word-break:break-all; }
  .dlg-actions { display:flex; gap:8px; justify-content:flex-end; margin-top:4px; }
  .dlg-actions button { background:#334155; color:var(--fg); border:0; border-radius:8px; padding:8px 12px; cursor:pointer; }
  .dlg-actions button.primary { background:var(--accent); color:#0f172a; font-weight:600; }
  .err { color:#fca5a5; font-size:12px; min-height:1em; }
</style>
</head>
<body>
<header>
  <h1>本地视频</h1>
  <div class="tools">
    <button id="btnTime" class="tool active" type="button">按时间</button>
    <button id="btnSite" class="tool" type="button">按站点分组</button>
    <button id="btnKind" class="tool" type="button">按类型分组</button>
  </div>
</header>
<main id="root"><div class="empty">加载中…</div></main>
<dialog id="editDlg">
  <form class="dlg" method="dialog" id="editForm">
    <h2>编辑卡片</h2>
    <label>文件名
      <div class="fname-row">
        <input id="editTitle" name="titleHead" autocomplete="off" required/>
        <span class="locked" id="editMeta"></span>
      </div>
    </label>
    <label>文案
      <textarea id="editCaption" name="caption"></textarea>
    </label>
    <div class="err" id="editErr"></div>
    <div class="dlg-actions">
      <button type="button" id="editCancel">取消</button>
      <button type="submit" class="primary">保存</button>
    </div>
  </form>
</dialog>
<script>
let mode = 'time';
let editingId = null;
const kinds = [
  { value:'', label:'未分类' },
  { value:'movie', label:'电影' },
  { value:'series', label:'电视剧' },
  { value:'song', label:'歌曲' },
  { value:'short', label:'小视频' },
  { value:'variety', label:'综艺' }
];
const root = document.getElementById('root');
const dlg = document.getElementById('editDlg');
document.getElementById('btnTime').onclick = () => setMode('time');
document.getElementById('btnSite').onclick = () => setMode('site');
document.getElementById('btnKind').onclick = () => setMode('kind');
document.getElementById('editCancel').onclick = () => dlg.close();
function setMode(m){
  mode = m;
  document.getElementById('btnTime').classList.toggle('active', mode==='time');
  document.getElementById('btnSite').classList.toggle('active', mode==='site');
  document.getElementById('btnKind').classList.toggle('active', mode==='kind');
  load();
}
function esc(s){ return String(s??'').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }
function kindOptions(selected){
  return kinds.map(k => `<option value="${esc(k.value)}"${(selected||'')===k.value?' selected':''}>${esc(k.label)}</option>`).join('');
}
function card(item){
  const meta = item.metaSuffix || '';
  const ext = item.extension || '';
  return `<article class="card" data-id="${esc(item.id)}">
    <a class="cover" href="${esc(item.playUrl)}" title="播放">
      <img src="${esc(item.thumbUrl)}" alt="" loading="lazy" decoding="async" onerror="this.style.opacity=.25"/>
    </a>
    <div class="meta">
      <div class="row">
        <span class="time">${esc(item.downloadedAtText)}</span>
        <button type="button" class="edit" data-edit="${esc(item.id)}">编辑</button>
        <select class="kind" data-kind="${esc(item.id)}" title="视频类型">${kindOptions(item.videoKind)}</select>
      </div>
      <div class="name">${esc(item.titleHead)}<span class="meta-sfx">${esc(meta)}${esc(ext)}</span></div>
      <div class="caption">${esc(item.caption)}</div>
    </div>
  </article>`;
}
function bindCardEvents(scope){
  scope.querySelectorAll('button.edit').forEach(btn => {
    btn.onclick = () => openEdit(btn.getAttribute('data-edit'));
  });
  scope.querySelectorAll('select.kind').forEach(sel => {
    sel.onchange = async () => {
      const id = sel.getAttribute('data-kind');
      try {
        await postEdit({ id, videoKind: sel.value });
        if (mode === 'kind') load();
      } catch (e) {
        alert(e.message || String(e));
        load();
      }
    };
  });
}
async function openEdit(id){
  const res = await fetch('/api/item?id='+encodeURIComponent(String(id).replace(/-/g,'')));
  if(!res.ok){ alert('无法加载条目'); return; }
  const item = await res.json();
  editingId = item.id;
  document.getElementById('editTitle').value = item.titleHead || '';
  document.getElementById('editMeta').textContent = (item.metaSuffix || '') + (item.extension || '');
  document.getElementById('editCaption').value = item.caption || '';
  document.getElementById('editErr').textContent = '';
  dlg.showModal();
  document.getElementById('editTitle').focus();
}
document.getElementById('editForm').onsubmit = async (e) => {
  e.preventDefault();
  const titleHead = document.getElementById('editTitle').value;
  const caption = document.getElementById('editCaption').value;
  const err = document.getElementById('editErr');
  err.textContent = '';
  try {
    await postEdit({ id: editingId, titleHead, caption });
    dlg.close();
    load();
  } catch (ex) {
    err.textContent = ex.message || String(ex);
  }
};
async function postEdit(body){
  const res = await fetch('/api/edit', {
    method:'POST',
    headers:{ 'Content-Type':'application/json' },
    body: JSON.stringify(body)
  });
  if(!res.ok){
    const t = await res.text();
    throw new Error(t || ('HTTP '+res.status));
  }
  return res.json();
}
async function load(){
  root.innerHTML = '<div class="empty">加载中…</div>';
  const q = mode==='site' ? '?group=site' : (mode==='kind' ? '?group=kind' : '');
  const res = await fetch('/api/videos'+q);
  const data = await res.json();
  if(mode==='site' || mode==='kind'){
    if(!data.length){ root.innerHTML='<div class="empty">暂无已完成的本地下载</div>'; return; }
    // Non-time groups stay collapsed by default (no open attribute).
    root.innerHTML = data.map(g => `<details class="group">
      <summary>${esc(g.group)}（${g.items.length}）</summary>
      <div class="grid">${g.items.map(card).join('')}</div>
    </details>`).join('');
  } else {
    if(!data.length){ root.innerHTML='<div class="empty">暂无已完成的本地下载</div>'; return; }
    root.innerHTML = `<div class="grid">${data.map(card).join('')}</div>`;
  }
  bindCardEvents(root);
}
load();
</script>
</body>
</html>
""";

    public const string PlayerHtml = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1"/>
<title>播放</title>
<style>
  :root { color-scheme: dark; --bg:#0f172a; --fg:#e2e8f0; --muted:#94a3b8; --accent:#38bdf8; }
  body { margin:0; font-family:"Segoe UI",system-ui,sans-serif; background:var(--bg); color:var(--fg); }
  header { display:flex; gap:12px; align-items:center; padding:12px 16px; border-bottom:1px solid #334155; }
  a { color:var(--accent); text-decoration:none; }
  .nav-hint { margin-left:auto; color:var(--muted); font-size:12px; }
  .wrap { max-width:1100px; margin:0 auto; padding:16px; position:relative; }
  .stage { position:relative; }
  video { width:100%; max-height:75vh; background:#000; border-radius:10px; display:block; }
  .arrow {
    position:absolute; top:50%; transform:translateY(-50%);
    width:44px; height:44px; border:0; border-radius:999px;
    background:rgba(15,23,42,.72); color:var(--fg); font-size:22px; cursor:pointer;
    display:flex; align-items:center; justify-content:center;
    z-index:2;
  }
  .arrow:hover { background:rgba(56,189,248,.9); color:#0f172a; }
  .arrow:disabled { opacity:.25; cursor:default; }
  .arrow.prev { left:10px; }
  .arrow.next { right:10px; }
  .meta { margin-top:12px; display:flex; flex-direction:column; gap:6px; }
  .time { color:var(--muted); font-size:13px; }
  .name { font-weight:600; }
  .caption { color:#cbd5e1; white-space:pre-wrap; }
  .pos { color:var(--muted); font-size:12px; margin-top:4px; }
</style>
</head>
<body>
<header>
  <a href="/">← 本地视频</a>
  <strong id="title">播放</strong>
  <span class="nav-hint">滚轮或 ← → 切换</span>
</header>
<div class="wrap">
  <div class="stage" id="stage">
    <button type="button" class="arrow prev" id="btnPrev" title="上一条" aria-label="上一条">‹</button>
    <video id="player" controls autoplay playsinline></video>
    <button type="button" class="arrow next" id="btnNext" title="下一条" aria-label="下一条">›</button>
  </div>
  <div class="meta">
    <div class="time" id="time"></div>
    <div class="name" id="name"></div>
    <div class="caption" id="caption"></div>
    <div class="pos" id="pos"></div>
  </div>
</div>
<script>
let playlist = [];
let index = -1;
let wheelLock = 0;
const norm = id => String(id||'').replace(/-/g,'').toLowerCase();
const currentId = norm(location.pathname.split('/').filter(Boolean).pop());

function go(delta){
  if(index < 0 || !playlist.length) return;
  const next = index + delta;
  if(next < 0 || next >= playlist.length) return;
  const item = playlist[next];
  if(!item?.playUrl) return;
  location.href = item.playUrl;
}

function syncButtons(){
  document.getElementById('btnPrev').disabled = index <= 0;
  document.getElementById('btnNext').disabled = index < 0 || index >= playlist.length - 1;
  if(index >= 0)
    document.getElementById('pos').textContent = (index+1) + ' / ' + playlist.length;
}

async function boot(){
  const [itemRes, listRes] = await Promise.all([
    fetch('/api/item?id='+currentId),
    fetch('/api/videos')
  ]);
  if(!itemRes.ok){
    document.getElementById('name').textContent='文件不存在或未完成';
    syncButtons();
    return;
  }
  const item = await itemRes.json();
  document.getElementById('title').textContent = item.fileName || '播放';
  document.getElementById('time').textContent = item.downloadedAtText || '';
  document.getElementById('name').textContent = item.fileName || '';
  document.getElementById('caption').textContent = item.caption || '';
  const v = document.getElementById('player');
  v.src = item.streamUrl;

  playlist = listRes.ok ? await listRes.json() : [];
  if(!Array.isArray(playlist)) playlist = [];
  index = playlist.findIndex(x => norm(x.id) === currentId || norm(x.playUrl?.split('/').pop()) === currentId);
  syncButtons();
}

document.getElementById('btnPrev').onclick = () => go(-1);
document.getElementById('btnNext').onclick = () => go(1);

document.addEventListener('keydown', e => {
  if(e.target && (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA' || e.target.tagName === 'SELECT')) return;
  if(e.key === 'ArrowLeft' || e.key === 'ArrowUp'){ e.preventDefault(); go(-1); }
  if(e.key === 'ArrowRight' || e.key === 'ArrowDown'){ e.preventDefault(); go(1); }
});

window.addEventListener('wheel', e => {
  const now = Date.now();
  if(now < wheelLock) return;
  if(Math.abs(e.deltaY) < 20) return;
  wheelLock = now + 450;
  e.preventDefault();
  go(e.deltaY > 0 ? 1 : -1);
}, { passive: false });

boot();
</script>
</body>
</html>
""";
}
