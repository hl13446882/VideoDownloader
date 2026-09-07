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
  ['src/VideoDownloader.Infrastructure/Browser/VideoObservationScript.cs', '14D26CC36EB9AB65A9DB151182A41615C4EC0D9635E11A402DEF40CDADF8E690'],
  ['src/VideoDownloader.Core/Naming/DownloadFileNameBuilder.cs', 'D7765C87861AAFAB7FBED7DC487E27059D98124F85A1C262AE991530851B60F8']
]) assert.equal(crypto.createHash('sha256').update(fs.readFileSync(path.join(root,file))).digest('hex').toUpperCase(), expected);
console.log('PASS: address extraction, no identity requirement, nested frame, malformed JSON, candidate bound, caption/naming hashes unchanged.');
