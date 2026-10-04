import test from 'node:test';
import assert from 'node:assert/strict';
import {sanitizeUsage,createClaudeUsagePoller,claudeUsageView} from '../claude-usage.mjs';
const at=Date.parse('2030-01-01T00:00:00Z');
const good=()=>({remaining:19,resetsAt:at/1000+86400,updatedAt:new Date(at).toISOString()});
test('only allowance numbers survive; provider secrets are discarded',()=>{
  assert.deepEqual(sanitizeUsage({...good(),accessToken:'test-secret',prompt:'private text'},at),good());
  assert.deepEqual(sanitizeUsage({error:'secret error content',accessToken:'test-secret'},at),{error:'usage_unavailable'});
});
test('expired, stale, future and malformed values are rejected',()=>{
  for(const change of [{remaining:NaN},{remaining:101},{remaining:19.5},{resetsAt:at/1000},{updatedAt:'bad'}, {updatedAt:new Date(at-61000).toISOString()},{updatedAt:new Date(at+61000).toISOString()}]){
    assert.deepEqual(sanitizeUsage({...good(),...change},at),{error:'usage_unavailable'});
  }
});
test('no credential check until explicitly enabled',async()=>{
  let calls=0;
  const p=createClaudeUsagePoller({enabled:()=>false,fetchUsage:async()=>{calls++;},saveQuota:()=>{},saveStatus:()=>{},now:()=>at});
  await p.tick();assert.equal(calls,0);
});
test('normal reads every five minutes; expired authorization backs off thirty',async()=>{
  let clock=at,calls=0,state,expired=false;
  const p=createClaudeUsagePoller({enabled:()=>true,fetchUsage:async()=>{calls++;return expired?{error:'sign_in_required'}:good();},saveQuota:()=>{},saveStatus:v=>state=v,now:()=>clock});
  await p.tick();await p.tick();assert.equal(calls,1);
  clock+=300000;expired=true;await p.tick();assert.equal(calls,2);assert.equal(state.state,'sign_in_required');
  clock+=300000;await p.tick();assert.equal(calls,2);
  clock+=1500000;await p.tick();assert.equal(calls,3);
});
test('overlapping reads are suppressed and stopping discards their result',async()=>{
  let resolve,calls=0,writes=0;
  const p=createClaudeUsagePoller({enabled:()=>true,fetchUsage:()=>{calls++;return new Promise(r=>resolve=r);},saveQuota:()=>writes++,saveStatus:()=>writes++,now:()=>at});
  const first=p.tick();await p.tick();assert.equal(calls,1);p.stop();resolve(good());await first;assert.equal(writes,0);
});
test('manual refresh bypasses authorization backoff without duplicate reads',async()=>{
 let calls=0;
 const p=createClaudeUsagePoller({enabled:()=>true,fetchUsage:async()=>{calls++;return {error:'sign_in_required'};},saveQuota:()=>{},saveStatus:()=>{},now:()=>at});
 await p.tick();await p.tick();assert.equal(calls,1);await p.tick(true);assert.equal(calls,2);
});

test('forced refresh waits for a busy read and performs one fresh read',async()=>{
 let resolve,calls=0;const p=createClaudeUsagePoller({enabled:()=>true,fetchUsage:()=>{calls++;return calls===1?new Promise(r=>resolve=r):Promise.resolve(good());},saveQuota:()=>{},saveStatus:()=>{},now:()=>at});
 const first=p.tick();const forced=p.tick(true);const another=p.tick(true);assert.equal(calls,1);
 resolve({error:'expired_credential'});await first;
 assert.equal((await forced).state,'ok');await another;assert.equal(calls,2);
});
test('an error clears the old quota; disabling during a read prevents writes',async()=>{
 let clears=0,enabled=true,resolve,writes=0;
 const p=createClaudeUsagePoller({enabled:()=>enabled,fetchUsage:async()=>({error:'access_denied'}),clearQuota:()=>clears++,saveQuota:()=>writes++,saveStatus:()=>writes++,now:()=>at});
 await p.tick();assert.equal(clears,1);
 const q=createClaudeUsagePoller({enabled:()=>enabled,fetchUsage:()=>new Promise(r=>resolve=r),saveQuota:()=>writes++,saveStatus:()=>writes++,now:()=>at});
 const pending=q.tick();enabled=false;resolve(good());await pending;assert.equal(writes,1);
});


test('temporary failure preserves stale allowance; retries recover without login',async()=>{
 let clock=at,quota,calls=0,clears=0,status;
 const replies=[good(),{error:'usage_unavailable'},{error:'usage_unavailable'},()=>({...good(),remaining:17,updatedAt:new Date(clock).toISOString()})];
 const p=createClaudeUsagePoller({enabled:()=>true,fetchUsage:async()=>{const v=replies[calls++];return typeof v==='function'?v():v;},readQuota:()=>quota,saveQuota:v=>quota=v,clearQuota:()=>{clears++;quota=null;},saveStatus:v=>status=v,now:()=>clock});
 await p.tick();clock+=5*60000;await p.tick();
 assert.equal(quota.remaining,19);assert.equal(quota.updatedAt,good().updatedAt);assert.equal(quota.stale,true);assert.equal(clears,0);
 assert.equal(Date.parse(status.nextCheckAt)-clock,60000);
 assert.deepEqual(claudeUsageView({enabled:true,status,quota,now:clock}).remaining,19);
 assert.equal(claudeUsageView({enabled:true,status,quota,now:clock}).stale,true);
 clock+=59999;await p.tick();assert.equal(calls,2);
 clock++;await p.tick();assert.equal(Date.parse(status.nextCheckAt)-clock,120000);
 clock+=120000;await p.tick();assert.equal(status.state,'ok');assert.equal(quota.remaining,17);assert.equal(quota.stale,undefined);
 assert.equal(claudeUsageView({enabled:true,status,quota,now:clock}).stale,false);
 assert.equal(Date.parse(status.nextCheckAt)-clock,300000);
});
test('long outages stop showing old data; retry delays cap at five minutes',async()=>{
 let clock=at,quota=good(),status,clears=0;
 const p=createClaudeUsagePoller({enabled:()=>true,fetchUsage:async()=>({error:'usage_unavailable'}),readQuota:()=>quota,saveQuota:v=>quota=v,clearQuota:()=>{quota=null;clears++;},saveStatus:v=>status=v,now:()=>clock});
 for(const minutes of [1,2,5,5,5]){await p.tick();assert.equal(Date.parse(status.nextCheckAt)-clock,minutes*60000);clock+=minutes*60000;}
 clock=at+31*60000;await p.tick();assert.equal(clears,1);assert.equal(quota,null);
});
test('cached allowance never survives reset, account failure, disable or malformed data',()=>{
 const status={state:'usage_unavailable'};
 for(const quota of [{...good(),resetsAt:at/1000},{...good(),updatedAt:new Date(at-31*60000).toISOString()},{...good(),remaining:101},{...good(),updatedAt:'bad'}])assert.equal(claudeUsageView({enabled:true,status,quota,now:at}).remaining,null);
 for(const state of ['sign_in_required','team_account_required','access_denied','no_authorized_credential'])assert.equal(claudeUsageView({enabled:true,status:{state},quota:good(),now:at}).remaining,null);
 assert.equal(claudeUsageView({enabled:false,status,quota:good(),now:at}).remaining,null);
});
test('provider cooldown overrides local retry and raw error data is not retained',async()=>{
 let status;
 const p=createClaudeUsagePoller({enabled:()=>true,fetchUsage:async()=>({error:'usage_unavailable',retryAfterSeconds:600,accessToken:'private',body:'private'}),saveQuota:()=>{},saveStatus:v=>status=v,now:()=>at});
 await p.tick();assert.equal(Date.parse(status.nextCheckAt)-at,600000);assert.deepEqual(Object.keys(status).sort(),['checkedAt','nextCheckAt','state']);
 assert.deepEqual(sanitizeUsage({error:'usage_unavailable',retryAfterSeconds:'secret'},at),{error:'usage_unavailable'});
});
