import test from 'node:test';
import assert from 'node:assert/strict';
import {createLogitechMonitor,intervalMs,validateSample,executable} from '../logitech-monitor.mjs';
test('five-minute sampling, sustained CPU, process replacement and failed samples',async()=>{
 let time=1000000,reads=0,seconds=30,id='1:start',fail=false,saved;
 const monitor=createLogitechMonitor({now:()=>time,read:async()=>{reads++;if(fail)throw new Error('gone');return {identity:id,cpuSeconds:seconds,memoryMiB:350};},save:value=>saved=value});
 assert.equal((await monitor.tick()).state,'baseline');
 time+=intervalMs-1;await monitor.tick();assert.equal(reads,1);
 time++;seconds+=270;assert.equal((await monitor.tick()).cpuPercent,90);assert.equal(monitor.view().state,'ok');
 time+=intervalMs;seconds+=270;assert.equal((await monitor.tick()).state,'high');assert.equal(monitor.view().automaticRestart,false);
 time+=intervalMs;id='2:start';seconds=1;assert.equal((await monitor.tick()).state,'baseline');assert.equal(monitor.view().cpuPercent,null);
 time+=intervalMs;seconds=0;assert.equal((await monitor.tick()).cpuPercent,null);
 time+=intervalMs;fail=true;assert.equal((await monitor.tick()).state,'unavailable');
 for(let i=0;i<300;i++){time+=intervalMs;await monitor.tick();}
 assert.equal(saved.history.length,288);assert.equal(JSON.stringify(saved).includes('identity'),false);
});
test('overlapping checks cannot create extra samples',async()=>{
 let finish,reads=0;
 const monitor=createLogitechMonitor({read:()=>{reads++;return new Promise(resolve=>finish=resolve);}});
 const first=monitor.tick();await monitor.tick();assert.equal(reads,1);finish(null);await first;assert.equal(monitor.view().state,'missing');
});
test('manual restart resets the CPU interval and retains bounded history',async()=>{
 let time=1000,seconds=0,saved;
 const monitor=createLogitechMonitor({now:()=>time,read:async()=>({identity:'same',cpuSeconds:seconds,memoryMiB:200}),save:value=>saved=value});
 await monitor.tick();time+=intervalMs;seconds=270;await monitor.tick();assert.equal(monitor.view().cpuPercent,90);
 monitor.reset();assert.equal(monitor.view().cpuPercent,null);await monitor.tick();assert.equal(monitor.view().state,'baseline');assert.equal(saved.history.length,3);
});

test('successful sample clears timeout diagnostics and restores immediate memory',async()=>{
 let time=1000,fail=true;
 const monitor=createLogitechMonitor({now:()=>time,read:async()=>{if(fail)throw Object.assign(new Error('timed out'),{killed:true});return {identity:'same',cpuSeconds:1,memoryMiB:210};}});
 assert.equal((await monitor.tick()).reason,'timeout');
 time+=intervalMs;fail=false;const recovered=await monitor.tick();
 assert.equal(recovered.state,'baseline');assert.equal(recovered.memoryMiB,210);assert.equal(recovered.reason,null);
});

test('a failed check retains explicitly stale values and next CPU average spans the missed interval',async()=>{
 let time=1000,seconds=0,fail=false;
 const monitor=createLogitechMonitor({now:()=>time,read:async()=>{if(fail)throw new Error('temporary');return {identity:'same',cpuSeconds:seconds,memoryMiB:200};}});
 await monitor.tick();time+=intervalMs;seconds=30;await monitor.tick();const good=monitor.view();
 time+=intervalMs;fail=true;await monitor.tick();assert.equal(monitor.view().state,'unavailable');assert.equal(monitor.view().cpuPercent,10);assert.equal(monitor.view().memoryMiB,200);assert.equal(monitor.view().lastSuccessfulAt,good.lastSuccessfulAt);
 time+=intervalMs;seconds=90;fail=false;await monitor.tick();assert.equal(monitor.view().state,'ok');assert.equal(monitor.view().cpuPercent,10);
});

test('native samples require the exact executable, current owner, PID identity and finite values',()=>{
 const sample={uid:501,executable,pid:100,identity:'100:start',cpuSeconds:1,memoryMiB:200};
 assert.equal(validateSample(sample,501),sample);assert.equal(validateSample(null,501),null);
 for(const patch of [{uid:502},{executable:'/tmp/LogiPluginService'},{pid:0},{identity:'other'},{cpuSeconds:NaN},{memoryMiB:-1}])assert.throws(()=>validateSample({...sample,...patch},501));
});
