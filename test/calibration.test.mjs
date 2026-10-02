import test from 'node:test';
import assert from 'node:assert/strict';
import {validatePoints,mapBrightness} from '../brightness.mjs';
test('Custom 40% → 15% point is honored and neighboring values interpolate',()=>{
  const points=validatePoints([{screen:100,keypad:40},{screen:40,keypad:15},{screen:0,keypad:10}]);
  assert.equal(mapBrightness(40,points),15);
  assert.equal(mapBrightness(20,points),13);
  assert.equal(mapBrightness(70,points),28);
  assert.equal(mapBrightness(-1,points),10);
  assert.equal(mapBrightness(101,points),40);
});
test('Ambiguous, incomplete and out-of-range tables are rejected',()=>{
  for(const points of [[],[{screen:0,keypad:10},{screen:40,keypad:20}], [{screen:0,keypad:10},{screen:0,keypad:15},{screen:100,keypad:40}], [{screen:0,keypad:0},{screen:100,keypad:40}], [{screen:0,keypad:10},{screen:100,keypad:101}]])assert.throws(()=>validatePoints(points));
});
