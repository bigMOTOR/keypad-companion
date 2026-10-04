// Cache device settings; force a fresh read before actions or a brightness write.
export function createBrightnessCache({now=Date.now,intervalMs=60000}={}){
  let value=null,next=0;
  const update=v=>{value=v;next=now()+intervalMs;return v;};
  return {
    async read(read,force=false){return force||value===null||now()>=next?update(await read()):value;},
    update,
    reset(){value=null;next=0;}
  };
}
