'use strict';
const {app,BrowserWindow,Tray,Menu,dialog,shell,session,nativeImage}=require('electron');
const {spawn,execFileSync}=require('node:child_process');
const fs=require('node:fs/promises'),http=require('node:http'),path=require('node:path'),crypto=require('node:crypto');
const {ORIGIN,allowedNavigation,allowedPermission,shouldRetrySession,verifiedDescriptor,consoleUrl}=require('./policy.cjs');
const {ensureDesktopShortcut}=require('./shortcut.cjs');
let window=null,tray=null,quitting=false,opening=null,pendingOpen=false;
// The descriptor belongs to the signed-in operator, independently of Electron's profile.
const mac=process.platform==='darwin';
const macPaths=mac&&app.isPackaged?JSON.parse(execFileSync(path.join(process.resourcesPath,'engine','OnAirNext.Host'),['--macos-paths'],{encoding:'utf8',timeout:5000,env:{...process.env,TMPDIR:'/tmp'}})):null;
const endpoint=macPaths?.descriptor || path.join(process.env.LOCALAPPDATA||'', 'RadioTEDU','OnAirNext-Dev','browser-console','access','console.json');
const setupArgument=process.argv.indexOf('--setup-package');
const setupPackage=setupArgument>=0&&process.argv[setupArgument+1]?path.resolve(process.argv[setupArgument+1]):null;
const web=mac?path.join(process.resourcesPath,'console','OnAirNext.Web'):setupPackage?path.join(setupPackage,'console','OnAirNext.Web.exe'):path.join(process.env.ProgramFiles||'C:\\Program Files','RadioTEDU-OnAirNext-Development','console','OnAirNext.Web.exe');
function health(){return new Promise((resolve,reject)=>{
 let body='',done=false;const finish=(error,value)=>{if(done)return;done=true;error?reject(error):resolve(value)};
 const req=http.get(ORIGIN+'/api/health',{agent:false,timeout:5000},res=>{
  if(res.statusCode!==200){res.resume();return finish(new Error('The console port is occupied by another application.'))}
  res.setEncoding('utf8');res.on('data',x=>{body+=x;if(body.length>4096)req.destroy(new Error('Invalid console response.'))});
  res.on('end',()=>{try{finish(null,JSON.parse(body))}catch{finish(new Error('Invalid console response.'))}});
  res.on('error',finish);
 });req.on('timeout',()=>{const error=new Error('Console health check timed out.');error.code='HEALTH_TIMEOUT';req.destroy(error)});
 req.on('error',err=>err.code==='ECONNREFUSED'?finish(null,null):finish(err));
})}
async function readReady(allowNotReady=false){const h=await health();if(!h)return null;let d;
 if(setupPackage){const bundle=JSON.parse(await fs.readFile(path.join(setupPackage,'bundle.json'),'utf8'));if(!/^[0-9a-f]{40}$/i.test(bundle.Source||'')||!h.version?.endsWith('+'+bundle.Source))throw new Error('A different console version is open. Use Close console in its System screen, then open this setup again. Broadcasts continue in the service.');}
 try{d=JSON.parse(await fs.readFile(endpoint,'utf8'))}catch(error){if(allowNotReady&&(error.code==='ENOENT'||error instanceof SyntaxError))return null;throw new Error('Open the console using the Windows account that owns its session.')}
 if(!verifiedDescriptor(h,d))throw new Error('The console session could not be verified.');return d;
}
async function consoleEnvironment(){
 if(mac)return {...process.env,TMPDIR:'/tmp'};
 if(setupPackage)return process.env;
 const base=path.join(process.env.LOCALAPPDATA||'', 'RadioTEDU','OnAirNext-Desktop');
 const root=path.join(base,'webroots','startup-session-cea8313');
 try{
  const content=await fs.readFile(path.join(base,'webroot-manifest-cea8313.json'));
  const digest=value=>crypto.createHash('sha256').update(value).digest('hex');
  if(digest(content)!=='7e38cbbde8d02ce4aab5fe7d3d7538ed01cf40a5038c5cce29c9f5df5b065538')return process.env;
  const manifest=JSON.parse(content);if(manifest.schema!==1||!Array.isArray(manifest.files)||manifest.files.length!==48)return process.env;
  if(digest(await fs.readFile(path.join(path.dirname(web),'OnAirNext.Web.dll')))!==manifest.baseConsoleSha256)return process.env;
  for(const file of manifest.files){if(!/^[a-z0-9._-]+$/i.test(file.name)||digest(await fs.readFile(path.join(root,file.name)))!==file.sha256)return process.env;}
  return {...process.env,ASPNETCORE_WEBROOT:root,ASPNETCORE_ENVIRONMENT:'Production'};
 }catch{return process.env}
}
async function ensureConsole(){
 let ready=null;
 for(let attempt=0;attempt<3;attempt++){try{ready=await readReady();break}catch(error){if(!['HEALTH_TIMEOUT','ECONNRESET','ETIMEDOUT'].includes(error.code)||attempt===2)throw error;await new Promise(r=>setTimeout(r,500))}}
 if(ready)return ready;
 await fs.access(web);const child=spawn(web,[],{detached:true,windowsHide:true,stdio:'ignore',cwd:path.dirname(web),env:await consoleEnvironment()});let spawnError=null;child.on('error',error=>{spawnError=error});child.unref();
 const until=Date.now()+60000;while(Date.now()<until){await new Promise(r=>setTimeout(r,400));if(spawnError)throw new Error('The local console could not start.');ready=await readReady(true);if(ready)return ready;if(child.exitCode!==null)throw new Error('The local console did not start.');}
 throw new Error('The local console did not become ready. Broadcasts continue independently.');
}
function newWindow(){
 const w=new BrowserWindow({width:1450,height:920,minWidth:900,minHeight:600,show:false,title:'RadioTEDU OnAir Next',icon:path.join(__dirname,'icon.png'),
  webPreferences:{backgroundThrottling:false,nodeIntegration:false,contextIsolation:true,sandbox:true,webSecurity:true}});
 w.webContents.on('will-navigate',(event,url)=>{if(!allowedNavigation(url))event.preventDefault()});
 w.webContents.on('will-redirect',(event,url)=>{if(!allowedNavigation(url))event.preventDefault()});
 w.webContents.setWindowOpenHandler(()=>({action:'deny'}));
 let sessionRetries=0;
 w.webContents.on('did-finish-load',()=>{if(sessionRetries>=5)return;setTimeout(async()=>{
  if(w.isDestroyed()||!allowedNavigation(w.webContents.getURL()))return;
  try{const message=await w.webContents.executeJavaScript("document.getElementById('action-status')?.textContent || ''");
   if(shouldRetrySession(message)){sessionRetries++;w.reload()}
  }catch{}
 },8000)});
 w.on('close',event=>{if(!quitting){event.preventDefault();w.hide()}});
 w.on('closed',()=>{if(window===w)window=null});return w;
}
async function showWindow(){
 if(opening)return opening;
 opening=(async()=>{try{const d=await ensureConsole();if(!window)window=newWindow();
  const current=window.webContents.getURL();if(!allowedNavigation(current))await window.loadURL(consoleUrl(d));
  window.show();if(window.isMinimized())window.restore();window.focus();
 }catch(error){dialog.showErrorBox('RadioTEDU OnAir Next',String(error.message||error))}})();
 try{return await opening}finally{opening=null}
}
async function openBrowser(){try{const d=await ensureConsole();await shell.openExternal(consoleUrl(d))}catch(error){dialog.showErrorBox('RadioTEDU OnAir Next',String(error.message||error))}}
async function finishSetupDesktop(){
 const install=path.join(process.env.ProgramFiles||'C:\\Program Files','RadioTEDU-OnAirNext-Development');
 try{
  const receipt=JSON.parse(await fs.readFile(path.join(process.env.ProgramData||'C:\\ProgramData','RadioTEDU-OnAirNext-Development','setup-status.json'),'utf8'));
  const bundle=JSON.parse(await fs.readFile(path.join(setupPackage,'bundle.json'),'utf8'));
  if(receipt.Phase!=='Installed'||receipt.Source!==bundle.Source)return false;
  const exe=path.join(install,'desktop','RadioTEDU OnAir Next.exe');await fs.access(exe);
  await ensureDesktopShortcut(shell,app.getPath('desktop'),exe,path.join(install,'setup','OnAirNext.Setup.exe'),path.join(install,'desktop/resources/app/icon.ico'));
  app.setLoginItemSettings({openAtLogin:true,path:exe,args:['--background']});return true;
 }catch{return false}
}
function makeTray(){const icon=nativeImage.createFromPath(path.join(__dirname,'icon.png'));tray=new Tray(icon);tray.setToolTip('RadioTEDU OnAir Next');
 tray.setContextMenu(Menu.buildFromTemplate([{label:'Open RadioTEDU OnAir Next',click:()=>void showWindow()},{label:'Open in browser',click:()=>void openBrowser()},{type:'separator'},{label:'Exit window',click:()=>{quitting=true;app.quit()}}]));
 tray.on('double-click',()=>void showWindow());}
if(!app.requestSingleInstanceLock())app.quit();else{
 app.on('activate',()=>{void showWindow()});
 app.on('second-instance',(_event,args)=>{if(!args.includes('--background')){if(app.isReady())void showWindow();else pendingOpen=true}});
 app.on('before-quit',()=>{quitting=true});app.on('window-all-closed',()=>{});
 app.whenReady().then(async()=>{
  if(!mac)app.setAppUserModelId('com.radiotedu.onairnext');
  session.defaultSession.setPermissionCheckHandler((_wc,permission,origin,details)=>allowedPermission(permission,origin,details));
  session.defaultSession.setPermissionRequestHandler((wc,permission,callback,details)=>callback(allowedPermission(permission,details?.requestingUrl||wc?.getURL()||'',details)));
  makeTray();if(app.isPackaged){
   if(!setupPackage)app.setLoginItemSettings({openAtLogin:true,path:process.execPath,args:['--background']});
   else {const timer=setInterval(()=>{void finishSetupDesktop().then(done=>{if(done)clearInterval(timer)})},3000);timer.unref();}
   try{if(!mac&&!setupPackage)await ensureDesktopShortcut(shell,app.getPath('desktop'),process.execPath,path.join(process.env.ProgramFiles||'C:\\Program Files','RadioTEDU-OnAirNext-Development','setup','OnAirNext.Setup.exe'),path.join(__dirname,'icon.ico'))}
   catch(error){if(!process.argv.includes('--background'))dialog.showErrorBox('Desktop shortcut',String(error.message||error))}
  }
  if(pendingOpen||!process.argv.includes('--background'))void showWindow();else void ensureConsole().catch(()=>{});
 }).catch(error=>{dialog.showErrorBox('RadioTEDU OnAir Next',String(error.message||error));app.quit()});
}

// Main-process-only seam for the isolated desktop smoke test; not exposed to the renderer.
module.exports={testAccess:{getWindow:()=>window,getTray:()=>tray,showWindow}};
