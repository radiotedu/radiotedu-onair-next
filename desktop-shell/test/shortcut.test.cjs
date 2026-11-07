'use strict';
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),fsp=require('node:fs/promises'),os=require('node:os'),path=require('node:path');
const {ensureDesktopShortcut}=require('../shortcut.cjs');
function fake(){const links=new Map();return {links,readShortcutLink:p=>{if(!links.has(p))throw Error('not a link');return links.get(p)},writeShortcutLink:(p,op,data)=>{if(op==='replace'&&!fs.existsSync(p))return false;links.set(p,{target:data.target});fs.writeFileSync(p,JSON.stringify(data));return true}}}
test('preserves the browser shortcut before replacing it',async()=>{
 const desktop=await fsp.mkdtemp(path.join(os.tmpdir(),'onair-shortcut-')),shell=fake(),link=path.join(desktop,'RadioTEDU OnAir Next.lnk');
 fs.writeFileSync(link,'browser-link');shell.links.set(link,{target:'C:\\Program Files\\RadioTEDU\\OnAirNext.Setup.exe'});
 const result=await ensureDesktopShortcut(shell,desktop,'C:\\Apps\\OnAir.exe','C:\\Program Files\\RadioTEDU\\OnAirNext.Setup.exe','C:\\Apps\\icon.ico');
 assert.equal(await fsp.readFile(result.backup,'utf8'),'browser-link');assert.equal(shell.readShortcutLink(link).target,'C:\\Apps\\OnAir.exe');
});
test('does not overwrite an unrelated existing shortcut',async()=>{
 const desktop=await fsp.mkdtemp(path.join(os.tmpdir(),'onair-shortcut-')),shell=fake(),link=path.join(desktop,'RadioTEDU OnAir Next.lnk');
 fs.writeFileSync(link,'unrelated');shell.links.set(link,{target:'C:\\Other\\app.exe'});
 const result=await ensureDesktopShortcut(shell,desktop,'C:\\Apps\\OnAir.exe','C:\\Setup\\OnAirNext.Setup.exe','C:\\Apps\\icon.ico');
 assert.notEqual(result.path,link);assert.equal(await fsp.readFile(link,'utf8'),'unrelated');assert.equal(shell.readShortcutLink(result.path).target,'C:\\Apps\\OnAir.exe');
});
