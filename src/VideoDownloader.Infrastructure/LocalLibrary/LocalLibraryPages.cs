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
  .tools { display:flex; gap:8px; margin-left:auto; }
  button { background:#334155; color:var(--fg); border:0; border-radius:8px; padding:8px 12px; cursor:pointer; }
  button.active { background:var(--accent); color:#0f172a; font-weight:600; }
  main { padding:16px 20px 40px; }
  .group { margin-bottom:20px; }
  .group > summary { list-style:none; cursor:pointer; font-weight:600; padding:8px 0; color:var(--accent); }
  .group > summary::-webkit-details-marker { display:none; }
  .grid { display:grid; grid-template-columns:repeat(auto-fill,minmax(220px,1fr)); gap:14px; }
  .card { background:var(--card); border-radius:12px; overflow:hidden; border:1px solid #334155; display:flex; flex-direction:column; }
  .cover { display:block; aspect-ratio:16/9; background:#0b1220; cursor:pointer; position:relative; }
  .cover img { width:100%; height:100%; object-fit:cover; display:block; }
  .meta { padding:10px 12px 12px; display:flex; flex-direction:column; gap:4px; min-height:88px; }
  .time { color:var(--muted); font-size:12px; }
  .name { font-size:13px; font-weight:600; word-break:break-all; }
  .caption { font-size:12px; color:#cbd5e1; word-break:break-word; display:-webkit-box; -webkit-line-clamp:2; -webkit-box-orient:vertical; overflow:hidden; }
  .empty { color:var(--muted); padding:40px 8px; text-align:center; }
</style>
</head>
<body>
<header>
  <h1>本地视频</h1>
  <div class="tools">
    <button id="btnTime" class="active" type="button">按时间</button>
    <button id="btnGroup" type="button">按站点分组</button>
  </div>
</header>
<main id="root"><div class="empty">加载中…</div></main>
<script>
let mode = 'time';
const root = document.getElementById('root');
document.getElementById('btnTime').onclick = () => { mode='time'; sync(); load(); };
document.getElementById('btnGroup').onclick = () => { mode='group'; sync(); load(); };
function sync(){
  document.getElementById('btnTime').classList.toggle('active', mode==='time');
  document.getElementById('btnGroup').classList.toggle('active', mode==='group');
}
function esc(s){ return String(s??'').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }
function card(item){
  return `<article class="card">
    <a class="cover" href="${esc(item.playUrl)}" title="播放">
      <img src="${esc(item.thumbUrl)}" alt="" loading="lazy" onerror="this.style.opacity=.25"/>
    </a>
    <div class="meta">
      <div class="time">${esc(item.downloadedAtText)}</div>
      <div class="name">${esc(item.fileName)}</div>
      <div class="caption">${esc(item.caption)}</div>
    </div>
  </article>`;
}
async function load(){
  root.innerHTML = '<div class="empty">加载中…</div>';
  const q = mode==='group' ? '?group=site' : '';
  const res = await fetch('/api/videos'+q);
  const data = await res.json();
  if(mode==='group'){
    if(!data.length){ root.innerHTML='<div class="empty">暂无已完成的本地下载</div>'; return; }
    root.innerHTML = data.map(g => `<details class="group" open>
      <summary>${esc(g.group)}（${g.items.length}）</summary>
      <div class="grid">${g.items.map(card).join('')}</div>
    </details>`).join('');
  } else {
    if(!data.length){ root.innerHTML='<div class="empty">暂无已完成的本地下载</div>'; return; }
    root.innerHTML = `<div class="grid">${data.map(card).join('')}</div>`;
  }
}
load();
setInterval(() => { if(document.visibilityState==='visible') load(); }, 15000);
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
  .wrap { max-width:1100px; margin:0 auto; padding:16px; }
  video { width:100%; max-height:75vh; background:#000; border-radius:10px; }
  .meta { margin-top:12px; display:flex; flex-direction:column; gap:6px; }
  .time { color:var(--muted); font-size:13px; }
  .name { font-weight:600; }
  .caption { color:#cbd5e1; white-space:pre-wrap; }
</style>
</head>
<body>
<header><a href="/">← 本地视频</a><strong id="title">播放</strong></header>
<div class="wrap">
  <video id="player" controls autoplay playsinline></video>
  <div class="meta">
    <div class="time" id="time"></div>
    <div class="name" id="name"></div>
    <div class="caption" id="caption"></div>
  </div>
</div>
<script>
const id = location.pathname.split('/').filter(Boolean).pop();
async function boot(){
  const res = await fetch('/api/item?id='+id);
  if(!res.ok){ document.getElementById('name').textContent='文件不存在或未完成'; return; }
  const item = await res.json();
  document.getElementById('title').textContent = item.fileName || '播放';
  document.getElementById('time').textContent = item.downloadedAtText || '';
  document.getElementById('name').textContent = item.fileName || '';
  document.getElementById('caption').textContent = item.caption || '';
  const v = document.getElementById('player');
  v.src = item.streamUrl;
}
boot();
</script>
</body>
</html>
""";
}
