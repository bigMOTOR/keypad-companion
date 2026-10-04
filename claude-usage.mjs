// Optional read-only usage polling inside the existing brightness process.
// Only sanitized numbers and fixed error codes may reach disk or the keypad.
const errors=new Set(['no_authorized_credential','sign_in_required','expired_credential','access_denied','team_account_required','usage_unavailable','refresh_unavailable','refresh_in_progress','keychain_unavailable']);
export function sanitizeUsage(input,now=Date.now()){
  if(!input||typeof input!=='object')return {error:'usage_unavailable'};
  if(input.error)return {error:errors.has(input.error)?input.error:'usage_unavailable'};
  const {remaining,resetsAt,updatedAt}=input;
  if(!Number.isInteger(remaining)||remaining<0||remaining>100||!Number.isInteger(resetsAt)||resetsAt*1000<=now)return {error:'usage_unavailable'};
  const age=now-Date.parse(updatedAt);
  if(!Number.isFinite(age)||age< -60000||age>60000)return {error:'usage_unavailable'};
  return {remaining,resetsAt,updatedAt:new Date(Date.parse(updatedAt)).toISOString()};
}
export function createClaudeUsagePoller({enabled,fetchUsage,saveQuota,clearQuota=()=>{},saveStatus,now=Date.now}){
  let active=null,queued=null,nextCheckAt=0,stopped=false;
  const tick=(force=false)=>{
    if(stopped||!enabled())return Promise.resolve({state:'disabled',performed:false});
    if(active){
      if(!force)return Promise.resolve({state:'checking',performed:false});
      // A login/manual request must re-read after the current request finishes.
      if(!queued)queued=active.then(()=>{queued=null;return tick(true);});
      return queued;
    }
    if(!force&&now()<nextCheckAt)return Promise.resolve({state:'backoff',performed:false});
    active=(async()=>{
      let result;
      try{result=sanitizeUsage(await fetchUsage(),now());}catch{result={error:'usage_unavailable'};}
      if(stopped||!enabled())return {state:'disabled',performed:false};
      nextCheckAt=now()+(result.error==='refresh_in_progress'?1:result.error&& !['usage_unavailable','refresh_unavailable','keychain_unavailable'].includes(result.error)?30:5)*60000;
      const state=result.error||'ok';
      try{
        if(result.error)clearQuota();else saveQuota(result);
        saveStatus({state,checkedAt:new Date(now()).toISOString(),nextCheckAt:new Date(nextCheckAt).toISOString()});
      }catch{return {state:'usage_unavailable',performed:false};}
      return {state,performed:true};
    })().finally(()=>{active=null;});
    return active;
  };
  return {tick,stop(){stopped=true;}};
}
