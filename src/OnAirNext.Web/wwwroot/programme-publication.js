(() => {
  'use strict';
  window.RadioTeduProgrammePublication={create(bridge){
    const model=RadioTeduPublicationState,context=()=>bridge.context(),uuid=()=>crypto.randomUUID().replaceAll('-','');
    const node=(tag,text,cls)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;if(cls)n.className=cls;return n;};
    const button=(text,fn)=>{const b=node('button',text,'button');b.type='button';b.addEventListener('click',fn);return b;};
    const key='studio-programme-publication-pending';let pending=[],timer=null,polling=false,reads=Promise.resolve(),lastContext='',nextStatusRefresh=0;
    const cards=new Map(),states=new Map(),jobs=new Map();
    try{const value=JSON.parse(localStorage.getItem(key)??'[]');if(Array.isArray(value))pending=value.filter(model.validPending).slice(0,8);}catch{}
    const current=()=>pending.find(p=>p.engine===context().engine);
    const persist=()=>{try{localStorage.setItem(key,JSON.stringify(pending));return true;}catch{return false;}};
    const usable=()=>context().fresh&&context().executionSupported&&!!context().engine;
    const serial=fn=>{const next=reads.then(fn,fn);reads=next.catch(()=>{});return next;};
    async function publication(engine,id){
      const response=await serial(()=>bridge.api('/api/programmes/publication?'+new URLSearchParams({engine,id})));
      if(response.error)throw new Error(response.error);return response;
    }
    function remember(value){
      if(!model.validPending(value)||pending.some(p=>p.engine===value.engine)||pending.length>=8)return false;
      pending.push(value);if(persist())return true;pending=pending.filter(p=>p.id!==value.id);return false;
    }
    function forget(value){pending=pending.filter(p=>p.id!==value.id);persist();jobs.delete(value.id);}
    function repaint(id){const c=cards.get(id);if(c?.slot.isConnected)render(c);}
    function render(card){
      const {revision,slot,archive}=card,r=revision.Draft.Rule,engine=card.engine;if(engine!==context().engine)return;
      const status=states.get(r.Id);slot.replaceChildren();
      if(!status){slot.append(node('p','Checking schedule…','quiet'));if(archive)archive.disabled=true;return;}
      const active=current(), own=active?.draftId===r.Id, info=model.describe(status.publication,r.Version,status.runtime===true);
      slot.append(node('p',info.text,'editor-note'));
      if(archive)archive.disabled=!!status.publication?.Active||!!active||!usable();
      const actions=node('div',undefined,'programme-actions');
      if(own){
        const job=jobs.get(active.id);slot.append(node('p',job?.text??'Checking the saved request…','editor-feedback'));
        if(job?.retry)actions.append(button('Retry same request',()=>void retry(active)));
        if(job?.retry)actions.append(button('Dismiss after review',()=>{forget(active);bridge.message('Request dismissed. Review the current saved schedule before another change.');bridge.reload();}));
        if(job?.cancel)actions.append(button('Cancel preparation',()=>void cancelPreparation(active)));
      }else if(!active&&usable()&&bridge.canWrite()){
        if(info.schedule)actions.append(button(status.publication?.Active?'Update schedule':'Prepare and schedule',()=>void begin('prepare',card,status.publication)));
        if(info.cancel)actions.append(button('Cancel schedule',()=>void begin('unpublish',card,status.publication)));
      }
      slot.append(actions);
    }
    async function refreshCard(card){
      try{
        const value=await publication(card.engine,card.revision.Draft.Rule.Id);
        if(card.engine!==context().engine)return;states.set(card.revision.Draft.Rule.Id,value);repaint(card.revision.Draft.Rule.Id);
      }catch(error){if(card.engine===context().engine){card.slot.replaceChildren(node('p','Schedule state unavailable: '+error.message,'editor-feedback'));if(card.archive)card.archive.disabled=true;}}
    }
    function attach(container,revision,archive){
      if(!context().executionSupported)return;
      nextStatusRefresh=Date.now()+30000;
      const slot=node('div',undefined,'programme-publication');container.append(slot);
      const card={revision,slot,archive,engine:context().engine};cards.set(revision.Draft.Rule.Id,card);states.delete(revision.Draft.Rule.Id);render(card);void refreshCard(card);
      const p=current();if(p&&p.draftId===revision.Draft.Rule.Id)schedulePoll(p,0);
    }
    async function begin(kind,card,publicationState){
      if(!usable()||!bridge.canWrite()||current()||card.engine!==context().engine)return;
      const r=card.revision.Draft.Rule,p={kind,engine:card.engine,id:uuid(),draftId:r.Id,draftVersion:r.Version,publicationVersion:publicationState?.Version??0};
      if(kind==='unpublish'&&!publicationState?.Active)return;
      if(!remember(p)){bridge.message('Browser recovery storage is unavailable. The request was not sent.');return;}
      jobs.set(p.id,{text:kind==='prepare'?'Preparing audio and saving the schedule…':'Cancelling the saved schedule…'});repaint(p.draftId);await retry(p);
    }
    async function retry(p){
      if(!usable()||current()?.id!==p.id||!bridge.canWrite()||!bridge.claim())return;
      try{
        const prepare=p.kind==='prepare',body=prepare?{Engine:p.engine,Id:p.id,DraftId:p.draftId,DraftVersion:p.draftVersion,PublicationVersion:p.publicationVersion}
          :{Engine:p.engine,Id:p.id,DraftId:p.draftId,PublicationVersion:p.publicationVersion};
        const result=await bridge.api('/api/programmes/'+(prepare?'prepare':'unpublish'),{method:'POST',body});
        if(result.accepted!==true)throw new Error(result.error??'The engine did not confirm this request.');
        jobs.set(p.id,{text:prepare?'Audio preparation is running.':'Checking the cancelled schedule…',cancel:prepare});
      }catch(error){jobs.set(p.id,{text:'The result is unconfirmed: '+error.message+'. Check or retry the same request.',retry:true});}
      finally{bridge.release();repaint(p.draftId);schedulePoll(p,0);}
    }
    async function cancelPreparation(p){
      if(!usable()||current()?.id!==p.id||!bridge.canWrite()||!bridge.claim())return;
      try{
        const response=await bridge.api('/api/programmes/cancel',{method:'POST',body:{Engine:p.engine,Id:uuid(),JobId:p.id}});
        if(response.accepted!==true)throw new Error(response.error??'Cancellation was not confirmed.');
        jobs.set(p.id,{text:'Cancellation requested. Checking the actual result…'});
      }catch(error){jobs.set(p.id,{text:'Cancellation is unconfirmed: '+error.message+'. Checking the preparation state.',cancel:true});}
      finally{bridge.release();repaint(p.draftId);schedulePoll(p,0);}
    }
    function schedulePoll(p,delay=2000){
      if(timer!==null||polling||current()?.id!==p.id||!context().active)return;
      timer=setTimeout(()=>{timer=null;void poll(p);},delay);
    }
    async function poll(p){
      if(polling||current()?.id!==p.id||!usable()||!context().active)return;polling=true;
      try{
        const durable=await publication(p.engine,p.draftId);if(current()?.id!==p.id||p.engine!==context().engine)return;
        states.set(p.draftId,durable);
        if(model.committed(p,durable.publication)||model.unpublished(p,durable.publication)){
          forget(p);bridge.message(p.kind==='prepare'?'Schedule saved and read back from the engine.':'Schedule cancelled and read back from the engine.');
          repaint(p.draftId);bridge.reload();return;
        }
        if(p.kind==='unpublish'){
          jobs.set(p.id,{text:'Cancellation has not been confirmed. Retry the same request.',retry:true});return;
        }
        const response=await serial(()=>bridge.api('/api/programmes/preparation?'+new URLSearchParams({engine:p.engine,job:p.id})));
        if(current()?.id!==p.id||p.engine!==context().engine)return;
        const job=response.job;if(response.error||!job){jobs.set(p.id,{text:'Preparation is not visible after reconnecting. Retry the same request or review the saved schedule.',retry:true});return;}
        if(job.State==='Preparing'||job.State==='Committing'){
          jobs.set(p.id,{text:job.State==='Preparing'?'Validating audio in the background…':'Saving the schedule…',cancel:job.State==='Preparing'});return;
        }
        if(job.State==='Published'){
          const checks=(jobs.get(p.id)?.checks??0)+1;
          jobs.set(p.id,{text:'Checking the saved schedule against the engine…',checks,retry:checks>=3});return;
        }
        if(job.State==='Failed'||job.State==='Cancelled'){
          forget(p);bridge.message(job.State==='Failed'?(job.Error??'Audio preparation failed. No new schedule was saved.'):'Preparation cancelled. No new schedule was saved.');bridge.reload();return;
        }
        jobs.set(p.id,{text:'The result needs review. Check the saved schedule or retry the same request.',retry:true});
      }catch(error){jobs.set(p.id,{text:'Could not confirm the request: '+error.message+'. Retry the same request.',retry:true});}
      finally{
        polling=false;repaint(p.draftId);
        const job=jobs.get(p.id);if(current()?.id===p.id&&!job?.retry)schedulePoll(p);
      }
    }
    return {attach,update(){
      const c=context(),p=current();if(p&&c.active&&usable())schedulePoll(p);
      if(c.active&&usable()&&Date.now()>=nextStatusRefresh){nextStatusRefresh=Date.now()+30000;for(const card of cards.values())if(card.slot.isConnected&&card.engine===c.engine)void refreshCard(card);}
      const signature=[c.engine,c.fresh,c.executionSupported,c.busy,c.active,bridge.canWrite(),p?.id].join('|');
      if(signature!==lastContext){lastContext=signature;for(const card of cards.values())if(card.slot.isConnected)render(card);}
    }};
  }};
})();
