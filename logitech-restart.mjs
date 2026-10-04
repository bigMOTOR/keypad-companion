import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {executable,readLogitech} from './logitech-monitor.mjs';
const run=promisify(execFile);
const app='/Applications/Utilities/LogiPluginService.app';
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));

// Invoked only by the authenticated manual button. Never match a process by
// a partial name, accept a PID from the browser, or force-kill a stuck process.
export async function restartLogitech({read=readLogitech,execute=run,wait=sleep,uid=process.getuid()}={}){
  const old=await read();
  if(old){
    if(!Number.isSafeInteger(old.pid)||old.pid<=1)throw new Error('Не вдалося перевірити службу Logitech.');
    const current=await read();
    if(old.uid!==uid||old.executable!==executable||current?.uid!==uid||current?.executable!==executable||current?.identity!==old.identity)throw new Error('Процес змінився · перезапуск скасовано.');
    await execute('/bin/kill',['-TERM',String(old.pid)],{timeout:3000,maxBuffer:4096});
    let stopped=false;
    for(let i=0;i<32;i++){
      const sample=await read();
      if(sample?.identity!==old.identity){stopped=true;break;}
      await wait(250);
    }
    if(!stopped)throw new Error('Служба не завершилася. Примусове закриття не застосовано.');
  }
  if(!(await read()))await execute('/usr/bin/open',['-g','-a',app],{timeout:5000,maxBuffer:4096});
  for(let i=0;i<60;i++){
    const sample=await read();
    if(sample&&(!old||sample.identity!==old.identity))return;
    await wait(250);
  }
  throw new Error('Служба ще не запустилася. Відкрий Logi Options+ і перевір підключення.');
}

export function createLogitechRestarter({restart=restartLogitech,now=Date.now,onSuccess=()=>{}}={}){
  let busy=false,next=0,state={state:'idle',error:null};
  const view=()=>({...state,busy,cooldownUntil:next||null});
  async function start(){
    if(busy||now()<next)throw new Error('Перезапуск уже триває або щойно завершився.');
    busy=true;state={state:'restarting',error:null};
    try{await restart();await onSuccess();state={state:'complete',error:null};}
    catch(error){state={state:'failed',error:error.message.startsWith('Не ')||error.message.startsWith('Процес ')||error.message.startsWith('Служба ')?error.message:'Не вдалося перезапустити службу Logitech.'};throw new Error(state.error);}
    finally{busy=false;next=now()+15000;}
    return view();
  }
  return {start,view};
}
