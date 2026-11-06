((root,factory)=>{if(typeof module==='object'&&module.exports)module.exports=factory();else root.RadioTeduPublicationState=factory();})(globalThis,()=>{
  'use strict';
  const id=value=>typeof value==='string'&&/^[a-f0-9]{32}$/.test(value);
  function validPending(value){
    return !!value&&['prepare','unpublish'].includes(value.kind??'prepare')&&typeof value.engine==='string'&&value.engine.length>0&&value.engine.length<=100&&id(value.id)&&id(value.draftId)&&
      Number.isSafeInteger(value.draftVersion)&&value.draftVersion>=1&&Number.isSafeInteger(value.publicationVersion)&&value.publicationVersion>=0;
  }
  function committed(pending,publication){
    return validPending(pending)&&(pending.kind??'prepare')==='prepare'&&!!publication&&publication.Id===pending.draftId&&publication.Active===true&&
      publication.Version===pending.publicationVersion+1&&publication.DraftVersion===pending.draftVersion;
  }
  function unpublished(pending,publication){
    return validPending(pending)&&pending.kind==='unpublish'&&!!publication&&publication.Id===pending.draftId&&publication.Active===false&&
      publication.Version===pending.publicationVersion+1;
  }
  function describe(publication,draftVersion,runtime){
    if(!runtime)return {text:'Scheduler unavailable. Current broadcasts continue.',schedule:false,cancel:publication?.Active===true};
    if(!publication?.Active)return {text:'Draft — not scheduled.',schedule:true,cancel:false};
    if(publication.DraftVersion===draftVersion)return {text:'Scheduled · saved version '+draftVersion+'.',schedule:false,cancel:true};
    return {text:'An earlier saved version is scheduled. Review this draft before updating it.',schedule:true,cancel:true};
  }
  return {validPending,committed,unpublished,describe};
});
