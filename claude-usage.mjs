// Optional read-only usage polling inside the existing brightness process.
// Only sanitized numbers and fixed error codes may reach disk or the keypad.
const errors=new Set(['no_authorized_credential','sign_in_required','expired_credential','access_denied','team_account_required','usage_unavailable','refresh_unavailable','refresh_in_progress','keychain_unavailable','keychain_interaction_required']);
const temporaryErrors=new Set(['usage_unavailable','refresh_unavailable','refresh_in_progress','keychain_unavailable','keychain_interaction_required']);
function cachedUsage(input,now){
  if(!input||typeof input!=='object')return null;
  const {remaining,resetsAt,updatedAt}=input,age=now-Date.parse(updatedAt);
  if(!Number.isInteger(remaining)||remaining<0||remaining>100||!Number.isInteger(resetsAt)||resetsAt*1000<=now||!Number.isFinite(age)||age< -60000||age>30*60000)return null;
  return {remaining,resetsAt,updatedAt:new Date(Date.parse(updatedAt)).toISOString(),stale:input.stale===true||age>=10*60000};
}
export function claudeUsageView({enabled,status={},quota,now=Date.now()}){
  if(!enabled)return {state:'disabled',remaining:null};
  const state=errors.has(status.state)||status.state==='ok'?status.state:'usage_unavailable';
  const cached=state==='ok'||temporaryErrors.has(state)?cachedUsage(quota,now):null;
  const stale=!!cached&&(cached.stale||state!=='ok');
  return {state:state==='ok'&&(!cached||stale)?'stale':state,remaining:cached?.remaining??null,stale,updatedAt:cached?.updatedAt??null,checkedAt:status.checkedAt||null,nextCheckAt:status.nextCheckAt||null};
}
export function sanitizeUsage(input,now=Date.now()){
  if(!input||typeof input!=='object')return {error:'usage_unavailable'};
  if(input.error){
    const error=errors.has(input.error)?input.error:'usage_unavailable';
    return {error,...(temporaryErrors.has(error)&&Number.isInteger(input.retryAfterSeconds)&&input.retryAfterSeconds>=60&&input.retryAfterSeconds<=86400?{retryAfterSeconds:input.retryAfterSeconds}:{})};
  }
  const {remaining,resetsAt,updatedAt}=input;
  if(!Number.isInteger(remaining)||remaining<0||remaining>100||!Number.isInteger(resetsAt)||resetsAt*1000<=now)return {error:'usage_unavailable'};
  const age=now-Date.parse(updatedAt);
  if(!Number.isFinite(age)||age< -60000||age>60000)return {error:'usage_unavailable'};
  return {remaining,resetsAt,updatedAt:new Date(Date.parse(updatedAt)).toISOString()};
}
export function createClaudeUsagePoller({enabled,fetchUsage,saveQuota,readQuota=()=>null,clearQuota=()=>{},saveStatus,now=Date.now}){
  let active=null,queued=null,nextCheckAt=0,stopped=false,temporaryFailures=0;
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
      try{result=sanitizeUsage(await fetchUsage(force),now());}catch{result={error:'usage_unavailable'};}
      if(stopped||!enabled())return {state:'disabled',performed:false};
      const temporary=temporaryErrors.has(result.error);
      temporaryFailures=temporary?temporaryFailures+1:0;
      const delay=result.error==='keychain_interaction_required'?30:temporary?(temporaryFailures===1?1:temporaryFailures===2?2:5):result.error?30:5;
      nextCheckAt=now()+Math.max(delay*60000,(result.retryAfterSeconds||0)*1000);
      const state=result.error||'ok';
      try{
        if(temporary){const cached=cachedUsage(readQuota(),now());if(cached)saveQuota({...cached,stale:true});else clearQuota();}
        else if(result.error)clearQuota();else saveQuota(result);
        saveStatus({state,checkedAt:new Date(now()).toISOString(),nextCheckAt:new Date(nextCheckAt).toISOString()});
      }catch{return {state:'usage_unavailable',performed:false};}
      return {state,performed:true};
    })().finally(()=>{active=null;});
    return active;
  };
  return {tick,stop(){stopped=true;}};
}
