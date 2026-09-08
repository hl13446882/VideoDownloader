const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const root = path.resolve(__dirname, '..');
const source = fs.readFileSync(path.join(root, 'src/VideoDownloader.Infrastructure/Browser/MediaAddressDiscoveryScript.cs'), 'utf8');
const expression = source.match(/Expression = """([\s\S]*?)""";/)[1];
function doc(media, scripts = [], frames = []) {
  return {baseURI: 'https://article.test/archives/123/', querySelectorAll(selector) {
    if (selector.startsWith('video')) return media;
    if (selector.startsWith('script')) return scripts.map(textContent => ({textContent}));
    return frames;
  }};
}
const child = doc([{src:'https://cdn.test/audio.m4a'}]);
const document = doc([{currentSrc:'https://cdn.test/video.mp4'}, {src:'blob:internal'}],
  [JSON.stringify({video: {url:'https://cdn.test/master.m3u8', title:'untouched'}}), 'not JSON'],
  [{contentDocument:child}, {get contentDocument() {throw new Error('cross-origin');}}]);
const result = JSON.parse(JSON.stringify(vm.runInNewContext(expression, {document, URL}, {timeout:1000})));
assert.deepEqual(result, [
  {url:'https://cdn.test/video.mp4'}, {url:'https://cdn.test/master.m3u8'}, {url:'https://cdn.test/audio.m4a'}
]);
const bounded = vm.runInNewContext(expression, {document:doc(Array.from({length:1000}, (_,i) => ({src:`https://cdn.test/${i}.mp4` }))), URL}, {timeout:1000});
assert.equal(bounded.length, 64);
for (const [file, expected] of [
  ['src/VideoDownloader.Infrastructure/Browser/VideoObservationScript.cs', '0E6397FF8B345547896D57A8A550694F43638A8BD4828BE5D53E7EA4527489FD'],
  ['src/VideoDownloader.Core/Naming/DownloadFileNameBuilder.cs', '33D9B77B0236BC190D44FD6F984B3ECF7BBF3D2C462A002957B63D2FA07F106D']
]) assert.equal(crypto.createHash('sha256').update(fs.readFileSync(path.join(root,file))).digest('hex').toUpperCase(), expected);
console.log('PASS: address extraction, no identity requirement, nested frame, malformed JSON, candidate bound, caption/naming hashes unchanged.');

// Exercise the observation script, including pages whose player lives in an iframe.
const observationSource=fs.readFileSync(path.join(root,'src/VideoDownloader.Infrastructure/Browser/VideoObservationScript.cs'),'utf8');
const install=observationSource.match(/Install = """([\s\S]*?)""";/)[1];
function observe(url, globals={}) {
  const document={title:'Current film',querySelector:()=>null,querySelectorAll:()=>[],getElementById:()=>null,addEventListener:()=>{}};
  const context={document,location:new URL(url),URL,Node:class{},HTMLMediaElement:class{},
    MutationObserver:class{observe(){}},queueMicrotask:()=>{},setInterval:()=>{},...globals};
  context.window=context;
  vm.runInNewContext(install,context,{timeout:1000});
  return context.__vdProbe();
}
const generic=observe('https://film.test/vod/play/id/100/sid/1/nid/1.html',{
  player_aaaa:{url:'https://cdn.test/current.m3u8',url_next:'https://cdn.test/next.m3u8',vod_data:{vod_name:'Current film'}}
});
assert.equal(generic.caption,'Current film');
assert.ok(generic.identity.includes('/id/100/'));
assert.ok(generic.media.includes('https://cdn.test/current.m3u8'));
assert.ok(!generic.media.includes('https://cdn.test/next.m3u8'));
const album={aweme_id:'123456789012345',desc:'Album',images:[
  {url_list:['https://img.test/one.jpg','https://backup.test/one.jpg']},
  {url_list:['https://img.test/two.jpg','https://backup.test/two.jpg']}
],music:{play_url:{url_list:['https://cdn.test/music.mp3']}}};
assert.equal(observe('https://www.douyin.com/?recommend=1',{__INITIAL_STATE__:{album}}),null);
assert.equal(observe('https://www.douyin.com/jingxuan?modal_id=999999999999999',{__INITIAL_STATE__:{album}}),null);
const note=observe('https://www.douyin.com/note/123456789012345',{__INITIAL_STATE__:{album}});
assert.equal(note.images.length,2);
assert.equal(note.caption,'Album');
console.log('PASS: iframe player metadata, next-item exclusion, album ownership and backup-image deduplication.');
