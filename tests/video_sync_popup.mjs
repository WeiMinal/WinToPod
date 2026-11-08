// SPDX-License-Identifier: GPL-3.0-or-later
// Production popup/worker code, simulated browser APIs. No MV3 installation claim.
import vm from 'node:vm';
import fs from 'node:fs';
import assert from 'node:assert/strict';
const popup=fs.readFileSync(new URL('../browser/WinToPod.VideoSync/popup.js',import.meta.url),'utf8');
const worker=fs.readFileSync(new URL('../browser/WinToPod.VideoSync/background.js',import.meta.url),'utf8');
const checks=[];
async function environment(url='https://www.bilibili.com/video/test') {
  const nodes=Object.fromEntries(['status','videoStatus','metrics','enable','disable'].map(id=>[id,{textContent:'',disabled:false}]));
  const data={disabledOrigins:[]}, state={connected:true,delayMs:2000,bridgeReachable:true,protocolCompatible:true};
  const report={phase:'active',reason:'none',observedDelayMs:2010,framesPresented:100};
  let permitted=true, poll, disposed=false, pagehide, injections=0;
  const context={URL,TextEncoder,Number,document:{querySelector:s=>nodes[s.slice(1)]},matchMedia:()=>({matches:false}),
    setInterval:f=>(poll=f,1),clearInterval:()=>disposed=true,addEventListener:(n,f)=>pagehide=f,
    chrome:{tabs:{query:async()=>[{id:1,url}],sendMessage:async()=>report},runtime:{sendMessage:async()=>({...state})},
      storage:{local:{get:async()=>({...data}),set:async s=>Object.assign(data,s)}},permissions:{contains:async()=>permitted,request:async()=>permitted},
      scripting:{unregisterContentScripts:async()=>{},registerContentScripts:async()=>{},executeScript:async()=>{injections++;}}}};
  vm.runInNewContext(popup,context);
  const flush=async()=>{for(let i=0;i<30;i++)await Promise.resolve();};await flush();
  return {nodes,data,state,report,refresh:async()=>{await poll();await flush();},permission:v=>permitted=v,close:()=>pagehide(),get disposed(){return disposed;},get injections(){return injections;}};
}
{
  const e=await environment();assert.match(e.nodes.metrics.textContent,/100 帧/);checks.push('actual page counters shown separately from requested delay');
  e.state.connected=false;e.state.bridgeReachable=false;await e.refresh();assert.equal(e.nodes.status.textContent,'主程序未响应');assert.equal(e.nodes.metrics.textContent,'');checks.push('lost bridge cannot retain active status or stale counters');
  e.state.bridgeReachable=true;e.state.protocolCompatible=false;await e.refresh();assert.match(e.nodes.status.textContent,/不兼容/);checks.push('protocol mismatch differs from missing main app');
  e.state.protocolCompatible=true;await e.refresh();assert.match(e.nodes.status.textContent,/未连接音箱/);checks.push('reachable disconnected state distinguished');
  e.state.connected=true;e.data.disabledOrigins=['https://www.bilibili.com'];await e.refresh();assert.match(e.nodes.videoStatus.textContent,/已停用/);assert.ok(e.nodes.disable.disabled);checks.push('disabled origin dominates stale active page report');
  e.data.disabledOrigins=[];e.permission(false);await e.refresh();assert.match(e.nodes.videoStatus.textContent,/尚未授权/);await e.nodes.enable.onclick();assert.equal(e.injections,0);checks.push('denied permission never injects');
  e.permission(true);e.report.phase='unsupported';e.report.reason='drm';await e.refresh();assert.match(e.nodes.videoStatus.textContent,/受保护/);checks.push('actionable unsupported reason');
  e.close();assert.ok(e.disposed);checks.push('popup close stops polling');
}
{
  const e=await environment('edge://extensions');assert.ok(e.nodes.enable.disabled&&e.nodes.disable.disabled);checks.push('internal pages cannot request permissions or inject');
}
{
  let listener,now=1000,result,fail=false;
  class Clock extends Date {static now(){return now;}}
  const c={Date:Clock,URL,AbortSignal,fetch:async()=>{if(fail)throw Error();return {ok:true,json:async()=>result};},chrome:{action:{setIcon:async()=>{}},storage:{local:{get:async()=>({})}},runtime:{onMessage:{addListener:f=>listener=f},onInstalled:{addListener(){}}}}};
  vm.runInNewContext(worker,c);
  const read=()=>new Promise(resolve=>listener({type:'WinToPod.status'},{},resolve));
  result={protocol:'WinToPod.VideoSync/1',connected:false,delayMs:0};let s=await read();assert.equal(s.bridgeReachable,true);assert.equal(s.protocolCompatible,true);
  now+=1000;result={protocol:'wrong',connected:true,delayMs:100};s=await read();assert.equal(s.bridgeReachable,true);assert.equal(s.protocolCompatible,false);assert.equal(s.connected,false);
  now+=1000;fail=true;s=await read();assert.equal(s.bridgeReachable,false);checks.push('real worker distinguishes valid offline, wrong protocol and failed fetch');
}
console.log(JSON.stringify({passed:true,checks,browser_apis_simulated:true,real_browser_installation_tested:false},null,2));
