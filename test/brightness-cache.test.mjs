import test from 'node:test';
import assert from 'node:assert/strict';
import {createBrightnessCache} from '../brightness-cache.mjs';
test('idle one-second ticks send only one Logitech read per minute',async()=>{
 let time=0,calls=0;const cache=createBrightnessCache({now:()=>time});
 const read=async()=>{calls++;return 20;};
 for(time=0;time<60000;time+=1000)assert.equal(await cache.read(read),20);
 assert.equal(calls,1);await cache.read(read);assert.equal(calls,2);
});
test('screen changes/actions and reconnects verify actual brightness immediately',async()=>{
 let actual=20,calls=0;const cache=createBrightnessCache();const read=async()=>{calls++;return actual;};
 await cache.read(read);actual=35;assert.equal(await cache.read(read,true),35);
 cache.update(40);assert.equal(await cache.read(read),40);
 cache.reset();assert.equal(await cache.read(read),35);assert.equal(calls,3);
});
test('failed fresh reads never advance the cache deadline',async()=>{
 let time=0;const cache=createBrightnessCache({now:()=>time});await cache.read(async()=>20);time=60000;
 await assert.rejects(cache.read(async()=>{throw new Error('offline');}),/offline/);
 assert.equal(await cache.read(async()=>30),30);
});
