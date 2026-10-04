import test from 'node:test';
import assert from 'node:assert/strict';
import {restartLogitech,createLogitechRestarter} from '../logitech-restart.mjs';
import {executable} from '../logitech-monitor.mjs';
const old={pid:100,identity:'100:old',uid:501,executable},fresh={pid:101,identity:'101:new',uid:501,executable};
test('manual restart verifies owner and executable, waits for exit, starts official app in background',async()=>{
  let service=old;const calls=[];
  await restartLogitech({uid:501,read:async()=>service,wait:async()=>{},execute:async(file,args)=>{
    calls.push([file,args]);
    if(file==='/bin/ps')return {stdout:'501 '+executable+'\n'};
    if(file==='/bin/kill')service=null;
    if(file==='/usr/bin/open')service=fresh;
    return {stdout:''};
  }});
  assert.deepEqual(calls.map(c=>c[0]),['/bin/kill','/usr/bin/open']);
  assert.deepEqual(calls[0][1],['-TERM','100']);
  assert.deepEqual(calls[1][1],['-g','-a','/Applications/Utilities/LogiPluginService.app']);
});
test('changed identity, wrong owner or executable cannot be terminated',async()=>{
  for(const scenario of ['identity','owner','executable']){
    let reads=0;const calls=[];
    await assert.rejects(restartLogitech({uid:501,read:async()=>{
      reads++;if(reads===1)return old;
      return scenario==='identity'?fresh:scenario==='owner'?{...old,uid:502}:{...old,executable:'/tmp/LogiPluginService'};
    },execute:async(file)=>{calls.push(file);return {stdout:''};}}));
    assert.deepEqual(calls,[]);
  }
});
test('a stuck process is not force killed or duplicated',async()=>{
  const calls=[];
  await assert.rejects(restartLogitech({uid:501,read:async()=>old,wait:async()=>{},execute:async(file,args)=>{calls.push([file,args]);return {stdout:'501 '+executable};}}),/не завершилася/);
  assert.equal(calls.filter(c=>c[0]==='/bin/kill').length,1);
  assert.equal(calls.some(c=>c[0]==='/usr/bin/open'),false);
});
test('a service already relaunched by macOS is not launched twice',async()=>{
  let service=old;const calls=[];
  await restartLogitech({uid:501,read:async()=>service,execute:async(file,args)=>{calls.push(file);if(file==='/bin/kill')service=fresh;return {stdout:'501 '+executable};}});
  assert.equal(calls.includes('/usr/bin/open'),false);
});
test('missing service starts quietly; launch failure is reported',async()=>{
  let service=null,opens=0;
  await restartLogitech({read:async()=>service,execute:async(file,args)=>{assert.equal(file,'/usr/bin/open');opens++;service=fresh;},wait:async()=>{}});
  assert.equal(opens,1);
  await assert.rejects(restartLogitech({read:async()=>null,execute:async()=>{},wait:async()=>{}}),/ще не запустилася/);
});
test('overlapping requests and cooldown cannot trigger repeated restarts',async()=>{
  let time=1000,finish,calls=0,resets=0;
  const controller=createLogitechRestarter({now:()=>time,restart:()=>{calls++;return new Promise(resolve=>finish=resolve);},onSuccess:()=>resets++});
  const first=controller.start();assert.equal(controller.view().busy,true);
  await assert.rejects(controller.start());finish();await first;
  assert.equal(calls,1);assert.equal(resets,1);assert.equal(controller.view().state,'complete');
  await assert.rejects(controller.start());time+=15000;
  const next=controller.start();finish();await next;assert.equal(calls,2);
});
test('failed restarts expose a safe message and release the busy state',async()=>{
  const controller=createLogitechRestarter({restart:async()=>{throw new Error('/private/sensitive command details');}});
  await assert.rejects(controller.start(),/Не вдалося/);
  assert.equal(controller.view().state,'failed');assert.equal(controller.view().busy,false);
  assert.equal(controller.view().error.includes('sensitive'),false);
});
