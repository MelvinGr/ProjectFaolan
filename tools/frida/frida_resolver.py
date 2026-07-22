import os, tempfile
import frida, time, json
OUT = open(os.path.join(tempfile.gettempdir(), 'frida_resolver.jsonl'), 'w')
dev = frida.get_device_manager().add_remote_device('127.0.0.1:27042')
session = dev.attach(308)
print("ATTACHED", flush=True)
script = session.create_script(r"""
const probes = ['0x1842846','0x18574ba','0x18574c7','0x1845ea3','0x1dd095c','0x1856f76','0x1061aaf','0x4175b3'];
const WANT = [13117, 323725, 323713, 700815, 700842, 4842950, 700816, 323726];
function scan(ctx){
  const regs={eax:ctx.eax,ebx:ctx.ebx,ecx:ctx.ecx,edx:ctx.edx,esi:ctx.esi,edi:ctx.edi};
  const out={};
  for (const k in regs){ const v=regs[k].toUInt32(); if (WANT.indexOf(v)>=0) out[k]=v; }
  for (let i=0;i<8;i++){ try{ const v=ctx.esp.add(i*4).readU32(); if(WANT.indexOf(v)>=0) out['sp'+i]=v; }catch(e){} }
  return out;
}
probes.forEach(p=>{
  try { Interceptor.attach(ptr(p), { onEnter(){ const h=scan(this.context); if(Object.keys(h).length) send({at:p, hit:h}); } }); }
  catch(e){ send({err:p+': '+e}); }
});
send({armed:true});
""")
def on_msg(m, d):
    p = m.get('payload') if m.get('type') == 'send' else m
    if isinstance(p, dict) and p.get('armed'):
        print("ARMED", flush=True)
    else:
        OUT.write(json.dumps(p) + "\n"); OUT.flush(); print("HIT", p, flush=True)
script.on('message', on_msg)
script.load()
while True:
    time.sleep(2)
