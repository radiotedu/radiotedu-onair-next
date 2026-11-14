const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm'),path=require('node:path');
test('brief capture bursts drop excess frames without reconnecting; a stalled link still closes',async()=>{
 const elements=new Map(),intervals=[];let worklet,socket,now=1000;
 const element=id=>{if(!elements.has(id))elements.set(id,{value:id==='mic-gain'?'0':'',textContent:'',listeners:{},classList:{toggle(){}},addEventListener(name,fn){this.listeners[name]=fn},setAttribute(){},replaceChildren(){},showModal(){}});return elements.get(id)};
 const track={stop(){},addEventListener(){}},node=()=>({connect(){},disconnect(){},gain:{value:0,setTargetAtTime(){}}});
 class Audio{constructor(){this.sampleRate=48000;this.currentTime=0;this.destination={};this.audioWorklet={addModule:async()=>{}}}async resume(){}async close(){}createGain(){return node()}createMediaStreamSource(){return node()}addEventListener(){}}
 class Socket{static OPEN=1;constructor(){socket=this;this.readyState=0;this.bufferedAmount=0;this.sent=0;this.closed=false}send(){this.sent++}close(){this.closed=true}}
 const sandbox={window:{addEventListener(){}},document:{getElementById:element,createElement:()=>({})},navigator:{mediaDevices:{enumerateDevices:async()=>[],getUserMedia:async()=>({getTracks:()=>[track]}),addEventListener(){}}},AudioContext:Audio,AudioWorkletNode:class{constructor(){worklet=this;this.port={}}connect(){}disconnect(){}},WebSocket:Socket,location:{host:'127.0.0.1:18121'},URLSearchParams,Float32Array,ArrayBuffer,Uint8Array,Date:{now:()=>now},setInterval:fn=>intervals.push(fn)};
 vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../../src/OnAirNext.Web/wwwroot/microphone.js'),'utf8'),sandbox);
 const mic=sandbox.window.RadioTeduMicrophone.create({context:()=>({engine:'fixture',station:'4',name:'Pop',fresh:true,running:true,supported:true,token:'fixture'})});
 element('mic-prepare').listeners.click();await new Promise(resolve=>setImmediate(resolve));
 assert.equal(element('mic-state').textContent,'Private test');element('mic-live').listeners.click();element('mic-confirm').returnValue='live';element('mic-confirm').listeners.close();socket.readyState=1;socket.onopen();
 for(let i=0;i<30;i++)worklet.port.onmessage({data:new Float32Array(1920).buffer});
 assert.equal(socket.sent,8);assert.equal(socket.closed,false);assert.equal(mic.active(),true);
 socket.onmessage({data:new Uint8Array([6]).buffer});worklet.port.onmessage({data:new Float32Array(1920).buffer});assert.equal(socket.sent,9);
 now+=1600;intervals[0]();assert.equal(socket.closed,true);assert.equal(mic.active(),false);assert.match(element('mic-feedback').textContent,/timed out/);
});
