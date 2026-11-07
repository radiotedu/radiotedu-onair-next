'use strict';
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),os=require('node:os'),path=require('node:path'),http=require('node:http'),net=require('node:net'),cp=require('node:child_process');
const get=url=>new Promise((resolve,reject)=>{const q=http.get(url,{timeout:2000},r=>{let b='';r.setEncoding('utf8');r.on('data',x=>b+=x);r.on('end',()=>resolve({status:r.statusCode,body:b}))});q.on('timeout',()=>q.destroy(Error('timeout')));q.on('error',reject)});
test('console serves a separately reviewed local web root',async()=>{
 assert.ok(process.env.ONAIR_WEB_DLL,'CI must provide the compiled console DLL');
 const dir=fs.mkdtempSync(path.join(os.tmpdir(),'onair-webroot-')),web=path.join(dir,'webroot');fs.mkdirSync(web);
 fs.writeFileSync(path.join(web,'studio.js'),'/* isolated reviewed override fixture */');
 const available=net.createServer();await new Promise((resolve,reject)=>available.once('error',reject).listen(18121,'127.0.0.1',resolve));await new Promise(resolve=>available.close(resolve));
 const child=cp.spawn('dotnet',[process.env.ONAIR_WEB_DLL],{windowsHide:true,env:{...process.env,LOCALAPPDATA:dir,ASPNETCORE_WEBROOT:web,ASPNETCORE_ENVIRONMENT:'Production'},stdio:['ignore','ignore','pipe']});
 let error='';child.stderr.on('data',x=>error+=x.toString().slice(0,1000));
 try{
  const until=Date.now()+25000;let result;
  while(Date.now()<until){if(child.exitCode!==null)throw Error('Console exited: '+error.slice(-500));try{result=await get('http://127.0.0.1:18121/studio.js');if(result.status===200)break}catch{}await new Promise(r=>setTimeout(r,200))}
  assert.equal(result?.status,200,'reviewed static file not served');assert.equal(result.body,'/* isolated reviewed override fixture */');
 }finally{child.kill();await new Promise(resolve=>{if(child.exitCode!==null)return resolve();child.once('exit',resolve);setTimeout(resolve,3000).unref()})}
});
