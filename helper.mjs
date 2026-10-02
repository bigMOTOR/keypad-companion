import {LogiBrightnessClient} from './logi-client.mjs';
import {defaultPoints,validatePoints,mapBrightness} from './brightness.mjs';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';
import {readFileSync,writeFileSync,renameSync,mkdirSync} from 'node:fs';
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
const view=()=>({...status,points:config.points,minimum:Math.min(...config.points.map(p=>p.keypad)),maximum:Math.max(...config.points.map(p=>p.keypad)),settleSeconds:config.settleSeconds,settling:Date.now()<settlingUntil&&status.mode==='auto',pausedUntil:pauseUntil||null});
const publish=()=>atomic('status.json',{...view(),updatedAt:new Date().toISOString()});
const run=promisify(execFile);
async function tick(force=false){
  if(busy||stopping)return;
  busy=true;
  try{
    loadConfig();
    const {stdout}=await run('/usr/bin/python3',[fileURLToPath(codeFile('display-brightness.py'))],{encoding:'utf8',timeout:3000});
    const display=JSON.parse(stdout);
    status.mac=display.available?Math.round(display.brightness*100):null;
    if(!client||client.ws.readyState!==1){client?.close();client=await LogiBrightnessClient.connect();lastApplied=null;}
    const current=await client.read();status.keypad=current;
    if(lastApplied!==null&&current!==lastApplied&&config.enabled){pauseUntil=Date.now()+config.manualOverrideMinutes*60000;manualDimUntil=0;lastApplied=null;saveOverride();}
    if(!config.enabled){status.mode='paused';status.target=null;status.error=null;publish();return;}
    if(Date.now()<pauseUntil){status.mode='manual';status.target=null;status.error=null;publish();return;}
    if(!display.available){status.mode=display.asleep?'screen-asleep':'screen-unavailable';status.target=null;status.error=null;publish();return;}
    if(status.mac!==lastScreen){lastScreen=status.mac;settlingUntil=Date.now()+config.settleSeconds*1000;}
    const dim=Date.now()<manualDimUntil;
    const target=dim?Math.min(...config.points.map(p=>p.keypad)):mapBrightness(status.mac,config.points);
    status.target=target;status.mode=dim?'dim-until-morning':'auto';
    if(force||dim||Date.now()>=settlingUntil){
      if(target!==current){status.keypad=await client.set(target);}
      lastApplied=status.keypad;
      if(force)settlingUntil=0;
    }
    status.error=null;publish();
  }catch(error){status.mode='waiting';status.error=error.message;publish();client?.close();client=null;}
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
  if(req.method==='POST'&&['/mode','/settings'].includes(req.url)){
    if(req.headers.origin!==origin||req.headers['x-keypad-token']!==csrf){res.writeHead(403);res.end();return;}
    let body='';for await(const chunk of req){body+=chunk;if(body.length>4096){res.writeHead(413);res.end();return;}}
    let input;try{input=JSON.parse(body);}catch{res.writeHead(400);json({error:'Некоректні дані.'});return;}
    try{
      if(req.url==='/settings'){
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
process.on('SIGTERM',()=>{stopping=true;client?.close();server.close();process.exit(0);});
process.on('SIGINT',()=>{stopping=true;client?.close();server.close();process.exit(0);});
publish();void tick();setInterval(()=>void tick(),1000);
