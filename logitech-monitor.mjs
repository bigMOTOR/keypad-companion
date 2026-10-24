import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {fileURLToPath} from 'node:url';
const run=promisify(execFile);
export const intervalMs=2*60*60*1000;
export const executable='/Applications/Utilities/LogiPluginService.app/Contents/MacOS/LogiPluginService';
let cachedPid=null;
export function validateSample(value,uid=process.getuid()){
  if(value===null)return null;
  if(!value||value.uid!==uid||value.executable!==executable||!Number.isSafeInteger(value.pid)||value.pid<=1||typeof value.identity!=='string'||!value.identity.startsWith(value.pid+':')||!Number.isFinite(value.cpuSeconds)||value.cpuSeconds<0||!Number.isFinite(value.memoryMiB)||value.memoryMiB<0)throw new Error('Invalid process sample');
  return value;
}
export async function readLogitech(){
  const {stdout}=await run(fileURLToPath(new URL('./logitech-health-reader',import.meta.url)),cachedPid?[String(cachedPid)]:[],{timeout:8000,maxBuffer:4096});
  const sample=validateSample(JSON.parse(stdout));cachedPid=sample?.pid??null;return sample;
}
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
export async function measureLogitech({read=readLogitech,now=Date.now,wait=sleep,durationMs=3000,onBaseline=()=>{}}={}){
  const first=await read(),started=now();onBaseline(first);
  if(!first)return null;
  await wait(durationMs);
  const last=await read(),elapsed=(now()-started)/1000;
  if(!last||last.identity!==first.identity)return null;
  const cpu=elapsed>=durationMs/1000&&elapsed<=30&&last.cpuSeconds>=first.cpuSeconds?(last.cpuSeconds-first.cpuSeconds)/elapsed*100:null;
  return {...last,cpuPercent:cpu===null?null:Math.round(cpu*10)/10};
}

// A short current measurement every two hours, not a diluted two-hour CPU average.
export function createLogitechMonitor({read=readLogitech,wait=sleep,now=Date.now,save=()=>{},onCheck=async()=>{}}={}){
 let next=0,busy=false,history=[];
 let state={state:'checking',cpuPercent:null,memoryMiB:null,checkedAt:null,nextCheckAt:null,intervalMinutes:120,sampleSeconds:3,lastSuccessfulAt:null};
 const view=()=>({...state});
 const reset=()=>{next=0;state={...state,state:'checking',cpuPercent:null,memoryMiB:null,lastSuccessfulAt:null,checkedAt:null,nextCheckAt:null,reason:null};};
 async function tick(){
  if(busy||now()<next)return view();
  busy=true;const started=now();next=started+intervalMs;
  try{
   const sample=await measureLogitech({read,wait,now,onBaseline:first=>{if(first)state={...state,state:'baseline',memoryMiB:Math.round(first.memoryMiB)};}});
   const checked=new Date(now()).toISOString();
   state={...state,state:!sample?'missing':sample.cpuPercent===null?'unavailable':sample.cpuPercent>=50||sample.memoryMiB>=1024?'high':'ok',cpuPercent:sample?.cpuPercent??null,memoryMiB:sample?Math.round(sample.memoryMiB):null,lastSuccessfulAt:sample?checked:null,reason:sample&&sample.cpuPercent===null?'incomplete-interval':null};
   await onCheck(sample,started);
  }catch(error){state={...state,state:'unavailable',reason:error.killed?'timeout':'process-read-failed'};}
  finally{
   state={...state,checkedAt:new Date(now()).toISOString(),nextCheckAt:next>0?new Date(next).toISOString():null};
   history.push({checkedAt:state.checkedAt,state:state.state,cpuPercent:state.cpuPercent,memoryMiB:state.memoryMiB,lastSuccessfulAt:state.lastSuccessfulAt,reason:state.reason||null});
   history=history.filter(item=>now()-Date.parse(item.checkedAt)<=24*60*60*1000).slice(-288);
   try{save({current:view(),history});}catch{}
   busy=false;
  }
  return view();
 }
 return {tick,view,reset};
}
