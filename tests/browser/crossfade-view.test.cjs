'use strict';
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');

test('a lost acknowledgement is retried with the same command and stays on its channel',async()=>{
  const listeners=new Map(),elements=new Map(),storage=new Map();
  function element(id){
    const e={id,value:'0',textContent:'',disabled:false,hidden:false,addEventListener(type,fn){listeners.set(id+':'+type,fn)},reportValidity(){return true}};
    Object.defineProperty(e,'valueAsNumber',{get(){return Number(e.value)}});return e;
  }
  for(const id of ['seconds','version','save','retry','feedback','form'])elements.set('crossfade-'+id,element('crossfade-'+id));
  const localStorage={getItem:k=>storage.get(k)??null,setItem:(k,v)=>storage.set(k,v)};
  const context={engine:'engine',station:'9',active:true,fresh:true,supported:true,busy:false};
  let configuration={StationId:'9',Version:0,Seconds:0},first=true;const ids=[],messages=[];
  const bridge={context:()=>context,claim:()=>true,release:()=>{},message:x=>messages.push(x),api:async(url,options)=>{
    if(!options){const station=new URLSearchParams(url.split('?')[1]).get('station');return {crossfade:station==='9'?configuration:{StationId:station,Version:0,Seconds:0}};}
    const command=options.body.request;ids.push(command.Id);
    if(first){first=false;configuration={StationId:command.StationId,Version:1,Seconds:command.CrossfadeSeconds};throw new Error('connection lost');}
    return {accepted:true,crossfade:configuration};
  }};
  const sandbox={window:{},document:{getElementById:id=>elements.get(id)},localStorage,URLSearchParams,
    crypto:{randomUUID:()=> '018f0000-0000-4000-8000-000000000001'},Date,Number,String,JSON,Error,Object,Array,Map};
  vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../../src/OnAirNext.Web/wwwroot/crossfade.js'),'utf8'),sandbox);
  const ui=sandbox.window.RadioTeduCrossfade.create(bridge);ui.update();
  const wait=async check=>{for(let i=0;i<100;i++){if(check())return;await new Promise(r=>setTimeout(r,5));}throw new Error('UI state did not settle');};
  await wait(()=>!elements.get('crossfade-seconds').disabled);
  elements.get('crossfade-seconds').value='3';listeners.get('crossfade-seconds:input')();listeners.get('crossfade-form:submit')({preventDefault(){}});
  await wait(()=>!elements.get('crossfade-retry').hidden);
  assert.equal(JSON.parse(storage.get('studio-crossfade-pending')).length,1);
  listeners.get('crossfade-retry:click')();await wait(()=>elements.get('crossfade-retry').hidden);
  assert.deepEqual(ids,[ids[0],ids[0]]);assert.equal(configuration.Seconds,3);
  assert.equal(JSON.parse(storage.get('studio-crossfade-pending')).length,0);
  context.station='1';ui.update();await wait(()=>!elements.get('crossfade-seconds').disabled);
  assert.equal(elements.get('crossfade-seconds').value,'0');assert.ok(messages.includes('Music transition saved.'));
});
