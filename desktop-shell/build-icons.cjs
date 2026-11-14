'use strict';
const {app,BrowserWindow,nativeImage}=require('electron');const fs=require('node:fs'),path=require('node:path');
// CI virtual machines may have no usable GPU; icons use the software renderer.
app.disableHardwareAcceleration();
app.whenReady().then(async()=>{try{
 const svg=fs.readFileSync(path.join(__dirname,'../src/OnAirNext.Web/wwwroot/brand/onair-next.svg'),'utf8');
 const window=new BrowserWindow({width:1024,height:1024,show:false,useContentSize:true,webPreferences:{offscreen:true,nodeIntegration:false,contextIsolation:true,sandbox:true}});
 await window.loadURL('data:text/html;charset=utf-8,'+encodeURIComponent('<html><body style="margin:0;background:transparent"><img width="1024" height="1024" src="data:image/svg+xml;base64,'+Buffer.from(svg).toString('base64')+'"></body></html>'));
 await new Promise(resolve=>setTimeout(resolve,150));const icon=await window.webContents.capturePage();
 fs.writeFileSync(path.join(__dirname,'icon.png'),icon.resize({width:512,height:512,quality:'best'}).toPNG());
 const png=icon.resize({width:256,height:256,quality:'best'}).toPNG(),header=Buffer.alloc(22);header.writeUInt16LE(1,2);header.writeUInt16LE(1,4);header.writeUInt16LE(1,10);header.writeUInt16LE(32,12);header.writeUInt32LE(png.length,14);header.writeUInt32LE(22,18);fs.writeFileSync(path.join(__dirname,'icon.ico'),Buffer.concat([header,png]));
 const set=path.join(__dirname,'OnAirNext.iconset');fs.mkdirSync(set,{recursive:true});for(const size of [16,32,128,256,512])for(const scale of [1,2])fs.writeFileSync(path.join(set,'icon_'+size+'x'+size+(scale===2?'@2x':'')+'.png'),icon.resize({width:size*scale,height:size*scale,quality:'best'}).toPNG());
 window.destroy();console.log('Rendered OnAir Next application icons.');app.exit(0);
 }catch(error){console.error(error);app.exit(1);}});
