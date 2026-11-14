(() => {
  'use strict';
  window.RadioTeduSetupWizard={create({api,context,roster,navigate,selectStation,refreshSetup}){
    const $=id=>document.getElementById(id);let busy=false,reviewed=null;const rows=new Map();
    const say=value=>$('wizard-feedback').textContent=value;
    function stopped(){const c=context();return c.fresh&&c.service&&c.supported&&c.stations.length===8&&c.stations.every(s=>s.State==='Stopped'&&!s.RequestedRunning&&s.Outputs.every(o=>o.State==='Stopped'&&!o.EncoderPid&&!o.QueuedBlocks));}
    function update(){ $('wizard-review').disabled=busy||!stopped();$('wizard-save').disabled=busy||!stopped()||!reviewed;$('wizard-start-service').disabled=busy;
      $('wizard-gate').textContent=stopped()?'Ready to review new stream destinations. No station will start automatically.':'Output configuration requires the installed engine and all channels stopped. Folder and sweeper screens remain available.'; }
    const mounts={'1':'/classic','2':'/lofi','4':'/radio','5':'/cazz','8':'/rock','9':'/energize','10':'/situation','11':'/maincharacter'};
    for(const station of roster){
      const row=document.createElement('tr');const name=document.createElement('th');name.scope='row';name.textContent=station.name;row.append(name);
      const checkbox=(label,checked=false)=>{const input=document.createElement('input');input.type='checkbox';input.checked=checked;input.setAttribute('aria-label',station.name+' '+label);const td=document.createElement('td');td.append(input);row.append(td);return input;};
      const enabled=checkbox('enable outputs');const td=document.createElement('td');const mount=document.createElement('input');mount.value=mounts[station.id];mount.maxLength=100;mount.setAttribute('aria-label',station.name+' primary mount');td.append(mount);row.append(td);
      const low=checkbox('64 kbps HE-AACv2 output',true),flac=checkbox('lossless FLAC output',station.id==='1'||station.id==='5');
      const action=document.createElement('td');for(const [label,page] of [['Music folder','library'],['Sweepers','ads']]){const button=document.createElement('button');button.type='button';button.className='button';button.textContent=label;button.addEventListener('click',()=>{selectStation(station.id);navigate(page);});action.append(button);}row.append(action);rows.set(station.id,{enabled,mount,low,flac});$('wizard-channels').append(row);
    }
    function profile(){
      const host=$('wizard-host').value.trim(),port=Number($('wizard-port').value),password=$('wizard-password').value,user=$('wizard-user').value.trim();
      if(!host||!Number.isInteger(port)||port<1||port>65535||!password||!user)throw Error('Enter the Icecast host, port, source username and password.');
      return {SchemaVersion:1,Roster:'Custom',Stations:roster.map(station=>{const row=rows.get(station.id),mount=row.mount.value.trim();const outputs=[];
        const add=(suffix,codec)=>outputs.push({Id:station.id+'-'+codec,Host:host,Port:port,Mount:mount+suffix,SourcePassword:password,SourceUsername:user,Profile:codec,QueueBlocks:250,WriteTimeoutMs:2000,Commissioned:true,
          Information:{Name:'RadioTEDU '+station.name,Description:station.name+' radio',Genre:station.name,Url:'https://radiotedu.com',Public:true}});
        if(row.enabled.checked){if(!/^\/[A-Za-z0-9._-]+$/.test(mount))throw Error('Use a single mount name beginning with / for '+station.name+'.');add('','AacLc192');if(row.low.checked)add('-low','HeAacV2_64');if(row.flac.checked)add('-flac','OggFlac');}
        return {StationId:station.id,Outputs:outputs};})};
    }
    function invalidate(){reviewed=null;$('wizard-confirm').checked=false;update();}
    $('wizard-form').addEventListener('submit',event=>event.preventDefault());
    $('wizard-form').addEventListener('input',event=>{if(event.target.id!=='wizard-confirm')invalidate();});
    $('wizard-review').addEventListener('click',()=>{try{const value=profile();const outputs=value.Stations.flatMap(s=>s.Outputs);if(!outputs.length)throw Error('Select at least one channel.');const keys=outputs.map(o=>o.Mount.toLowerCase());if(new Set(keys).size!==keys.length)throw Error('Every mount must have one owner.');reviewed={engine:context().engine,profile:value};$('wizard-summary').textContent=outputs.map(o=>o.Host+':'+o.Port+o.Mount+' · '+o.Profile).join('\n');say('Review every destination below. Passwords are not displayed or saved in browser storage.');update();}catch(error){invalidate();say(error.message);}});
    $('wizard-save').addEventListener('click',async()=>{
      if(!reviewed||!stopped()||!$('wizard-confirm').checked){say('Confirm that these destinations are yours and their former source has been disconnected.');return;}
      const selected=reviewed;if(selected.engine!==context().engine){invalidate();say('Engine changed; review again.');return;}busy=true;update();
      try{const result=await api('/api/command',{method:'POST',body:{engine:selected.engine,request:{Id:crypto.randomUUID(),Action:'commission-output-profile',OutputProfile:selected.profile}}});if(result.accepted!==true)throw Error(result.error??'Output configuration was rejected.');$('wizard-password').value='';reviewed=null;say('Connections saved in protected service storage. Start the stopped service below, reconnect, verify the displayed destinations, then start each selected channel from On Air.');}
      catch(error){reviewed=null;say('Save was not confirmed. Read the engine destinations before retrying. '+error.message);}
      finally{busy=false;update();void refreshSetup();}
    });
    $('wizard-start-service').addEventListener('click',async()=>{busy=true;update();try{await api('/api/setup/start-service',{method:'POST',timeout:120000});say('Service start completed. Reconnect and verify each channel before starting its broadcast.');}catch(error){say(error.message);}finally{busy=false;update();void refreshSetup();}});
    $('wizard-open-installation').addEventListener('click',()=>{$('setup-runtime').scrollIntoView({behavior:'smooth',block:'center'});$('setup-runtime').focus();});
    $('wizard-clear').addEventListener('click',()=>{$('wizard-password').value='';invalidate();say('Unsaved credentials cleared.');});
    update();return {update};
  }};
})();
