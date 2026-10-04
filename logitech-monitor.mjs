import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {fileURLToPath} from 'node:url';
const run=promisify(execFile);
export const intervalMs=5*60*1000;
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
// CPU is averaged across the full five-minute interval; 100% means one core.
// This monitor never kills or restarts Logitech and stores no process arguments.
export function createLogitechMonitor({read=readLogitech,now=Date.now,save=()=>{}}={}){
  let next=0,busy=false,previous=null,highCount=0,history=[];
  let state={state:'checking',cpuPercent:null,memoryMiB:null,checkedAt:null,nextCheckAt:null,intervalMinutes:5,automaticRestart:false,lastSuccessfulAt:null};
  const view=()=>({...state});
  const reset=()=>{next=0;previous=null;highCount=0;state={...state,lastSuccessfulAt:null,state:'checking',cpuPercent:null,memoryMiB:null,checkedAt:null,nextCheckAt:null,reason:null};};
  async function tick(){
    if(busy||now()<next)return view();
    busy=true;const checked=now();next=checked+intervalMs;
    try{
      const sample=await read();let cpu=null;
      if(sample&&previous&&sample.identity===previous.identity&&checked>previous.at&&sample.cpuSeconds>=previous.cpuSeconds){
        cpu=Math.round((sample.cpuSeconds-previous.cpuSeconds)/((checked-previous.at)/1000)*1000)/10;
      }
      highCount=sample&&cpu!==null&&cpu>=80?highCount+1:0;
      previous=sample?{...sample,at:checked}:null;
      state={...state,state:!sample?'missing':highCount>=2?'high':cpu===null?'baseline':'ok',cpuPercent:cpu,memoryMiB:sample?Math.round(sample.memoryMiB):null,lastSuccessfulAt:sample?new Date(checked).toISOString():null,reason:null};
    }catch(error){
      const reason=error.killed?'timeout':['Invalid process time','Incomplete process sample','Invalid process memory','Invalid process sample'].includes(error.message)?error.message:'process-read-failed';
      highCount=0;state={...state,state:'unavailable',reason};
    }finally{
      state={...state,checkedAt:new Date(checked).toISOString(),nextCheckAt:new Date(next).toISOString()};
      history.push({checkedAt:state.checkedAt,state:state.state,cpuPercent:state.cpuPercent,memoryMiB:state.memoryMiB,lastSuccessfulAt:state.lastSuccessfulAt,reason:state.reason||null});
      history=history.slice(-288);
      try{save({current:view(),history});}catch{}
      busy=false;
    }
    return view();
  }
  return {tick,view,reset};
}
