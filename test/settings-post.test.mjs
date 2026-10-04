import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
const html=readFileSync(new URL('../settings.html',import.meta.url),'utf8');
const postCode=html.slice(html.indexOf('async function post('),html.indexOf('for(const button of document.querySelectorAll'));
const response=(status,body)=>({status,ok:status>=200&&status<300,json:async()=>body});
function client(fetch){return vm.runInNewContext("let token='old';"+postCode+';post',{fetch});}
test('open settings recover stale authorization without losing the entered duration',async()=>{
 const calls=[];const token='a'.repeat(48);
 const post=client(async(url,options)=>{calls.push([url,options]);return calls.length===1?response(403,{error:'expired'}):url==='/session'?response(200,{token}):response(200,{ok:true});});
 assert.equal((await post('/caffeine/settings',{durationMinutes:180})).ok,true);
 assert.deepEqual(calls.map(c=>c[0]),['/caffeine/settings','/session','/caffeine/settings']);
 assert.equal(calls[0][1].body,calls[2][1].body);assert.equal(calls[2][1].headers['X-Keypad-Token'],token);
});
test('invalid session response never repeats a settings write',async()=>{
 let writes=0;
 const post=client(async url=>url==='/session'?response(200,{token:'bad'}):(writes++,response(403,{})));
 await assert.rejects(post('/caffeine/settings',{durationMinutes:180}),/Онови панель/);assert.equal(writes,1);
});
test('empty replies and disconnected helper give Ukrainian errors, not Safari parser text',async()=>{
 const empty=client(async()=>({ok:false,status:400,json:async()=>{throw new SyntaxError('The string did not match the expected pattern.');}}));
 await assert.rejects(empty('/caffeine/settings',{}),/Помічник не підтвердив/);
 const offline=client(async()=>{throw new Error('network');});await assert.rejects(offline('/caffeine/settings',{}),/Немає зв’язку/);
});
