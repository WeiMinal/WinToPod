// SPDX-License-Identifier: GPL-3.0-or-later
// Deterministic lifecycle/timing test. This is not a browser or codec execution test.
import vm from 'node:vm';
import fs from 'node:fs';
import assert from 'node:assert/strict';
const script=fs.readFileSync(new URL('../browser/WinToPod.VideoSync/content.js',import.meta.url),'utf8');
const passed=[];
async function environment(options={}) {
  let now=100, seq=0, frames=0, released=0, closed=0, drawn=[], canvases=[], jobs=new Map(), events={};
  const state={connected:true,delayMs:400,...options.state};
  const video={videoWidth:640,videoHeight:360,clientWidth:640,clientHeight:360,offsetLeft:0,offsetTop:0,readyState:4,paused:false,ended:false,controls:!!options.controls,mediaKeys:options.drm?{}:null,addEventListener:(n,f)=>events[n]=f,removeEventListener:n=>delete events[n]};
  const parent={style:{},insertBefore(c){canvases.push(c);}};video.parentElement=parent;
  class Frame { constructor(v,o){this.timestamp=o.timestamp;frames++;} close(){released++;} }
  class Encoder {constructor(c){this.c=c;this.encodeQueueSize=0;}configure(){} encode(f){this.c.output({timestamp:f.timestamp,byteLength:options.large?20000000:100},{decoderConfig:{codec:'vp8'}});}close(){closed++;} }
  class Decoder {constructor(c){this.c=c;this.state='unconfigured';this.decodeQueueSize=0;}configure(){this.state='configured';}decode(c){const f=new Frame(null,{timestamp:c.timestamp});this.c.output(f);}close(){closed++;} }
  const context={chrome:{runtime:{sendMessage:async()=>({...state})}},performance:{now:()=>now},VideoFrame:Frame,VideoEncoder:Encoder,VideoDecoder:Decoder,
    document:{hidden:false,createElement:()=>{const c={style:{},dataset:{},setAttribute(){},getContext:()=>({drawImage:f=>drawn.push({at:now,source:f.timestamp/1000})}),remove(){canvases=canvases.filter(x=>x!==c);}};return c;},querySelectorAll:()=>[video]},getComputedStyle:()=>({position:'static',objectFit:'contain'}),
    setTimeout:(f,ms)=>{const id=++seq;jobs.set(id,{f,at:now+ms});return id;},clearTimeout:id=>jobs.delete(id),requestAnimationFrame:f=>context.setTimeout(()=>f(now),16),cancelAnimationFrame:id=>jobs.delete(id),addEventListener:(n,f)=>events[n]=f};
  if(options.noCodec) delete context.VideoEncoder;
  vm.runInNewContext(script,context);await Promise.resolve();await Promise.resolve();
  async function advance(ms) {const end=now+ms;while(true){const item=[...jobs].sort((a,b)=>a[1].at-b[1].at)[0];if(!item||item[1].at>end)break;jobs.delete(item[0]);now=item[1].at;item[1].f();await Promise.resolve();await Promise.resolve();}now=end;}
  return {advance,state,video,events,get canvases(){return canvases;},get drawn(){return drawn;},get frames(){return frames;},get released(){return released;},get closed(){return closed;}};
}
for(const options of [{state:{connected:false}},{drm:true},{controls:true},{noCodec:true}]) {
  const env=await environment(options);await env.advance(1000);assert.equal(env.canvases.length,0);passed.push('passthrough '+JSON.stringify(options));
}
{
  const env=await environment();await env.advance(350);assert.equal(env.drawn.filter(x=>Number.isFinite(x.source)).length,0);await env.advance(250);
  assert.ok(env.drawn.some(x=>Number.isFinite(x.source)));assert.ok(env.drawn.filter(x=>Number.isFinite(x.source)).every(x=>x.at-x.source>=400 && x.at-x.source<420));
  assert.equal(env.canvases[0].dataset.wintopodSync,'active');assert.ok(env.frames-env.released<=4);
  passed.push('encoded queue waits requested delay and closes each frame');
  env.state.connected=false;await env.advance(800);assert.equal(env.canvases.length,0);assert.equal(env.closed,2);passed.push('disconnect restores page and closes codecs');
}
{
  const env=await environment({large:true});await env.advance(200);assert.equal(env.canvases.length,0);assert.equal(env.closed,2);passed.push('bounded encoded buffer fails open');
}
{
  const env=await environment();await env.advance(100);env.events.seeking();assert.equal(env.canvases.length,1);await env.advance(500);assert.ok(env.drawn.some(x=>Number.isFinite(x.source)));passed.push('seek preserves pictures for queued audio');env.state.delayMs=500;await env.advance(600);assert.equal(env.closed,0);passed.push('live delay changes keep codecs');env.events.pagehide();assert.equal(env.frames,env.released);passed.push('pagehide closes all frames');
}
console.log(JSON.stringify({passed:true,checks:passed,browser_execution_tested:false,actual_codec_execution_tested:false},null,2));
