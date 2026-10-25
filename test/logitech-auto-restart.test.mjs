import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtempSync,readFileSync,writeFileSync,rmSync,statSync,existsSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {createLogitechAutoRestart,requestRestartSafety,captureRestartDiagnostic,recoveryIntervalMs} from '../logitech-auto-restart.mjs';
const high={identity:'100:old',pid:100,cpuPercent:65,memoryMiB:200};
function fixture(options={}){
 let time=1000,saved,enabled=true;const calls=[];
 const policy=createLogitechAutoRestart({now:()=>time,wait:async ms=>{calls.push('wait');time+=ms;},enabled:()=>enabled,load:()=>saved,save:s=>saved={...s},measure:async()=>{calls.push('measure');return {...high};},guard:async()=>{calls.push('guard');return {safe:true};},diagnose:async()=>calls.push('diagnose'),restart:async(_,beforeStop)=>{await beforeStop();calls.push('restart');},...options});
 return {policy,calls,advance:()=>time+=recoveryIntervalMs,disable:()=>enabled=false,get saved(){return saved;}};
}
test('arming delay, repeated measurement, diagnostics and final fresh guard precede automatic restart',async()=>{
 const f=fixture();await f.policy.consider(high);assert.deepEqual(f.calls,[]);f.advance();await f.policy.consider(high);
 assert.deepEqual(f.calls,['guard','wait','measure','guard','diagnose','guard','restart']);assert.equal(f.policy.view().state,'restarted');assert.equal(f.saved.awaitingRecovery,true);
 await f.policy.consider(high);assert.equal(f.calls.filter(s=>s==='restart').length,1);
 f.advance();await f.policy.consider(high);assert.equal(f.policy.view().state,'needs-attention');assert.equal(f.calls.filter(s=>s==='restart').length,1);
});
test('recording and unknown recording state block before confirmation',async()=>{
 for(const reason of ['recording','unavailable']){
  const f=fixture({guard:async()=>({safe:false,reason})});f.advance();await f.policy.consider(high);assert.equal(f.policy.view().state,'deferred');assert.equal(f.policy.view().reason,reason);assert.deepEqual(f.calls,[]);
 }
});
test('a recording starting during confirmation or after diagnostics prevents termination',async()=>{
 for(const deniedAt of [2,3]){
  let checks=0;const f=fixture({guard:async()=>++checks===deniedAt?{safe:false,reason:'recording'}:{safe:true}});f.advance();await f.policy.consider(high);assert.equal(f.calls.includes('restart'),false);
 }
});
test('transient load, changed PID, disabled recovery or failed diagnostics cannot cause a restart',async()=>{
 for(const options of [{measure:async()=>({...high,cpuPercent:5})},{measure:async()=>({...high,identity:'other'})},{enabled:()=>false},{diagnose:async()=>{throw new Error('disk full');}}]){
  const f=fixture(options);f.advance();await f.policy.consider(high);assert.equal(f.calls.includes('restart'),false);
 }
});
test('memory must exceed 1 GiB on both readings, without confusing CPU percentages with RAM',async()=>{
 const memory={...high,cpuPercent:5,memoryMiB:1200};
 const f=fixture({measure:async()=>memory});f.advance();await f.policy.consider(memory);assert.equal(f.calls.includes('restart'),true);
 const g=fixture({measure:async()=>({...memory,memoryMiB:200})});g.advance();await g.policy.consider(memory);assert.equal(g.calls.includes('restart'),false);
});
test('loop protection survives a helper restart and low load rearms it',async()=>{
 const f=fixture({load:()=>({awaitingRecovery:true,lastAttemptAt:1,lastRestartAt:2})});f.advance();await f.policy.consider(high);assert.equal(f.policy.view().state,'needs-attention');assert.deepEqual(f.calls,[]);
 f.advance();await f.policy.consider({...high,cpuPercent:5});assert.equal(f.saved.awaitingRecovery,false);
});
test('a fresh plugin response requires this request nonce, live host PID, freshness and explicit safe result',async()=>{
 const dir=mkdtempSync(join(tmpdir(),'keypad-safety-'));
 try{
  for(const patch of [{},{hostPid:101},{nonce:'0'.repeat(32)},{checkedAt:'2000-01-01T00:00:00Z'},{safe:false,reason:'recording'},{reason:'unknown'}]){
   let time=Date.now();const now=()=>time;
   const result=await requestRestartSafety(dir,high,{now,timeoutMs:300,wait:async ms=>{time+=ms;const request=JSON.parse(readFileSync(join(dir,'restart-safety-request.json'),'utf8'));writeFileSync(join(dir,'restart-safety-response.json'),JSON.stringify({nonce:request.nonce,hostPid:100,checkedAt:new Date(time).toISOString(),safe:true,reason:'safe',...patch}));}});
   assert.equal(result.safe,Object.keys(patch).length===0);
  }
 }finally{rmSync(dir,{recursive:true,force:true});}
});
test('diagnostics are local, private and replace only the previous bounded snapshot',async()=>{
 const dir=mkdtempSync(join(tmpdir(),'keypad-diagnostic-'));let calls=0;
 try{
  const execute=async(file,args)=>{assert.equal(file,'/usr/bin/sample');assert.deepEqual(args.slice(0,3),['100','2','10']);writeFileSync(args.at(-1),'stack '+(++calls));};
  await captureRestartDiagnostic(dir,high,{execute,read:async()=>high});await captureRestartDiagnostic(dir,high,{execute,read:async()=>high});
  const path=join(dir,'Diagnostics','logitech-last-restart.sample.txt');assert.equal(readFileSync(path,'utf8'),'stack 2');assert.equal(statSync(path).mode&0o777,0o600);assert.equal(statSync(join(dir,'Diagnostics')).mode&0o777,0o700);
  assert.equal(existsSync(join(dir,'Diagnostics','logitech-last-restart.json')),true);
 }finally{rmSync(dir,{recursive:true,force:true});}
});

test('diagnostics do not attach to a replaced process',async()=>{let called=false;await assert.rejects(captureRestartDiagnostic('/unused',high,{read:async()=>({...high,identity:'replacement'}),execute:async()=>called=true}),/process changed/);assert.equal(called,false);});

test('explicit manual restart rearms persistent recovery without an immediate automatic repeat',async()=>{const f=fixture({load:()=>({awaitingRecovery:true})});f.advance();await f.policy.consider(high);assert.equal(f.policy.view().state,'needs-attention');f.policy.manualRestarted();assert.equal(f.saved.awaitingRecovery,false);await f.policy.consider(high);assert.deepEqual(f.calls,[]);f.advance();await f.policy.consider(high);assert.equal(f.calls.includes('restart'),true);});

test('variable sample durations do not postpone a scheduled two-hour recovery check',async()=>{let time=0;let diagnostics=0;const p=createLogitechAutoRestart({now:()=>time,save:()=>{},guard:async()=>({safe:true}),wait:async()=>{},measure:async()=>high,diagnose:async()=>diagnostics++,restart:async(_,beforeStop)=>beforeStop()});time=recoveryIntervalMs+3005;await p.consider({...high,cpuPercent:5},recoveryIntervalMs);time=2*recoveryIntervalMs+3000;await p.consider(high,2*recoveryIntervalMs);assert.equal(diagnostics,1);});

test('disabling recovery during the final safety check prevents termination',async()=>{let enabled=true,n=0;const f=fixture({enabled:()=>enabled,guard:async()=>{if(++n===3)enabled=false;return {safe:true};}});f.advance();await f.policy.consider(high);assert.equal(f.calls.includes('restart'),false);});
