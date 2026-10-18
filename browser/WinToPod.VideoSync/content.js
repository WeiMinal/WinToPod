// SPDX-License-Identifier: GPL-3.0-or-later
(() => {
  if(globalThis.__WinToPodVideoSync?.revision==='automatic-web-sync')return;
  globalThis.__WinToPodVideoSync?.stop?.();
  let pipeline=null,state={connected:false},stopped=false,disposed=false,polling;
  const failures=new WeakMap(),empty=(phase='idle',reason='none')=>({phase,reason,framesPresented:0,observedDelayMs:0});let report=empty();
  class DelayedVideo {
    constructor(video,delay){
      Object.assign(this,{video,delay,chunks:[],decoded:[],bytes:0,closed:false,framesPresented:0,observedDelayMs:0,lastCapture:-Infinity,lastKey:-Infinity,width:video.videoWidth,height:video.videoHeight});
      this.canvas=document.createElement('canvas');this.canvas.dataset.wintopodSync='buffering';this.canvas.setAttribute('aria-hidden','true');
      Object.assign(this.canvas.style,{position:'absolute',pointerEvents:'none',display:'none',background:'black',margin:'0',padding:'0',border:'0'});
      this.parent=video.parentElement;this.oldPosition=this.parent.style.position;
      if(getComputedStyle(this.parent).position==='static'){this.parent.style.position='relative';this.changedPosition=true;}
      this.parent.insertBefore(this.canvas,video.nextSibling);
      try{
        this.context=this.canvas.getContext('2d',{alpha:false});const scale=Math.min(1,1920/this.width,1080/this.height);
        this.canvas.width=Math.max(2,Math.floor(this.width*scale/2)*2);this.canvas.height=Math.max(2,Math.floor(this.height*scale/2)*2);
        this.decoder=new VideoDecoder({output:f=>{if(this.closed)f.close();else this.decoded.push(f);},error:()=>this.fail('codec-error')});
        this.encoder=new VideoEncoder({output:(c,m)=>this.enqueue(c,m),error:()=>this.fail('codec-error')});
        this.encoder.configure({codec:'vp8',width:this.canvas.width,height:this.canvas.height,bitrate:8000000,framerate:60,latencyMode:'realtime'});
        this.onSeek=()=>{this.lastKey=-Infinity;};video.addEventListener('seeking',this.onSeek);
        this.capture(performance.now());this.scheduleCapture();this.tick();
      }catch(e){this.close();throw e;}
    }
    position(){
      const v=this.video,c=this.canvas,s=getComputedStyle(v);
      Object.assign(c.style,{left:v.offsetLeft+'px',top:v.offsetTop+'px',width:v.clientWidth+'px',height:v.clientHeight+'px',objectFit:s.objectFit,objectPosition:s.objectPosition,zIndex:s.zIndex==='auto'?'0':s.zIndex,transform:s.transform,transformOrigin:s.transformOrigin,display:'block'});
    }
    scheduleCapture(){if(this.closed)return;if(this.video.requestVideoFrameCallback)this.vfc=this.video.requestVideoFrameCallback((n,m)=>{this.capture(m.expectedDisplayTime||n);this.scheduleCapture();});else this.raf=requestAnimationFrame(n=>{this.capture(n);this.scheduleCapture();});}
    capture(presentation){
      if(this.closed)return;const v=this.video,n=performance.now();if(v.paused||v.ended||v.readyState<2||n-this.lastCapture<15||this.encoder.encodeQueueSize>=2)return;let f;
      try{f=new VideoFrame(v,{timestamp:Math.round(presentation*1000)});this.encoder.encode(f,{keyFrame:n-this.lastKey>1800});if(n-this.lastKey>1800)this.lastKey=n;this.lastCapture=n;
        if(this.canvas.style.display==='none'){this.context.drawImage(v,0,0,this.canvas.width,this.canvas.height);this.position();}
      }catch(e){this.fail(e?.name==='SecurityError'?'cross-origin':'codec-error');}finally{f?.close();}
    }
    enqueue(chunk,meta){if(this.closed)return;if(this.bytes+chunk.byteLength>32*1024*1024||this.chunks.length>=350){this.fail('buffer-limit');return;}if(meta.decoderConfig)this.config=meta.decoderConfig;this.chunks.push({chunk,config:this.config});this.bytes+=chunk.byteLength;}
    tick(){
      if(this.closed)return;const n=performance.now();
      try{
        while(this.chunks.length&&this.chunks[0].chunk.timestamp/1000+this.delay<=n+25&&this.decoder.decodeQueueSize<3&&this.decoded.length<4){
          const i=this.chunks.shift();this.bytes-=i.chunk.byteLength;if(this.decoder.state==='unconfigured')this.decoder.configure(i.config||{codec:'vp8',codedWidth:this.canvas.width,codedHeight:this.canvas.height});this.decoder.decode(i.chunk);
        }
        let due;while(this.decoded.length&&this.decoded[0].timestamp/1000+this.delay<=n){due?.close();due=this.decoded.shift();}
        if(due){try{this.position();this.context.drawImage(due,0,0,this.canvas.width,this.canvas.height);this.framesPresented++;this.observedDelayMs=Math.round(n-due.timestamp/1000);this.canvas.dataset.wintopodSync='active';}finally{due.close();}}
      }catch{this.fail('codec-error');return;}this.timer=setTimeout(()=>this.tick(),8);
    }
    status(){return {phase:this.framesPresented?this.video.paused?'paused':'active':'buffering',reason:'none',framesPresented:this.framesPresented,observedDelayMs:this.observedDelayMs};}
    fail(reason){failures.set(this.video,reason);report=empty('error',reason);this.close();}
    close(){if(this.closed)return;this.closed=true;cancelAnimationFrame(this.raf);clearTimeout(this.timer);if(this.vfc!==undefined)this.video.cancelVideoFrameCallback?.(this.vfc);this.video.removeEventListener('seeking',this.onSeek);this.canvas.remove();if(this.changedPosition&&this.parent.style.position==='relative')this.parent.style.position=this.oldPosition;try{this.encoder?.close();}catch{}try{this.decoder?.close();}catch{}this.decoded.forEach(f=>f.close());this.decoded=[];this.chunks=[];this.bytes=0;}
  }
  function refresh(){
    const videos=[...document.querySelectorAll('video')].filter(v=>v.videoWidth&&v.clientWidth>100&&v.clientHeight>70).sort((a,b)=>b.clientWidth*b.clientHeight-a.clientWidth*a.clientHeight),v=videos[0];let reason='none';
    if(!v)reason='no-video';else if(v.mediaKeys)reason='drm';else if(v.controls)reason='native-controls';else if(!('VideoFrame'in globalThis&&'VideoEncoder'in globalThis&&'VideoDecoder'in globalThis))reason='no-codec';else if(document.pictureInPictureElement||document.fullscreenElement?.tagName==='VIDEO')reason='fullscreen';else if(document.hidden)reason='hidden';else if(failures.has(v))reason=failures.get(v);
    if(pipeline&&(pipeline.closed||pipeline.video!==v||!state.connected||reason!=='none'||pipeline.width!==v.videoWidth||pipeline.height!==v.videoHeight)){pipeline.close();pipeline=null;}
    if(pipeline)pipeline.delay=state.delayMs;
    if(!state.connected){report=empty();return;}if(reason!=='none'){report=empty(reason==='no-video'?'waiting':'unsupported',reason);return;}
    if(!pipeline&&!v.paused&&!v.ended){try{pipeline=new DelayedVideo(v,state.delayMs);}catch(e){failures.set(v,e?.name==='SecurityError'?'cross-origin':'codec-error');report=empty('error',failures.get(v));}}
    if(pipeline)report=pipeline.status();else if(!failures.has(v))report=empty('waiting');
  }
  async function poll(){if(stopped)return;try{state=await chrome.runtime.sendMessage({type:'WinToPod.status',dark:globalThis.matchMedia?.('(prefers-color-scheme: dark)').matches,report:pipeline&&!pipeline.closed?pipeline.status():report})||{connected:false};}catch{state={connected:false};}if(!Number.isInteger(state.delayMs)||state.delayMs<0||state.delayMs>5000)state={connected:false};refresh();polling=setTimeout(poll,500);}
  addEventListener('pagehide',()=>{stopped=true;clearTimeout(polling);pipeline?.close();},{once:true});addEventListener('pageshow',e=>{if(e.persisted&&stopped&&!disposed){stopped=false;poll();}});
  chrome.runtime.onMessage?.addListener((m,s,r)=>{if(m?.type==='WinToPod.companion-status')r(report);});
  globalThis.__WinToPodVideoSync={revision:'automatic-web-sync',stop(){disposed=true;stopped=true;clearTimeout(polling);pipeline?.close();}};poll();
})();
