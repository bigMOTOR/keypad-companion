import {spawn} from 'node:child_process';
import {accessSync,constants} from 'node:fs';
import {homedir,userInfo} from 'node:os';
import {join} from 'node:path';

export function findClaudeExecutable(){
  for(const path of [join(homedir(),'.local/bin/claude'),'/opt/homebrew/bin/claude','/usr/local/bin/claude']){
    try{accessSync(path,constants.X_OK);return path;}catch{}
  }
  return null;
}

export function createClaudeLogin({executable=findClaudeExecutable(),cwd,spawnProcess=spawn,onSuccess=async()=>{},timeoutMs=600000}){
  let child=null,timeout=null,stopped=false,state=executable?'idle':'unavailable';
  const view=()=>({available:!!executable,state});
  const start=()=>{
    if(stopped||child||state==='verifying')return view();
    if(!executable){state='unavailable';return view();}
    state='signing_in';
    let current;
    try{
      current=spawnProcess(executable,['auth','login','--claudeai'],{
        cwd,env:{HOME:homedir(),USER:userInfo().username,LOGNAME:userInfo().username,PATH:'/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin',TERM:'dumb'},
        stdio:['ignore','pipe','pipe']
      });
      child=current;
      // Drain and discard all output: OAuth URLs and auth output never reach logs or HTTP.
      current.stdout?.resume();current.stderr?.resume();
    }catch{state='failed';return view();}
    const finish=(success)=>{
      if(child!==current)return;
      clearTimeout(timeout);timeout=null;child=null;
      if(stopped)return;
      state=success?'verifying':'failed';
      if(success)Promise.resolve().then(onSuccess).then(result=>{if(!stopped&&state==='verifying')state=result?.state==='ok'?'complete':'usage_unavailable';}).catch(()=>{if(!stopped)state='usage_unavailable';});
    };
    current.once('error',()=>finish(false));
    current.once('close',code=>finish(code===0));
    timeout=setTimeout(()=>{if(child===current){state='failed';child=null;current.kill('SIGTERM');}},timeoutMs);
    timeout.unref?.();
    return view();
  };
  const cancel=()=>{const current=child;child=null;clearTimeout(timeout);timeout=null;if(current)current.kill('SIGTERM');if(executable)state='idle';return view();};
  return {view,start,cancel,stop:()=>{stopped=true;cancel();}};
}
