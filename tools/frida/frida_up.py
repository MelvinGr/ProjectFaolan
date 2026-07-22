import os, tempfile
import frida, time, json, sys
WIN_PID = int(sys.argv[1])
OUT = open(os.path.join(tempfile.gettempdir(), 'frida_up.jsonl'), 'w')
dev = frida.get_device_manager().add_remote_device('127.0.0.1:27042')
session = dev.attach(WIN_PID)
print("ATTACHED", WIN_PID, flush=True)
script = session.create_script(r"""
// caller-of-RequestResource entries (candidate resolver), + RequestResource itself for the return-address (caller ip)
const RESOLVER_ENTRIES = [0x185733a, 0x1845e8d, 0x1061a20];
const REQ = 0x1842818;               // Request(type=ecx, id=edx)
const LOOK = 13117;
const MESHES = [323713, 323725, 700815, 700816, 4842950];
function u32(p,o){ try{ return ptr(p).add(o).readU32(); }catch(e){ return -1; } }
// Hook RequestResource: when a meshindex(1010030) is requested, log the CALLER ip (resolver call-site)
const reqCallers = {};
Interceptor.attach(ptr(REQ), {
  onEnter(){
    const type=this.context.ecx.toUInt32(), id=this.context.edx.toUInt32();
    if (type===1010030){  // any MESHINDEXFILE request -> tally its caller (resolver call-site)
      const caller = '0x'+this.context.esp.readU32().toString(16);
      const k = caller;
      if (!reqCallers[k]){ reqCallers[k]=0; }
      reqCallers[k]++;
      if (reqCallers[k] <= 3) send({req:{type, id, caller:k}});
    }
  }
});
// Hook resolver-candidate entries: dump this(ecx) struct fields, looking for LOOK/meshindex
RESOLVER_ENTRIES.forEach(a=>{
  try{
    Interceptor.attach(ptr(a), {
      onEnter(){
        const ecx=this.context.ecx.toUInt32();
        const fields=[]; for(let o=0;o<0x80;o+=4) fields.push(u32(ecx,o)>>>0);
        const args=[]; for(let i=1;i<=4;i++){ try{args.push(this.context.esp.add(i*4).readU32());}catch(e){args.push(0);} }
        // only emit if LOOK or a mesh id appears anywhere (ecx fields / args / edx)
        const pool = fields.concat(args, [this.context.edx.toUInt32(), this.context.eax.toUInt32()]);
        if (pool.indexOf(LOOK)>=0 || MESHES.some(m=>pool.indexOf(m)>=0)){
          send({fn:'0x'+a.toString(16), ecx:'0x'+ecx.toString(16), edx:this.context.edx.toUInt32(), args:args,
                fields:fields, look:pool.indexOf(LOOK)>=0});
        }
      }
    });
    send({hooked:'0x'+a.toString(16)});
  }catch(e){ send({err:'0x'+a.toString(16)+': '+e}); }
});
send({armed:true});
""")
def on_msg(m, d):
    p = m.get('payload') if m.get('type') == 'send' else m
    if isinstance(p, dict) and p.get('armed'):
        print("ARMED", flush=True)
    else:
        OUT.write(json.dumps(p) + "\n"); OUT.flush(); print("MSG", json.dumps(p)[:200], flush=True)
script.on('message', on_msg)
script.load()
while True:
    time.sleep(2)
