'use strict';
const {URL}=require('node:url');
const ORIGIN='http://127.0.0.1:18121';
function allowedNavigation(value){try{const u=new URL(value);return u.origin===ORIGIN&&!u.username&&!u.password}catch{return false}}
function verifiedDescriptor(health,descriptor){
 return !!health&&health.product==='RadioTEDU-OnAirNext'&&Number.isInteger(health.processId)&&health.processId>0&&
  typeof health.version==='string'&&health.version.length>0&&!!descriptor&&descriptor.ProcessId===health.processId&&
  descriptor.Version===health.version&&typeof descriptor.Token==='string'&&/^[0-9a-f]{64}$/i.test(descriptor.Token);
}
function consoleUrl(descriptor){return ORIGIN+'/#access='+descriptor.Token}
module.exports={ORIGIN,allowedNavigation,verifiedDescriptor,consoleUrl};
