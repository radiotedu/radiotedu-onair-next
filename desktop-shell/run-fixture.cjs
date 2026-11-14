'use strict';
// Wait for GUI-subsystem executables explicitly; PowerShell may otherwise move
// to the next fixture while Electron is still running.
const cp=require('node:child_process'),path=require('node:path'),fs=require('node:fs');
if(process.env.GITHUB_ACTIONS!=='true')throw Error('Disposable CI only.');
const entry=process.argv[2];if(!['build-icons.cjs','test/smoke-main.cjs','test/operator-e2e.cjs'].includes(entry))throw Error('Unknown desktop fixture.');
const exe=path.join(__dirname,'node_modules/electron/dist',process.platform==='darwin'?'Electron.app/Contents/MacOS/Electron':'electron.exe');
const result=cp.spawnSync(exe,[path.join(__dirname,entry)],{cwd:__dirname,stdio:'inherit',windowsHide:false,timeout:240000});
if(result.error)throw result.error;if(result.status!==0)throw Error('Desktop fixture failed: '+entry+' ('+result.status+')');
if(entry==='test/operator-e2e.cjs'){
 const evidence=JSON.parse(fs.readFileSync(path.join(__dirname,'../artifacts/operator-ui/result.json'),'utf8'));
 if(evidence.passed!==true||evidence.source!==process.env.GITHUB_SHA)throw Error('Operator evidence is missing or failed.');
}
