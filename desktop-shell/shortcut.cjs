'use strict';
const fs=require('node:fs/promises'),path=require('node:path'),constants=require('node:fs').constants;
const equal=(a,b)=>typeof a==='string'&&typeof b==='string'&&path.win32.normalize(a).toLowerCase()===path.win32.normalize(b).toLowerCase();
async function exists(p){try{await fs.access(p);return true}catch(e){if(e.code==='ENOENT')return false;throw e}}
async function unique(dir,stem){for(let i=0;i<1000;i++){const p=path.join(dir,stem+(i?' '+i:'')+'.lnk');if(!await exists(p))return p}throw new Error('No free desktop shortcut name.')}
async function ensureDesktopShortcut(shell,desktop,exe,legacySetup,icon){
 let link=path.join(desktop,'RadioTEDU OnAir Next.lnk'),operation='create',backup=null;
 if(await exists(link)){
  let prior;try{prior=shell.readShortcutLink(link)}catch{prior=null}
  if(prior&&equal(prior.target,exe))return {path:link,existing:true};
  if(prior&&equal(prior.target,legacySetup)){
   backup=await unique(desktop,'RadioTEDU OnAir Next (Tarayıcı)');
   await fs.copyFile(link,backup,constants.COPYFILE_EXCL);
   const current=shell.readShortcutLink(link);if(!equal(current.target,legacySetup))throw new Error('Desktop shortcut changed while preserving it.');
   operation='replace';
  }else link=await unique(desktop,'RadioTEDU OnAir Next (Uygulama)');
 }
 if(!shell.writeShortcutLink(link,operation,{target:exe,cwd:path.dirname(exe),icon,description:'RadioTEDU OnAir Next desktop console'}))throw new Error('Desktop shortcut could not be created.');
 const saved=shell.readShortcutLink(link);if(!equal(saved.target,exe))throw new Error('Desktop shortcut verification failed.');
 return {path:link,backup,existing:false};
}
module.exports={ensureDesktopShortcut};
