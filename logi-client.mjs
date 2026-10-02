import {execFileSync} from 'node:child_process';
import WebSocket from './lib/ws/index.js';

export class LogiBrightnessClient {
  constructor(ws) {
    this.ws=ws;this.requests=new Map();this.nextId=1000000+Math.floor(Math.random()*1000000);
    ws.addEventListener('message',event=>{
      let r;try {r=JSON.parse(event.data);} catch{return;}
      if(process.env.LOGI_BR_DEBUG)console.log('response',JSON.stringify({id:r.id,name:r.name,failed:r.failed,brightness:r.data?.displayBrightness}));
      const pending=this.requests.get(r.id??r.Id);if(!pending)return;
      this.requests.delete(r.id);clearTimeout(pending.timer);
      if(r.failed)pending.reject(new Error(r.errorMessage||'Logitech rejected the request'));
      else pending.resolve(r.data);
    });
    ws.addEventListener('close',()=>{for(const p of this.requests.values()){clearTimeout(p.timer);p.reject(new Error('Logitech connection closed'));}this.requests.clear();});
  }
  static async connect() {
    const text=execFileSync('/usr/sbin/lsof',['-nP','-a','-c','LogiPlugin','-iTCP','-sTCP:LISTEN'],{encoding:'utf8',timeout:3000});
    const ports=[...new Set([...text.matchAll(/127\.0\.0\.1:(\d+)/g)].map(m=>Number(m[1])))];
    for(const port of ports) {
      let ws;
      try {
        ws=await new Promise((resolve,reject)=>{
          const candidate=new WebSocket(`ws://127.0.0.1:${port}/configui3`,{perMessageDeflate:false});
          const timer=setTimeout(()=>{candidate.close();reject(new Error('Connect timeout'));},2000);
          candidate.addEventListener('open',()=>{clearTimeout(timer);resolve(candidate);},{once:true});
          candidate.addEventListener('error',()=>{clearTimeout(timer);reject(new Error('Connect failed'));},{once:true});
        });
        const client=new LogiBrightnessClient(ws);
        await client.read();return client;
      } catch {ws?.close();}
    }
    throw new Error('Logitech brightness service is unavailable');
  }
  request(name,parameters={}) {
    if(this.ws.readyState!==WebSocket.OPEN)return Promise.reject(new Error('Logitech connection closed'));
    const id=this.nextId++;
    return new Promise((resolve,reject)=>{
      const timer=setTimeout(()=>{this.requests.delete(id);reject(new Error('Logitech request timeout'));},3000);
      this.requests.set(id,{resolve,reject,timer});
      if(process.env.LOGI_BR_DEBUG)console.log('request',id,name);
      this.ws.send(JSON.stringify({Id:id,Name:name,Parameters:parameters,Data:null}));
    });
  }
  async read() {
    const data=await this.request('GetExtendedDeviceSettings');
    const value=data?.displayBrightness;
    if(!Number.isInteger(value)||value<0||value>100)throw new Error('Unexpected Logitech brightness response');
    return value;
  }
  async set(value) {
    if(!Number.isInteger(value)||value<1||value>100)throw new Error('Brightness must be an integer from 1 to 100');
    await this.request('SetExtendedDeviceSettings',{settingName:'displayBrightness',settingValue:String(value)});
    const actual=await this.read();
    if(actual!==value)throw new Error(`Brightness verification failed: ${actual} instead of ${value}`);
    return actual;
  }
  close(){this.ws.close();}
}
