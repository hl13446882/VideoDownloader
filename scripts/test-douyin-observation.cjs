const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../src/VideoDownloader.Infrastructure/Browser/DouyinObservationScript.cs'), 'utf8');
const body = source.split('"""')[1];
const currentId = '7674888187625458982', adId = '7670164200798342410';
const record = (id, url) => ({ aweme_id: id, desc: id, video: { play_addr: { url_list: [url] } } });
function observe({ hydration=true, domId=null, page=true }={}) {
  class Node {}
  class Media extends Node {}
  const active = new Media();
  Object.assign(active, { tagName:'VIDEO', paused:false, duration:20, src:'https://v3.douyinvod.com/ad.mp4', currentSrc:'https://v3.douyinvod.com/ad.mp4',
    getBoundingClientRect:()=>({top:0,left:0,bottom:500,right:500}),
    getAttribute: key => key==='data-aweme-id' ? domId : null,
    querySelector:()=>null, querySelectorAll:()=>[], parentElement:null,
    '__reactProps$test': { item:record(adId,'https://v3.douyinvod.com/ad.mp4') } });
  const location = new URL('https://www.douyin.com/jingxuan' + (page ? '?modal_id='+currentId : ''));
  const context = { Node, HTMLMediaElement:Media, URL, location, innerHeight:1000,innerWidth:1000,
    document:{querySelectorAll:()=>[active],getElementById:()=>null,addEventListener:()=>{}},
    getComputedStyle:()=>({visibility:'visible',display:'block',opacity:'1'}),
    MutationObserver:class{observe(){}},setInterval:()=>{},queueMicrotask:()=>{} };
  context.window = hydration ? {__UNIVERSAL_DATA_FOR_REHYDRATION__:record(currentId,'https://v3.douyinvod.com/current.mp4')} : {};
  vm.createContext(context); vm.runInContext(body,context);
  return JSON.parse(JSON.stringify(context.window.__vdProbe()));
}
let result=observe();
assert.equal(result.identity,'content:'+currentId);
assert.deepEqual(result.media,['https://v3.douyinvod.com/current.mp4']);
result=observe({hydration:false});
assert.equal(result.identity,'content:'+currentId);
assert.deepEqual(result.media,[]);
assert.equal(result.caption,'');
// Feed without a fixed permalink follows the active player's own work.
result=observe({hydration:false,page:false});
assert.equal(result.identity,'content:'+adId);
assert.deepEqual(result.media,['https://v3.douyinvod.com/ad.mp4']);
console.log('Douyin observation: fixed-page/ad mismatch, missing data, active-feed identity passed.');
