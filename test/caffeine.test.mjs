import test from 'node:test';
import assert from 'node:assert/strict';
import {defaultCaffeineMinutes,validateCaffeineMinutes} from '../caffeine.mjs';
test('two hours default; configurable from one minute to 24 hours',()=>{
 assert.equal(defaultCaffeineMinutes,120);
 for(const minutes of [1,15,120,480,1440])assert.equal(validateCaffeineMinutes(minutes),minutes);
 for(const bad of [0,1441,-1,NaN,1.5,'120',null])assert.throws(()=>validateCaffeineMinutes(bad));
});
