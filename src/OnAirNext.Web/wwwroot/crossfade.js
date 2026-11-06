(() => {
  'use strict';
  window.RadioTeduCrossfade={create(bridge){
    const $=id=>document.getElementById('crossfade-'+id),key='studio-crossfade-pending';
    const valid=p=>p&&typeof p.engine==='string'&&p.engine.length>0&&typeof p.station==='string'&&p.station.length>0&&
      /^[a-f0-9-]{32,36}$/i.test(p.id??'')&&Number.isSafeInteger(p.version)&&p.version>=0&&Number.isFinite(p.seconds)&&
      (p.seconds===0||p.seconds>=.5&&p.seconds<=15);
    let pending=[];try{const saved=JSON.parse(localStorage.getItem(key)??'[]');if(Array.isArray(saved))pending=saved.filter(valid).slice(0,8);}catch{}
    const persist=()=>{try{localStorage.setItem(key,JSON.stringify(pending));return true;}catch{return false;}};
    const state={key:'',configuration:null,dirty:false,loading:false,generation:0,nextFetch:0};
    const context=()=>bridge.context(),target=c=>String(c.engine??'')+'\0'+c.station;
    const waiting=c=>pending.find(p=>p.engine===c.engine);
    const text=value=>{$('feedback').textContent=value;};
    function update(){
      const c=context(),k=target(c),usable=c.active&&c.fresh&&c.supported&&!!c.engine&&!!c.station;
      if(k!==state.key){state.key=k;state.configuration=null;state.dirty=false;state.loading=false;state.generation++;state.nextFetch=0;}
      const p=waiting(c),own=p?.station===c.station;
      if(usable&&!state.configuration&&!state.loading&&Date.now()>=state.nextFetch)void load(c,k);
      $('seconds').disabled=!usable||c.busy||!state.configuration||!!p;
      $('save').disabled=!usable||c.busy||!state.configuration||!state.dirty||!!p;
      $('retry').hidden=!own;$('retry').disabled=!usable||c.busy;
      if(!usable&&!state.loading)text('Connect a compatible engine to manage transitions.');
      else if(p&&!own)text('A saved transition request belongs to another channel. Select that channel to retry it.');
      else if(own)text('A transition request needs confirmation. Retry the same saved request.');
    }
    async function load(c,k){
      state.loading=true;state.nextFetch=Date.now()+5000;const generation=state.generation;
      try{
        const response=await bridge.api('/api/crossfade?'+new URLSearchParams({engine:c.engine,station:c.station}));
        if(k!==state.key||generation!==state.generation)return;
        const value=response.crossfade;if(response.error||!value||value.StationId!==c.station||!Number.isSafeInteger(value.Version)||!Number.isFinite(value.Seconds))throw new Error(response.error??'Invalid setting.');
        state.configuration=value;$('version').textContent='Version '+value.Version;
        if(!state.dirty)$('seconds').value=String(value.Seconds);
        if(!waiting(c))text('Saved transition loaded.');
      }catch(error){if(k===state.key&&generation===state.generation)text('Could not load transitions: '+error.message);}
      finally{if(k===state.key&&generation===state.generation){state.loading=false;update();}}
    }
    async function submit(p){
      if(!bridge.claim())return;
      try{
        const response=await bridge.api('/api/command',{method:'POST',body:{engine:p.engine,request:{Id:p.id,Action:'crossfade-save',StationId:p.station,Revision:p.version,CrossfadeSeconds:p.seconds}}});
        if(response.accepted!==true)throw Object.assign(new Error(response.error??'The engine rejected the request.'),{rejected:true});
        const saved=response.crossfade;
        if(!saved||saved.StationId!==p.station||saved.Version!==p.version+1||saved.Seconds!==p.seconds)throw new Error('The saved result did not match the request.');
        pending=pending.filter(x=>x.id!==p.id);persist();
        if(target(context())===p.engine+'\0'+p.station){state.configuration=saved;state.dirty=false;$('seconds').value=String(saved.Seconds);$('version').textContent='Version '+saved.Version;text('Transition saved. It applies to upcoming music.');}
        bridge.message('Music transition saved.');
      }catch(error){
        if(error.rejected||error.status&&error.status<500){pending=pending.filter(x=>x.id!==p.id);persist();state.configuration=null;state.nextFetch=0;text('Transition was not saved: '+error.message);}
        else text('The result is unconfirmed. Retry the same saved request.');
      }finally{bridge.release();update();}
    }
    $('seconds').addEventListener('input',()=>{state.dirty=true;update();});
    $('form').addEventListener('submit',event=>{
      event.preventDefault();const c=context(),seconds=$('seconds').valueAsNumber;
      if(!c.active||!c.fresh||!c.supported||c.busy||!state.configuration||waiting(c))return;
      if(!$('form').reportValidity()||!Number.isFinite(seconds)||seconds!==0&&seconds<.5){text('Choose 0 or a duration from 0.5 to 15 seconds.');return;}
      const request={engine:c.engine,station:c.station,id:crypto.randomUUID(),version:state.configuration.Version,seconds};
      if(pending.length>=8){text('Resolve saved transition requests first.');return;}
      pending.push(request);if(!persist()){pending.pop();text('Browser storage is unavailable. The request was not sent.');return;}
      void submit(request);update();
    });
    $('retry').addEventListener('click',()=>{const p=waiting(context());if(p?.station===context().station)void submit(p);});
    return {update};
  }};
})();
