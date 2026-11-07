'use strict';
// Disposable loopback fixture. No production service, files or credentials.
const {app,shell}=require('electron'),http=require('node:http'),fs=require('node:fs'),os=require('node:os'),path=require('node:path'),assert=require('node:assert/strict');
app.commandLine.appendSwitch('disable-gpu');
const root=fs.mkdtempSync(path.join(os.tmpdir(),'radiotedu-electron-smoke-'));
process.env.LOCALAPPDATA=root;
const d=path.join(root,'RadioTEDU','OnAirNext-Dev','browser-console','access');fs.mkdirSync(d,{recursive:true});
const token='a'.repeat(64),version='smoke-only';fs.writeFileSync(path.join(d,'console.json'),JSON.stringify({ProcessId:process.pid,Version:version,Token:token}));
const server=http.createServer((req,res)=>{
 if(req.url==='/api/health'){res.writeHead(200,{'Content-Type':'application/json'});return res.end(JSON.stringify({product:'RadioTEDU-OnAirNext',processId:process.pid,version}))}
 if(req.url==='/'){res.writeHead(200,{'Content-Type':'text/html'});return res.end('<!doctype html><title>OnAir test</title><main>Fixture console</main>')}
 res.writeHead(404);res.end();
});
let timer;function fail(error){console.error('Desktop smoke failed: '+error.message);clearTimeout(timer);server.close();app.exit(1)}
process.on('uncaughtException',fail);process.on('unhandledRejection',fail);
timer=setTimeout(()=>fail(new Error('timed out')),30000);
server.listen(18121,'127.0.0.1',async()=>{
 try{
  const target=require('../main.cjs').testAccess;
  async function until(fn){let deadline=Date.now()+20000;while(Date.now()<deadline){if(fn())return;await new Promise(r=>setTimeout(r,50))}throw new Error('expected window state was not reached')}
  await until(()=>target.getWindow()&&target.getWindow().isVisible());const window=target.getWindow();
  assert.ok(target.getTray());assert.equal(new URL(window.webContents.getURL()).origin,'http://127.0.0.1:18121');
  const {ensureDesktopShortcut}=require('../shortcut.cjs');const desktop=path.join(root,'Desktop');fs.mkdirSync(desktop);
  const shortcut=await ensureDesktopShortcut(shell,desktop,process.execPath,path.join(root,'old.exe'),path.join(__dirname,'..','icon.ico'));
  assert.equal(path.resolve(shell.readShortcutLink(shortcut.path).target).toLowerCase(),path.resolve(process.execPath).toLowerCase());
  assert.equal(window.webContents.getLastWebPreferences().nodeIntegration,false);
  assert.equal(window.webContents.getLastWebPreferences().contextIsolation,true);
  assert.equal(window.webContents.getLastWebPreferences().sandbox,true);
  window.close();await until(()=>!window.isVisible());assert.equal(window.isDestroyed(),false);
  await target.showWindow();await until(()=>window.isVisible());
  const popup=await window.webContents.executeJavaScript("window.open('https://example.invalid') === null");assert.equal(popup,true);
  console.log('PASS desktop window, tray, close-to-tray, reopen, secure renderer and popup denial');
  clearTimeout(timer);server.close();app.quit();
 }catch(error){fail(error)}
});
server.on('error',fail);
