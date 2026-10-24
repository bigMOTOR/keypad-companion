import test from 'node:test';
import assert from 'node:assert/strict';
import {createLogitechMonitor,measureLogitech,intervalMs,validateSample,executable} from '../logitech-monitor.mjs';
function clocked(){let time=1000,seconds=0,reads=0,fail=false,id='100:start';return {now:()=>time,wait:async ms=>{time+=ms;seconds+=ms/1000*.6;},read:async()=>{reads++;if(fail)throw Object.assign(new Error('timeout'),{killed:true});return {identity:id,pid:100,cpuSeconds:seconds,memoryMiB:200};},advance:ms=>time+=ms,setFail:value=>fail=value,setId:value=>id=value,get reads(){return reads;}};}
test('two-hour checks use a short current CPU sample and suppress all intervening process reads',async()=>{
 const c=clocked();let saved,checked=0;const m=createLogitechMonitor({...c,save:s=>saved=s,onCheck:async()=>checked++});
 assert.equal(intervalMs,7200000);await m.tick();assert.equal(m.view().cpuPercent,60);assert.equal(m.view().intervalMinutes,120);assert.equal(c.reads,2);assert.equal(checked,1);
 await m.tick();c.advance(intervalMs-3001);await m.tick();assert.equal(c.reads,2);c.advance(1);await m.tick();assert.equal(c.reads,4);assert.equal(checked,2);assert.equal(JSON.stringify(saved).includes('identity'),false);
});
test('overlap cannot launch another sample',async()=>{let finish,reads=0;const m=createLogitechMonitor({read:()=>{reads++;return new Promise(r=>finish=r);}});const first=m.tick();await m.tick();assert.equal(reads,1);finish(null);await first;assert.equal(m.view().state,'missing');});
test('failed checks preserve timestamped last success; recovery and reset obtain a fresh short CPU sample',async()=>{
 const c=clocked();let saved;const m=createLogitechMonitor({...c,save:s=>saved=s});await m.tick();const first=m.view();c.advance(intervalMs);c.setFail(true);await m.tick();assert.equal(m.view().state,'unavailable');assert.equal(m.view().cpuPercent,60);assert.equal(m.view().lastSuccessfulAt,first.lastSuccessfulAt);assert.equal(m.view().reason,'timeout');
 c.advance(intervalMs);c.setFail(false);await m.tick();assert.equal(m.view().reason,null);m.reset();assert.equal(m.view().cpuPercent,null);await m.tick();assert.equal(m.view().cpuPercent,60);
 c.advance(25*3600000);await m.tick();assert.equal(saved.history.length,1);
});
test('PID replacement and invalid CPU time cannot create a high CPU reading',async()=>{
 let at=0,n=0;const sample=await measureLogitech({now:()=>at,wait:async ms=>at+=ms,read:async()=>({identity:++n===1?'old':'new',cpuSeconds:10,memoryMiB:200})});assert.equal(sample,null);
 n=0;at=0;const backwards=await measureLogitech({now:()=>at,wait:async ms=>at+=ms,read:async()=>({identity:'same',cpuSeconds:++n===1?100:1,memoryMiB:200})});assert.equal(backwards.cpuPercent,null);
});
test('native samples require the exact executable, current owner, PID identity and finite values',()=>{
 const sample={uid:501,executable,pid:100,identity:'100:start',cpuSeconds:1,memoryMiB:200};
 assert.equal(validateSample(sample,501),sample);assert.equal(validateSample(null,501),null);
 for(const patch of [{uid:502},{executable:'/tmp/LogiPluginService'},{pid:0},{identity:'other'},{cpuSeconds:NaN},{memoryMiB:-1}])assert.throws(()=>validateSample({...sample,...patch},501));
});
