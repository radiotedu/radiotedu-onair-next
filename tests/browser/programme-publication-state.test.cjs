const test=require('node:test');
const assert=require('node:assert/strict');
const state=require('../../src/OnAirNext.Web/wwwroot/programme-publication-state.js');

test('a schedule is confirmed only by matching active durable readback',()=>{
  const pending={engine:'onair-next-dev-service',id:'a'.repeat(32),draftId:'b'.repeat(32),draftVersion:3,publicationVersion:2};
  const published={Id:pending.draftId,Version:3,DraftVersion:3,Active:true};
  assert.equal(state.validPending(pending),true);
  assert.equal(state.committed(pending,published),true);
  assert.equal(state.committed(pending,{...published,Active:false}),false);
  assert.equal(state.committed(pending,{...published,DraftVersion:2}),false);
  assert.equal(state.committed(pending,{...published,Version:4}),false);
  assert.equal(state.committed({...pending,id:'bad'},published),false);
  const cancelling={...pending,kind:'unpublish'};
  assert.equal(state.unpublished(cancelling,{...published,Active:false}),true);
  assert.equal(state.unpublished(cancelling,published),false);
  assert.equal(state.unpublished(cancelling,{...published,Active:false,Version:4}),false);
});

test('the controls distinguish unavailable, unscheduled, active and edited drafts',()=>{
  assert.deepEqual(state.describe(null,1,false),{text:'Scheduler unavailable. Current broadcasts continue.',schedule:false,cancel:false});
  assert.equal(state.describe({Active:true},1,false).cancel,true);
  assert.equal(state.describe(null,1,true).schedule,true);
  assert.deepEqual(state.describe({Active:true,DraftVersion:1},1,true),{text:'Scheduled · saved version 1.',schedule:false,cancel:true});
  const newer=state.describe({Active:true,DraftVersion:1},2,true);
  assert.equal(newer.schedule,true);assert.equal(newer.cancel,true);assert.match(newer.text,/earlier/);
});
