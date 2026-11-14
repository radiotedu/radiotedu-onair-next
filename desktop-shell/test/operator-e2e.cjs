'use strict';
if(process.env.GITHUB_ACTIONS!=='true')throw Error('Disposable CI only.');
const {app,BrowserWindow,session}=require('electron');const fs=require('node:fs'),path=require('node:path'),cp=require('node:child_process'),crypto=require('node:crypto');
app.commandLine.appendSwitch('use-fake-device-for-media-stream');app.commandLine.appendSwitch('use-fake-ui-for-media-stream');app.commandLine.appendSwitch('disable-gpu');
const root=path.resolve(__dirname,'../..'),out=path.join(root,'artifacts/operator-ui');fs.mkdirSync(out,{recursive:true});
let host=null,web=null,window=null;const failures=[];const log=fs.openSync(path.join(out,'process.log'),'a');
const pause=ms=>new Promise(resolve=>setTimeout(resolve,ms));
async function until(check,label,seconds=40){const end=Date.now()+seconds*1000;while(Date.now()<end){if(await check())return;await pause(150);}throw Error('Timed out: '+label);}
const js=code=>window.webContents.executeJavaScript(code,true);
const evaluate=fn=>js('('+fn.toString()+')()');
const text=id=>js('document.getElementById('+JSON.stringify(id)+')?.textContent ?? ""');
const click=id=>js('document.getElementById('+JSON.stringify(id)+').click()');
async function screenshot(name){await pause(300);fs.writeFileSync(path.join(out,name+'.png'),(await window.webContents.capturePage()).toPNG());}
function spawn(exe,args){const child=cp.spawn(exe,args,{stdio:['ignore',log,log],windowsHide:true,env:{...process.env,TMPDIR:process.platform==='darwin'?'/tmp':process.env.TMPDIR}});child.on('error',error=>failures.push(error.message));return child;}
app.whenReady().then(async()=>{
 try{
  let descriptor,engine;const installed=process.platform==='darwin';
  if(installed){const resources='/Applications/RadioTEDU OnAir Next.app/Contents/Resources';const paths=JSON.parse(cp.execFileSync(path.join(resources,'engine/OnAirNext.Host'),['--macos-paths'],{encoding:'utf8'}));descriptor=paths.descriptor;engine='onair-next-dev-macos';try{const health=await fetch('http://127.0.0.1:18121/api/health');if(!health.ok)throw Error('Console not ready');}catch{web=spawn(path.join(resources,'console/OnAirNext.Web'),[]);}}
  else {
   const data=path.join(process.env.LOCALAPPDATA,'RadioTEDU','OnAirNext-Dev','operator-e2e',crypto.randomUUID());fs.mkdirSync(data,{recursive:true});engine='onair-next-dev-e2e-'+crypto.randomUUID().replaceAll('-','');
   const config=path.join(data,'host.json');fs.writeFileSync(config,JSON.stringify({Ffmpeg:path.join(root,'artifacts/windows-codec-runtime/bin/ffmpeg.exe'),DataRoot:data,PipeName:engine,Stations:['1','2','4','5','8','9','10','11'].map(Id=>({Id,Playlist:[],Outputs:Id==='4'?[{Id:'ui-loopback',Host:'127.0.0.1',Port:9,Mount:'/next-ui-fixture',SourcePassword:'synthetic-ci-only'}]:[],AutoStart:false}))}));
   host=spawn(path.join(root,'artifacts/windows-package/bin/OnAirNext.Host.exe'),['--config',config]);web=spawn(path.join(root,'artifacts/windows-package/console/OnAirNext.Web.exe'),[]);descriptor=path.join(process.env.LOCALAPPDATA,'RadioTEDU','OnAirNext-Dev','browser-console/access/console.json');
  }
  let endpoint;
  await until(async()=>{try{const response=await fetch('http://127.0.0.1:18121/api/health');const health=await response.json();endpoint=JSON.parse(fs.readFileSync(descriptor,'utf8'));return health.processId===endpoint.ProcessId;}catch{return false;}},'isolated console startup');
  session.defaultSession.setPermissionRequestHandler((_wc,permission,callback,details)=>callback(permission==='media'&&!(details.mediaTypes??[]).includes('video')));
  session.defaultSession.setPermissionCheckHandler((_wc,permission)=>permission==='media');
  window=new BrowserWindow({width:1450,height:1000,show:true,webPreferences:{nodeIntegration:false,contextIsolation:true,sandbox:true,backgroundThrottling:false}});
  window.webContents.on('console-message',(_event,level,message)=>{if(level>=3)failures.push(message);});
  await window.loadURL('http://127.0.0.1:18121/#access='+endpoint.Token);
  if(!installed){await evaluate(()=>document.querySelector('[data-page=system]').click());await until(()=>js('Array.from(document.getElementById("engine-select").options).some(o=>o.value==='+JSON.stringify(engine)+')'),'fixture discovery');await js('document.getElementById("engine-select").value='+JSON.stringify(engine));await click('connect-engine');}
  await until(()=>evaluate(()=>document.querySelector('[aria-label="Pop channel"]')!==null),'channel tabs');
  await evaluate(()=>{document.querySelector('[aria-label="Pop channel"]').click();document.querySelector('[data-page="library"]').click();});
  await until(()=>evaluate(()=>!document.getElementById('add-files').disabled),'file import controls');
  await evaluate(()=>{const frames=48000*6,bytes=new Uint8Array(44+frames*4),v=new DataView(bytes.buffer);const ascii=(at,s)=>{for(let i=0;i<s.length;i++)bytes[at+i]=s.charCodeAt(i)};ascii(0,'RIFF');v.setUint32(4,bytes.length-8,true);ascii(8,'WAVEfmt ');v.setUint32(16,16,true);v.setUint16(20,1,true);v.setUint16(22,2,true);v.setUint32(24,48000,true);v.setUint32(28,192000,true);v.setUint16(32,4,true);v.setUint16(34,16,true);ascii(36,'data');v.setUint32(40,frames*4,true);for(let i=0;i<frames;i++){const n=Math.round(5000*Math.sin(2*Math.PI*440*i/48000));v.setInt16(44+i*4,n,true);v.setInt16(46+i*4,n,true);}const transfer=new DataTransfer();transfer.items.add(new File([bytes],'Synthetic operator test.wav',{type:'audio/wav'}));const input=document.getElementById('music-files');input.files=transfer.files;input.dispatchEvent(new Event('change',{bubbles:true}));});
  await until(async()=>/1 validated/.test(await text('library-count')),'validated UI file import',70);await screenshot('library-import');
  await evaluate(()=>document.querySelector('[data-page=system]').click());
  if(installed){
   await until(()=>evaluate(()=>!document.getElementById('wizard-review').disabled),'stopped-channel setup gate');
   await evaluate(()=>{for(const [id,value] of [['wizard-host','127.0.0.1'],['wizard-port','18001'],['wizard-user','source'],['wizard-password','synthetic-ci-only']]){const input=document.getElementById(id);input.value=value;input.dispatchEvent(new Event('input',{bubbles:true}));}document.querySelector('[aria-label="Pop enable outputs"]').click();const mount=document.querySelector('[aria-label="Pop primary mount"]');mount.value='/next-ui-fixture';mount.dispatchEvent(new Event('input',{bubbles:true}));});
   await click('wizard-review');await until(async()=>(await text('wizard-summary')).includes('/next-ui-fixture-low'),'redacted output review');
   await evaluate(()=>document.getElementById('wizard-confirm').click());await click('wizard-save');
   await until(async()=>(await text('wizard-feedback')).startsWith('Connections saved'),'protected profile save');
   await until(async()=>(await text('outputs-summary')).includes('/next-ui-fixture-low'),'automatic daemon restart and saved output readback',70);
   if(await evaluate(()=>document.getElementById('wizard-password').value)!=='')throw Error('Saved password remains in form.');
  }
  await evaluate(()=>document.querySelector('[data-page=air]').click());await until(()=>evaluate(()=>!document.getElementById('start-station').disabled),'configured station start control');await click('start-station');await until(async()=>await text('station-state')==='Running','station start');
  await click('mic-prepare');await until(async()=>await text('mic-state')==='Private test','private microphone capture');await click('mic-live');await evaluate(()=>document.querySelector('#mic-confirm button[value="live"]').click());
  await until(async()=>(await text('mic-state')).startsWith('LIVE'),'authenticated microphone on air');await pause(2500);if(!(await text('mic-state')).startsWith('LIVE'))throw Error('Microphone lost its engine acknowledgements.');await screenshot('microphone-live');window.hide();await pause(8000);if(!(await text('mic-state')).startsWith('LIVE'))throw Error('Hiding the desktop window ended capture.');window.show();
  await click('mic-end');await until(async()=>await text('mic-state')==='Private test','return to automation');
  await evaluate(()=>document.querySelector('[aria-label="Rock channel"]').click());await until(async()=>await text('mic-state')==='Off','channel change releases microphone');
  await evaluate(()=>document.querySelector('[aria-label="Pop channel"]').click());await click('stop-station');await evaluate(()=>document.querySelector('#stop-dialog button[value="stop"]').click());await until(async()=>await text('station-state')==='Stopped','isolated station stop');
  await evaluate(()=>document.querySelector('[data-page=system]').click());  await screenshot('setup-wizard');window.setSize(1000,850);await screenshot('setup-narrow');
  const functionalErrors=failures.filter(error=>!/favicon|Failed to load resource/.test(error));if(functionalErrors.length)throw Error(functionalErrors.slice(0,5).join('; '));
  fs.writeFileSync(path.join(out,'result.json'),JSON.stringify({passed:true,source:process.env.GITHUB_SHA,platform:process.platform,scope:'Real UI import, audio validation, station start, synthetic microphone transport, explicit end and channel-change release. No production outputs or physical microphone.'},null,2));console.log('PASS real operator UI import and microphone workflow');
 }catch(error){fs.writeFileSync(path.join(out,'result.json'),JSON.stringify({passed:false,error:error.message,consoleErrors:failures},null,2));console.error(error);if(window)await screenshot('failure').catch(()=>{});process.exitCode=1;}
 finally {window?.destroy();host?.kill();web?.kill();fs.closeSync(log);app.exit(process.exitCode||0);}
});
