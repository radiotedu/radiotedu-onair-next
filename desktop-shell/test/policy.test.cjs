'use strict';
const test=require('node:test'),assert=require('node:assert/strict');
const p=require('../policy.cjs');
const good={product:'RadioTEDU-OnAirNext',processId:42,version:'1.0'};
const descriptor={ProcessId:42,Version:'1.0',Token:'a'.repeat(64)};
test('only the local console origin may navigate',()=>{
 assert.equal(p.allowedNavigation('http://127.0.0.1:18121/#access=x'),true);
 for(const bad of ['http://127.0.0.1:18122/','http://127.0.0.1.evil:18121/','https://127.0.0.1:18121/','http://u:p@127.0.0.1:18121/','file:///C:/test'])assert.equal(p.allowedNavigation(bad),false);
});
test('only a matching private session descriptor is accepted',()=>{
 assert.equal(p.verifiedDescriptor(good,descriptor),true);
 for(const d of [{...descriptor,ProcessId:43},{...descriptor,Version:'2'},{...descriptor,Token:'short'},null])assert.equal(p.verifiedDescriptor(good,d),false);
 assert.equal(p.verifiedDescriptor({...good,product:'other'},descriptor),false);
 assert.equal(p.consoleUrl(descriptor),p.ORIGIN+'/#access='+descriptor.Token);
});

test('only local audio permissions are allowed',()=>{
 const local='http://127.0.0.1:18121/', remote='https://example.invalid';
 assert.equal(p.allowedPermission('media',local,{mediaTypes:['audio']}),true);
 assert.equal(p.allowedPermission('speaker-selection',local),true);
 for(const [kind,url,details] of [['media',local,{mediaTypes:['video']}],['media',local,{mediaType:'video'}],['media',remote,{}],['geolocation',local,{}],['display-capture',local,{}]])assert.equal(p.allowedPermission(kind,url,details),false);
});
