import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {randomBytes} from 'node:crypto';
import {readFileSync,writeFileSync,renameSync,mkdirSync,mkdtempSync,rmSync,chmodSync,statSync} from 'node:fs';
import {join} from 'node:path';
import {measureLogitech,readLogitech} from './logitech-monitor.mjs';
const run=promisify(execFile);
export const recoveryIntervalMs=2*60*60*1000;
export const cpuThreshold=50,memoryThresholdMiB=1024;
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
const elevated=s=>({cpu:Number.isFinite(s?.cpuPercent)&&s.cpuPercent>=cpuThreshold,memory:Number.isFinite(s?.memoryMiB)&&s.memoryMiB>=memoryThresholdMiB});

// A cached dashboard is not a safety check. The authorized plugin must answer
// this exact request afresh, from the same Logitech process, or no restart occurs.
export async function requestRestartSafety(directory,expected,{now=Date.now,wait=sleep,timeoutMs=12000}={}){
  const nonce=randomBytes(16).toString('hex'),start=now();
  const file=join(directory,'restart-safety-request.json');
  writeFileSync(file+'.tmp',JSON.stringify({nonce,requestedAt:new Date(start).toISOString()}),{mode:0o600});renameSync(file+'.tmp',file);
  while(now()-start<timeoutMs){
    try{
      const replyFile=join(directory,'restart-safety-response.json');
      if(statSync(replyFile).size>4096)throw new Error('oversized reply');
      const reply=JSON.parse(readFileSync(replyFile,'utf8')),age=now()-Date.parse(reply.checkedAt);
      if(reply.nonce===nonce&&reply.hostPid===expected.pid&&Number.isFinite(age)&&age>=-1000&&age<=2000){
        const reason=['call','recording','audio','unavailable'].includes(reply.reason)?reply.reason:'unavailable';
        return reply.safe===true&&reply.reason==='safe'?{safe:true}:{safe:false,reason};
      }
    }catch{}
    await wait(100);
  }
  return {safe:false,reason:'unavailable'};
}

export async function captureRestartDiagnostic(directory,sample,{execute=run,now=Date.now,read=readLogitech}={}){
  const current=await read();if(!current||current.identity!==sample.identity)throw new Error('process changed before diagnostics');
  const diagnostics=join(directory,'Diagnostics');mkdirSync(diagnostics,{recursive:true,mode:0o700});chmodSync(diagnostics,0o700);
  const temp=mkdtempSync(join(diagnostics,'.restart-'));
  try{
    const file=join(temp,'sample.txt');writeFileSync(file,'',{mode:0o600});
    await execute('/usr/bin/sample',[String(sample.pid),'2','10','-file',file],{timeout:6000,maxBuffer:4096});
    if(statSync(file).size>4*1024*1024)throw new Error('oversized diagnostic');
    renameSync(file,join(diagnostics,'logitech-last-restart.sample.txt'));
    const summary=join(temp,'summary.json');
    writeFileSync(summary,JSON.stringify({checkedAt:new Date(now()).toISOString(),pid:sample.pid,cpuPercent:sample.cpuPercent,memoryMiB:Math.round(sample.memoryMiB)}),{mode:0o600});
    renameSync(summary,join(diagnostics,'logitech-last-restart.json'));
  }finally{rmSync(temp,{recursive:true,force:true});}
}


export function createLogitechAutoRestart({measure=measureLogitech,guard,restart,diagnose,enabled=()=>true,now=Date.now,wait=sleep,load=()=>null,save=()=>{}}={}){
  let persisted;try{persisted=load();}catch{persisted=null;}
  let busy=false,armedAt=now()+recoveryIntervalMs;
  let recovery={awaitingRecovery:persisted?.awaitingRecovery===true,lastAttemptAt:Number.isFinite(persisted?.lastAttemptAt)?persisted.lastAttemptAt:null,lastRestartAt:Number.isFinite(persisted?.lastRestartAt)?persisted.lastRestartAt:null};
  let state={state:'armed',automaticRestart:true,cpuThreshold,memoryThresholdMiB,lastRestartAt:recovery.lastRestartAt};
  const view=()=>({...state,automaticRestart:enabled(),nextCheckAt:new Date(armedAt).toISOString()});
  const set=(name,reason=null)=>{state={...state,state:name,reason};};
  const persist=()=>save(recovery);
  async function consider(first,checkedAt=now()){
    if(busy||now()<armedAt)return view();
    armedAt=checkedAt+recoveryIntervalMs;
    if(!enabled()){set('disabled');return view();}
    busy=true;
    try{
      const high=elevated(first);
      if(!first||first.cpuPercent===null){set('deferred','unavailable');return view();}
      if(!high.cpu&&!high.memory){recovery.awaitingRecovery=false;persist();set('healthy');return view();}
      if(recovery.awaitingRecovery){set('needs-attention');return view();}
      if(recovery.lastAttemptAt!==null&&now()-recovery.lastAttemptAt<recoveryIntervalMs){set('cooldown');return view();}
      let safety=await guard(first);
      if(!safety?.safe){set('deferred',safety?.reason||'unavailable');return view();}
      set('confirming');await wait(30000);
      const confirmed=await measure(),again=elevated(confirmed);
      if(!confirmed||confirmed.identity!==first.identity){set('deferred','process-changed');return view();}
      if(!(high.cpu&&again.cpu||high.memory&&again.memory)){set('recovered');return view();}
      if(!enabled()){set('disabled');return view();}
      safety=await guard(confirmed);
      if(!safety?.safe){set('deferred',safety?.reason||'unavailable');return view();}
      await diagnose(confirmed);
      // Persist before attempting termination: a helper crash must not create a restart loop.
      recovery={...recovery,awaitingRecovery:true,lastAttemptAt:now()};persist();
      set('restarting');
      await restart(confirmed,async()=>{
        if(!enabled())throw new Error('Не дозволено автоматичний перезапуск.');
        const final=await guard(confirmed);
        if(!enabled()||!final?.safe)throw new Error('Не вдалося підтвердити безпеку перезапуску.');
        return {expiresAt:now()+2000};
      });
      recovery.lastRestartAt=now();persist();state={...state,lastRestartAt:recovery.lastRestartAt};set('restarted');
    }catch{set('failed');}
    finally{busy=false;}
    return view();
  }
  function manualRestarted(){recovery={awaitingRecovery:false,lastAttemptAt:now(),lastRestartAt:now()};persist();armedAt=now()+recoveryIntervalMs;state={...state,lastRestartAt:recovery.lastRestartAt};set('armed');}
  return {consider,view,manualRestarted};
}
