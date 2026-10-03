import test from 'node:test';
import assert from 'node:assert/strict';
import {userInfo} from 'node:os';
import {EventEmitter} from 'node:events';
import {createClaudeLogin} from '../claude-login.mjs';
function fakeChild(){const child=new EventEmitter();child.stdout={resume(){}};child.stderr={resume(){}};child.kill=()=>{child.killed=true;};return child;}
test('login delegates only the subscription auth command and ignores output',()=>{
  let call;const child=fakeChild();const login=createClaudeLogin({executable:'/fake/claude',cwd:'/private/runtime',spawnProcess:(...args)=>{call=args;return child;}});
  assert.equal(login.start().state,'signing_in');
  assert.deepEqual(call[1],['auth','login','--claudeai']);
  assert.deepEqual(call[2].stdio,['ignore','pipe','pipe']);
  assert.equal(call[2].cwd,'/private/runtime');
  assert.equal(call[2].env.ANTHROPIC_API_KEY,undefined);
  assert.equal(call[2].env.USER,userInfo().username);
  assert.equal(call[2].env.LOGNAME,userInfo().username);
  login.stop();assert.equal(child.killed,true);
});
test('a second click cannot start a concurrent login; cancellation permits a later attempt',()=>{
  let calls=0;const login=createClaudeLogin({executable:'/fake/claude',spawnProcess:()=>{calls++;return fakeChild();}});
  login.start();login.start();assert.equal(calls,1);login.cancel();login.start();assert.equal(calls,2);login.stop();
});
test('only successful auth triggers a usage refresh',async()=>{
  let refreshes=0;let child=fakeChild();const login=createClaudeLogin({executable:'/fake/claude',spawnProcess:()=>child,onSuccess:async()=>{refreshes++;return {state:'ok'};}});
  login.start();child.emit('close',1);await Promise.resolve();assert.equal(refreshes,0);assert.equal(login.view().state,'failed');
  child=fakeChild();login.start();child.emit('close',0);await new Promise(r=>setImmediate(r));assert.equal(refreshes,1);assert.equal(login.view().state,'complete');login.stop();
});
test('raw errors and missing executables are not exposed or treated as success',()=>{
  const missing=createClaudeLogin({executable:null});assert.deepEqual(missing.start(),{available:false,state:'unavailable'});
  const child=fakeChild();const login=createClaudeLogin({executable:'/fake/claude',spawnProcess:()=>child});login.start();child.emit('error',new Error('secret token'));assert.deepEqual(login.view(),{available:true,state:'failed'});login.stop();
});

test('successful login does not claim quota success if the read fails',async()=>{
 const child=fakeChild();const login=createClaudeLogin({executable:'/fake/claude',spawnProcess:()=>child,onSuccess:async()=>({state:'access_denied'})});
 login.start();child.emit('close',0);assert.equal(login.view().state,'verifying');
 await new Promise(r=>setImmediate(r));assert.equal(login.view().state,'usage_unavailable');login.stop();
});
