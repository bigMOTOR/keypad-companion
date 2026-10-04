import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
import {claudeUsageView} from '../claude-usage.mjs';
const html=readFileSync(new URL('../settings.html',import.meta.url),'utf8');
const render=html.slice(html.indexOf('function extraState(s){'),html.indexOf('\nasync function refresh'));
function panel(){
 const nodes=new Map(),document={querySelector(selector){if(!nodes.has(selector))nodes.set(selector,{style:{},dataset:{}});return nodes.get(selector);}};
 const context=vm.createContext({document,Date,Number,coffeeLoaded:true});
 vm.runInContext(render,context);
 return {nodes,show(claude){context.input={claude,caffeineMinutes:120};vm.runInContext('extraState(input)',context);return nodes.get('#claude-state');}};
}
test('panel labels cached quota stale and recovers automatically on fresh status',()=>{
 const ui=panel(),now=Date.now(),quota={remaining:18,resetsAt:Math.floor(now/1000)+3600,updatedAt:new Date(now-300000).toISOString(),stale:true};
 const old=ui.show(claudeUsageView({enabled:true,status:{state:'usage_unavailable',nextCheckAt:new Date(now+60000).toISOString()},quota,now}));
 assert.match(old.textContent,/Останній відомий залишок: 18%.*дані застаріли.*тимчасово недоступні.*наступна перевірка/);
 assert.equal(old.style.color,'#ffe5a7');
 const fresh=ui.show(claudeUsageView({enabled:true,status:{state:'ok'},quota:{...quota,remaining:17,updatedAt:new Date(now).toISOString(),stale:false},now}));
 assert.equal(fresh.textContent,'Тижневий залишок: 17%');assert.equal(fresh.style.color,'');
});
test('panel does not display cached limits when authorization is rejected',()=>{
 const now=Date.now(),ui=panel(),quota={remaining:18,resetsAt:Math.floor(now/1000)+3600,updatedAt:new Date(now).toISOString()};
 const node=ui.show(claudeUsageView({enabled:true,status:{state:'sign_in_required'},quota,now}));
 assert.match(node.textContent,/Потрібен повторний вхід/);assert.doesNotMatch(node.textContent,/18%/);
});
