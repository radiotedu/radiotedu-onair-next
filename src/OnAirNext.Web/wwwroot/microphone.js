(() => {
  'use strict';
  window.RadioTeduMicrophone = {create({context}) {
    const $ = id => document.getElementById(id);
    let stream=null, audio=null, gain=null, monitor=null, worklet=null, socket=null;
    let owner=null, liveOwner=null, busy=false, generation=0, lastAck=0, outstanding=0, muted=false;
    const say = message => { $('mic-feedback').textContent=message; };
    const same = (a,b) => !!a && a.engine===b.engine && a.station===b.station;
    const eligible = c => c.fresh && c.running && c.supported && !!c.token;
    function render() {
      const c=context();
      $('mic-prepare').disabled=busy || !!stream || !c.supported;
      $('mic-device').disabled=busy || !!stream;
      $('mic-refresh').disabled=busy || !!stream;
      $('mic-live').disabled=busy || !stream || !!socket || !eligible(c) || !same(owner,c);
      $('mic-end').disabled=!socket;
      $('mic-close').disabled=!stream && !busy;
      $('mic-mute').disabled=!stream;
      $('mic-mute').textContent=muted?'Unmute':'Mute';
      $('mic-mute').setAttribute('aria-pressed',String(muted));
      $('mic-state').textContent=socket?.readyState===WebSocket.OPEN?'LIVE · '+(liveOwner?.name??'') : socket?'Connecting' : stream?'Private test':'Off';
      $('mic-state').classList.toggle('mic-is-live',socket?.readyState===WebSocket.OPEN);
      $('mic-monitor').disabled=!stream;
    }
    function endLive(message='Microphone ended. Music returns to its normal level.') {
      const old=socket; socket=null; liveOwner=null; outstanding=0;
      if(old) { old.onclose=null; old.onerror=null; old.onmessage=null; old.close(); }
      if(message) say(message); render();
    }
    async function close(message='Microphone released. Automation continues.') {
      generation++; busy=false; endLive(null);
      const oldStream=stream, oldAudio=audio; stream=null;audio=null;owner=null;
      worklet?.disconnect();worklet=null;gain=null;monitor=null;
      oldStream?.getTracks().forEach(track=>track.stop());
      $('mic-monitor').checked=false; $('mic-level').value=0; $('mic-level-text').textContent='No input';
      if(oldAudio) await oldAudio.close().catch(()=>{});
      say(message);render();
    }
    async function devices() {
      if(!navigator.mediaDevices?.enumerateDevices) {say('Microphone capture requires this local console in a supported browser.');return;}
      const selected=$('mic-device').value;
      try {
        const inputs=(await navigator.mediaDevices.enumerateDevices()).filter(d=>d.kind==='audioinput');
        $('mic-device').replaceChildren(...inputs.map((device,index)=>{
          const option=document.createElement('option');option.value=device.deviceId;option.textContent=device.label || 'Microphone '+(index+1);return option;
        }));
        if(inputs.some(d=>d.deviceId===selected)) $('mic-device').value=selected;
      } catch(error) {say(error.message);}
    }
    async function prepare() {
      if(busy || stream) return;
      const c=context(), attempt=++generation; busy=true; owner={...c};render();
      let acquired=null, created=null;
      try {
        const selected=$('mic-device').value;
        acquired=await navigator.mediaDevices.getUserMedia({audio:{deviceId:selected?{exact:selected}:undefined,
          channelCount:2,sampleRate:48000,echoCancellation:false,noiseSuppression:false,autoGainControl:false},video:false});
        if(attempt!==generation || !same(c,context())) {acquired.getTracks().forEach(t=>t.stop());return;}
        created=new AudioContext({sampleRate:48000,latencyHint:'interactive'});
        if(created.sampleRate!==48000) throw new Error('This audio device cannot provide the required 48 kHz context.');
        await created.audioWorklet.addModule('/microphone-worklet.js');
        await created.resume();
        if(attempt!==generation || !same(c,context())) {acquired.getTracks().forEach(t=>t.stop());await created.close();return;}
        stream=acquired;audio=created;muted=false;
        gain=audio.createGain();gain.gain.value=Math.pow(10,Number($('mic-gain').value)/20);
        const source=audio.createMediaStreamSource(stream);source.connect(gain);
        monitor=audio.createGain();monitor.gain.value=0;gain.connect(monitor);monitor.connect(audio.destination);
        worklet=new AudioWorkletNode(audio,'radiotedu-microphone',{numberOfInputs:1,numberOfOutputs:1,outputChannelCount:[2]});
        gain.connect(worklet);worklet.connect(audio.destination);
        worklet.port.onmessage=event=>{
          if(!stream) return;
          const samples=new Float32Array(event.data);let peak=0;
          for(const sample of samples)peak=Math.max(peak,Math.abs(sample));
          $('mic-level').value=Math.min(1,peak);
          $('mic-level-text').textContent=peak>0?Math.max(-96,20*Math.log10(peak)).toFixed(1)+' dBFS':'Silent';
          if(socket?.readyState===WebSocket.OPEN) {
            if(!same(liveOwner,context()) || socket.bufferedAmount>15360 || outstanding>=8) {
              endLive('Microphone connection could not keep up. Music continues. Choose Go live to reconnect.');return;
            }
            socket.send(event.data);outstanding++;
          }
        };
        stream.getTracks().forEach(track=>track.addEventListener('ended',()=>void close('Microphone disconnected. Automation continues.')));
        audio.addEventListener('statechange',()=>{if(audio && audio.state!=='running')endLive('Audio capture paused. Music continues; test the microphone before going live again.');});
        await devices();say('Private test only. Check the meter, then choose Go live for '+c.name+'.');
      } catch(error) {
        acquired?.getTracks().forEach(t=>t.stop());if(created && created!==audio)await created.close().catch(()=>{});
        await close('Microphone unavailable: '+error.message);
      } finally {if(attempt===generation){busy=false;render();}}
    }
    function goLive() {
      const c=context();if(!stream || socket || !eligible(c) || !same(owner,c))return;
      // The confirmation names the captured target; changing selection cannot redirect an open microphone.
      $('mic-confirm-text').textContent='Send this microphone to '+c.name+' and all of its outputs? Music stays playing at a reduced level. No other channel changes.';
      $('mic-confirm').returnValue='cancel';$('mic-confirm').showModal();
    }
    $('mic-confirm').addEventListener('close',()=>{
      const c=context();if($('mic-confirm').returnValue!=='live' || !stream || socket || !eligible(c) || !same(owner,c))return;
      liveOwner={...c};outstanding=0;lastAck=Date.now();
      const opened=new WebSocket('ws://'+location.host+'/api/microphone?'+new URLSearchParams({engine:c.engine,station:c.station}),['onair-microphone-v1',c.token]);
      socket=opened;opened.binaryType='arraybuffer';
      opened.onopen=()=>{if(socket!==opened){opened.close();return;}lastAck=Date.now();say('Microphone LIVE on '+c.name+'. End live restores normal music level.');render();};
      opened.onmessage=event=>{if(socket!==opened)return;if(!(event.data instanceof ArrayBuffer)||new Uint8Array(event.data)[0]!==6){endLive('Invalid microphone acknowledgement. Music continues.');return;}lastAck=Date.now();outstanding=Math.max(0,outstanding-1);};
      opened.onerror=()=>{if(socket===opened)endLive('Microphone could not connect. The channel may be busy or the engine unavailable. Music continues.');};
      opened.onclose=()=>{if(socket===opened)endLive('Microphone connection ended. Music continues. Go live is never retried automatically.');};render();
    });
    $('mic-refresh').addEventListener('click',()=>void devices());
    $('mic-prepare').addEventListener('click',()=>void prepare());
    $('mic-live').addEventListener('click',goLive);
    $('mic-end').addEventListener('click',()=>endLive());
    $('mic-close').addEventListener('click',()=>void close());
    $('mic-mute').addEventListener('click',()=>{muted=!muted;applyGain();render();});
    function applyGain(){if(gain&&audio)gain.gain.setTargetAtTime(muted?0:Math.pow(10,Number($('mic-gain').value)/20),audio.currentTime,.01);$('mic-gain-value').textContent=$('mic-gain').value+' dB';}
    $('mic-gain').addEventListener('input',applyGain);
    $('mic-monitor').addEventListener('change',()=>{if(monitor&&audio)monitor.gain.setTargetAtTime($('mic-monitor').checked?1:0,audio.currentTime,.01);});
    navigator.mediaDevices?.addEventListener('devicechange',()=>{if(!stream)void devices();});
    window.addEventListener('pagehide',()=>{void close();});
    setInterval(()=>{if(socket && Date.now()-lastAck>1500)endLive('Microphone acknowledgement timed out. Music continues.');},250);
    void devices();render();
    return {active:()=>!!socket,update(){const c=context();if(owner&&!same(owner,c)){void close('Channel changed. Microphone released; test again for the selected channel.');return;}if(socket&&!eligible(c))endLive('Station status is unavailable. Microphone ended; music automation remains independent.');render();}};
  }};
})();
