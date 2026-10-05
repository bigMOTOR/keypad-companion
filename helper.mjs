import {LogiBrightnessClient} from './logi-client.mjs';
import {defaultPoints,validatePoints,mapBrightness} from './brightness.mjs';
import {createBrightnessCache} from './brightness-cache.mjs';
import {createClaudeUsagePoller,claudeUsageView} from './claude-usage.mjs';
import {defaultCaffeineMinutes,validateCaffeineMinutes} from './caffeine.mjs';
import {createLogitechMonitor} from './logitech-monitor.mjs';
import {createLogitechRestarter} from './logitech-restart.mjs';
import {createClaudeLogin} from './claude-login.mjs';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {readFileSync,writeFileSync,renameSync,mkdirSync,rmSync} from 'node:fs';
import {fileURLToPath} from 'node:url';
import {join} from 'node:path';
import {homedir} from 'node:os';
import {createServer} from 'node:http';
import {randomBytes} from 'node:crypto';
const root=new URL('./',import.meta.url);
const dataRoot=process.env.KEYPAD_BRIGHTNESS_DATA_DIR||join(homedir(),'Library/Application Support/KeypadBrightness');
mkdirSync(dataRoot,{recursive:true});
const dataFile=name=>join(dataRoot,name);
const codeFile=name=>new URL(name,root);
const atomic=(name,data)=>{writeFileSync(dataFile(name+'.tmp'),JSON.stringify(data,null,2));renameSync(dataFile(name+'.tmp'),dataFile(name));};
const defaults={enabled:true,points:defaultPoints,pollSeconds:1,settleSeconds:3,manualOverrideMinutes:60};
let config={...defaults,...JSON.parse(readFileSync(dataFile('config.json'),'utf8'))};
const loadConfig=()=>{config={...defaults,...JSON.parse(readFileSync(dataFile('config.json'),'utf8'))};config.points=validatePoints(config.points);if(!Number.isFinite(config.settleSeconds)||config.settleSeconds<0||config.settleSeconds>10)throw new Error('Некоректна затримка.');};
let client=null,lastApplied=null,pauseUntil=0,manualDimUntil=0,busy=false,stopping=false,lastScreen=null,settlingUntil=0;
try{const state=JSON.parse(readFileSync(dataFile('override.json'),'utf8'));pauseUntil=state.pauseUntil||0;manualDimUntil=state.manualDimUntil||0;}catch{}
const saveOverride=()=>atomic('override.json',{pauseUntil,manualDimUntil});
let status={mode:'starting',mac:null,keypad:null,target:null,error:null};
const caffeineMinutes=()=>{try{return validateCaffeineMinutes(JSON.parse(readFileSync(dataFile('caffeine-settings.json'),'utf8')).durationMinutes);}catch{return defaultCaffeineMinutes;}};
const claudeEnabled=()=>{try{return JSON.parse(readFileSync(dataFile('ai-settings.json'),'utf8')).claudeEnabled===true;}catch{return false;}};
const clearClaudeQuota=()=>rmSync(dataFile('claude-quota.json'),{force:true});
const claudeView=()=>{
  if(!claudeEnabled())return {state:'disabled',remaining:null};
  let state={state:'disabled'},quota=null;
  try{state=JSON.parse(readFileSync(dataFile('claude-usage-status.json'),'utf8'));}catch{}
  try{quota=JSON.parse(readFileSync(dataFile('claude-quota.json'),'utf8'));}catch{}
  return claudeUsageView({enabled:true,status:state,quota});
};
const view=()=>({...status,points:config.points,minimum:Math.min(...config.points.map(p=>p.keypad)),maximum:Math.max(...config.points.map(p=>p.keypad)),settleSeconds:config.settleSeconds,settling:Date.now()<settlingUntil&&status.mode==='auto',pausedUntil:pauseUntil||null,caffeineMinutes:caffeineMinutes(),logitech:{...logitechMonitor.view(),restart:logitechRestarter.view()},claude:{...claudeView(),login:claudeLogin.view()}});
const publish=()=>atomic('status.json',{...view(),updatedAt:new Date().toISOString()});
const run=promisify(execFile);
const brightnessCache=createBrightnessCache();
let observedScreen=null;
const privateAtomic=(name,value)=>{writeFileSync(dataFile(name+'.tmp'),JSON.stringify(value),{mode:0o600});renameSync(dataFile(name+'.tmp'),dataFile(name));};
const logitechMonitor=createLogitechMonitor({save:value=>privateAtomic('logitech-health.json',value)});
const logitechRestarter=createLogitechRestarter({onSuccess:async()=>{client?.close();client=null;lastApplied=null;brightnessCache.reset();logitechMonitor.reset();await logitechMonitor.tick();}});
const claudeUsage=createClaudeUsagePoller({
  enabled:claudeEnabled,
  clearQuota:clearClaudeQuota,
  readQuota:()=>{try{return JSON.parse(readFileSync(dataFile('claude-quota.json'),'utf8'));}catch{return null;}},
  fetchUsage:async(manual)=>{const {stdout}=await run('/usr/bin/python3',[fileURLToPath(codeFile('claude-weekly-usage.py')),...(manual?['--allow-keychain-ui']:[])],{encoding:'utf8',timeout:75000,maxBuffer:16384});return JSON.parse(stdout);},
  saveQuota:value=>privateAtomic('claude-quota.json',value),
  saveStatus:value=>privateAtomic('claude-usage-status.json',value)
});
const claudeLogin=createClaudeLogin({cwd:dataRoot,onSuccess:async()=>{clearClaudeQuota();privateAtomic('ai-settings.json',{claudeEnabled:true});return await claudeUsage.tick(true);}});
async function tick(force=false){
  if(busy||stopping)return;
  busy=true;
  try{
    loadConfig();
    const {stdout}=await run('/usr/bin/python3',[fileURLToPath(codeFile('display-brightness.py'))],{encoding:'utf8',timeout:3000});
    const display=JSON.parse(stdout);
    status.mac=display.available?Math.round(display.brightness*100):null;
    if(!client||client.ws.readyState!==1){client?.close();client=await LogiBrightnessClient.connect();lastApplied=null;brightnessCache.reset();}
    const screenChanged=status.mac!==observedScreen;observedScreen=status.mac;
    const current=await brightnessCache.read(()=>client.read(),force||screenChanged);status.keypad=current;
    if(lastApplied!==null&&current!==lastApplied&&config.enabled){pauseUntil=Date.now()+config.manualOverrideMinutes*60000;manualDimUntil=0;lastApplied=null;saveOverride();}
    if(!config.enabled){status.mode='paused';status.target=null;status.error=null;publish();return;}
    if(Date.now()<pauseUntil){status.mode='manual';status.target=null;status.error=null;publish();return;}
    if(!display.available){status.mode=display.asleep?'screen-asleep':'screen-unavailable';status.target=null;status.error=null;publish();return;}
    if(status.mac!==lastScreen){lastScreen=status.mac;settlingUntil=Date.now()+config.settleSeconds*1000;}
    const dim=Date.now()<manualDimUntil;
    const target=dim?Math.min(...config.points.map(p=>p.keypad)):mapBrightness(status.mac,config.points);
    status.target=target;status.mode=dim?'dim-until-morning':'auto';
    if(force||dim||Date.now()>=settlingUntil){
      if(target!==current){
        const actual=await brightnessCache.read(()=>client.read(),true);
        if(lastApplied!==null&&actual!==lastApplied&&config.enabled){pauseUntil=Date.now()+config.manualOverrideMinutes*60000;manualDimUntil=0;lastApplied=null;saveOverride();status.keypad=actual;status.mode='manual';status.target=null;status.error=null;publish();return;}
        status.keypad=actual===target?actual:brightnessCache.update(await client.set(target));
      }
      lastApplied=status.keypad;
      if(force)settlingUntil=0;
    }
    status.error=null;publish();
  }catch(error){status.mode='waiting';status.error=error.message;publish();client?.close();client=null;brightnessCache.reset();}
  finally{busy=false;}
}
const csrf=randomBytes(24).toString('hex');
const origin='http://127.0.0.1:57973';
const server=createServer(async(req,res)=>{
  res.setHeader('Cache-Control','no-store');res.setHeader('X-Content-Type-Options','nosniff');
  res.setHeader('Content-Security-Policy',"default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'");
  if(req.headers.host!=='127.0.0.1:57973'){res.writeHead(403);res.end();return;}
  const json=value=>{res.setHeader('Content-Type','application/json');res.end(JSON.stringify(value));};
  if(req.method==='GET'&&req.url==='/'){res.setHeader('Content-Type','text/html; charset=utf-8');res.end(readFileSync(codeFile('settings.html'),'utf8').replace('CSRF_TOKEN',csrf));return;}
  if(req.method==='GET'&&req.url==='/status'){json(view());return;}
  if(req.method==='GET'&&req.url==='/session'){
    if(req.headers.origin&&req.headers.origin!==origin){res.writeHead(403);json({error:'Недозволений запит.'});return;}
    json({token:csrf});return;
  }
  if(req.method==='POST'&&['/mode','/settings','/caffeine/settings','/claude/refresh','/claude/login','/claude/login/cancel','/claude/disable','/claude/enable','/logitech/restart'].includes(req.url)){
    if(req.headers.origin!==origin||req.headers['x-keypad-token']!==csrf){res.writeHead(403);json({error:'Панель потребує оновлення після перезапуску помічника.'});return;}
    let body='';for await(const chunk of req){body+=chunk;if(body.length>4096){res.writeHead(413);res.end();return;}}
    let input;try{input=JSON.parse(body);}catch{res.writeHead(400);json({error:'Некоректні дані.'});return;}
    try{
      if(req.url==='/logitech/restart'){
        json({ok:true,restart:await logitechRestarter.start()});return;
      }else if(req.url==='/caffeine/settings'){
        privateAtomic('caffeine-settings.json',{durationMinutes:validateCaffeineMinutes(input.durationMinutes)});
        json({ok:true});return;
      }else if(req.url==='/claude/login'){
        const login=claudeLogin.start();if(!login.available)throw new Error('Встанови Claude Code, щоб увійти.');json({ok:true,login});return;
      }else if(req.url==='/claude/login/cancel'){
        json({ok:true,login:claudeLogin.cancel()});return;
      }else if(req.url==='/claude/enable'){
        privateAtomic('ai-settings.json',{claudeEnabled:true});const result=await claudeUsage.tick(true);json({ok:true,...result});return;
      }else if(req.url==='/claude/disable'){
        claudeLogin.cancel();privateAtomic('ai-settings.json',{claudeEnabled:false});clearClaudeQuota();rmSync(dataFile('claude-usage-status.json'),{force:true});json({ok:true});return;
      }else if(req.url==='/claude/refresh'){
        const result=await claudeUsage.tick(true);json({ok:true,...result});return;
      }else if(req.url==='/settings'){
        const points=validatePoints(input.points);
        if(!Number.isInteger(input.settleSeconds)||input.settleSeconds<0||input.settleSeconds>10)throw new Error('Затримка: 0–10 секунд.');
        while(busy)await new Promise(r=>setTimeout(r,25));
        loadConfig();config.points=points;config.settleSeconds=input.settleSeconds;atomic('config.json',config);
      }else{
        if(!['auto','pause','dim'].includes(input.action))throw new Error('Невідомий режим.');
        while(busy)await new Promise(r=>setTimeout(r,25));
        loadConfig();pauseUntil=0;manualDimUntil=0;lastApplied=null;
        config.enabled=input.action!=='pause';
        if(input.action==='dim'){const morning=new Date();morning.setHours(8,0,0,0);if(morning.getTime()<=Date.now())morning.setDate(morning.getDate()+1);manualDimUntil=morning.getTime();}
        atomic('config.json',config);saveOverride();
      }
      await tick(true);json({ok:true});return;
    }catch(error){res.writeHead(400);json({error:error.message});return;}
  }
  res.writeHead(404);res.end();
});
server.on('error',e=>{console.error('Settings panel:',e.message);});
server.listen(57973,'127.0.0.1');
process.on('SIGTERM',()=>{stopping=true;claudeLogin.stop();claudeUsage.stop();client?.close();server.close();process.exit(0);});
process.on('SIGINT',()=>{stopping=true;claudeLogin.stop();claudeUsage.stop();client?.close();server.close();process.exit(0);});
publish();void tick();void claudeUsage.tick();void logitechMonitor.tick();setInterval(()=>{void tick();void claudeUsage.tick();void logitechMonitor.tick();},1000);
