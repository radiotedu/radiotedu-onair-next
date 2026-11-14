'use strict';
// Runs only for successful trusted tag builds. Never publishes PR artifacts.
const fs=require('node:fs/promises'),path=require('node:path'),crypto=require('node:crypto'),cp=require('node:child_process');
const repo='radiotedu/radiotedu-onair-next',api='https://api.github.com/repos/'+repo;
async function request(url,options={}){const response=await fetch(url,{...options,headers:{Accept:'application/vnd.github+json','X-GitHub-Api-Version':'2022-11-28',Authorization:'Bearer '+process.env.GITHUB_TOKEN,...options.headers}});if(!response.ok)throw Error('GitHub request failed: '+response.status+' '+new URL(url).pathname);return response;}
(async()=>{
 if(process.env.GITHUB_ACTIONS!=='true'||!process.env.GITHUB_TOKEN)throw Error('Trusted GitHub release job required.');
 const event=JSON.parse(await fs.readFile(process.env.GITHUB_EVENT_PATH,'utf8')),trigger=event.workflow_run;
 if(!trigger||trigger.event!=='push'||trigger.conclusion!=='success'||trigger.repository?.full_name!==repo)return;
 const tag=trigger.head_branch;if(!/^v\d+\.\d+\.\d+(?:-preview\.\d+)?$/.test(tag??''))return;
 const ref=await(await request(api+'/git/ref/tags/'+encodeURIComponent(tag))).json();if(ref.object.type!=='commit'||ref.object.sha!==trigger.head_sha)throw Error('Release tag must point directly to the tested commit.');
 const runs=await(await request(api+'/actions/runs?head_sha='+trigger.head_sha+'&event=push&per_page=100')).json();
 const chosen=[];for(const name of ['Windows build and unit tests','macOS engine and setup qualification']){
  const run=runs.workflow_runs.filter(r=>r.name===name&&r.head_branch===tag).sort((a,b)=>b.id-a.id)[0];
  if(!run||run.status!=='completed')return;if(run.conclusion!=='success')throw Error('A required installer qualification failed: '+name);chosen.push(run);
 }
 const directory=path.resolve('artifacts/release-assets');await fs.mkdir(directory,{recursive:true});
 const expected=new Set(['onair-next-windows-setup','onair-next-setup-osx-arm64','onair-next-setup-osx-x64']);
 for(const run of chosen){const data=await(await request(api+'/actions/runs/'+run.id+'/artifacts?per_page=100')).json();for(const artifact of data.artifacts){if(!expected.has(artifact.name))continue;if(artifact.expired)throw Error('Qualified artifact expired.');expected.delete(artifact.name);
  const archive=path.join(directory,artifact.name+'.zip'),response=await request(api+'/actions/artifacts/'+artifact.id+'/zip');const stream=await import('node:stream/promises'),nodeStream=await import('node:stream'),nativeFs=await import('node:fs');await stream.pipeline(nodeStream.Readable.fromWeb(response.body),nativeFs.createWriteStream(archive,{flags:'wx'}));
  cp.execFileSync('python3',['-c','import pathlib,sys,zipfile; root=pathlib.Path(sys.argv[2]).resolve(); z=zipfile.ZipFile(sys.argv[1]); assert all((root/n.filename).resolve().is_relative_to(root) for n in z.infolist()); z.extractall(root)',archive,path.join(directory,artifact.name)],{stdio:'inherit'});
 }}if(expected.size)throw Error('Missing qualified installers: '+[...expected].join(', '));
 const assets=[];async function walk(root){for(const item of await fs.readdir(root,{withFileTypes:true})){const full=path.join(root,item.name);if(item.isDirectory())await walk(full);else if(/\.(exe|pkg)$/.test(item.name))assets.push(full);}}await walk(directory);
 if(assets.length!==3)throw Error('Expected exactly three installer binaries.');
 const sums=[];for(const file of assets){const digest=crypto.createHash('sha256');const {createReadStream}=await import('node:fs');for await(const chunk of createReadStream(file))digest.update(chunk);sums.push(digest.digest('hex')+'  '+path.basename(file));}
 const checksums=path.join(directory,'SHA256SUMS.txt');await fs.writeFile(checksums,sums.join('\n')+'\n');assets.push(checksums);
 const notes='Station-bound microphone panel, assigned-live-folder imports, guided Icecast setup, native desktop window and Windows / Apple Silicon / Intel Mac installers.\n\nDeveloper: RadioTEDU Technologies / RTAI.\n\nQualification: both platform workflows passed for '+trigger.head_sha+'. See the Actions runs for unit, real loopback, installation and synthetic microphone UI evidence. This is a preview; physical audio devices, sustained deployment and actual-PC commissioning remain separate. Existing live installations were not deployed by these builds.\n\nSigning: publisher information identifies RadioTEDU. Windows setup is unsigned; macOS uses ad-hoc signatures and is not Apple Developer ID notarized. These packages do not claim verified publisher trust. Fresh-install packages preserve existing broadcasters; use reviewed updates for an installed system.\n\nSee docs/OPERATOR_SETUP_AND_MICROPHONE.md in the repository.';
 let release;const existing=await fetch(api+'/releases/tags/'+encodeURIComponent(tag),{headers:{Authorization:'Bearer '+process.env.GITHUB_TOKEN,Accept:'application/vnd.github+json'}});
 if(existing.ok)release=await existing.json();else if(existing.status===404)release=await(await request(api+'/releases',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({tag_name:tag,target_commitish:trigger.head_sha,name:'RadioTEDU OnAir Next '+tag,body:notes,draft:true,prerelease:true})})).json();else throw Error('Cannot inspect existing release.');
 if(!release.draft)throw Error('This release is already public; refusing to replace its files.');
 for(const file of assets){const bytes=await fs.readFile(file),name=path.basename(file);const duplicate=(release.assets??[]).find(a=>a.name===name);if(duplicate){if(duplicate.digest!=='sha256:'+crypto.createHash('sha256').update(bytes).digest('hex'))throw Error('An existing draft asset differs: '+name);continue;}
  await request(release.upload_url.replace(/\{.*$/,'')+'?name='+encodeURIComponent(name),{method:'POST',headers:{'Content-Type':'application/octet-stream'},body:bytes});
 }
 await request(api+'/releases/'+release.id,{method:'PATCH',headers:{'Content-Type':'application/json'},body:JSON.stringify({draft:false,prerelease:true,body:notes})});console.log('Published qualified preview installers for '+tag);
})().catch(error=>{console.error(error.message);process.exitCode=1;});
