import os, tempfile
import frida, time, json, sys
WIN_PID = int(sys.argv[1])
OUT = open(os.path.join(tempfile.gettempdir(), 'frida_stack.jsonl'), 'w')
dev = frida.get_device_manager().add_remote_device('127.0.0.1:27042')
session = dev.attach(WIN_PID)
print("ATTACHED", WIN_PID, flush=True)
script = session.create_script(r"""
const REQ = 0x1842818;              // RequestResource(type=ecx, id=edx)
const MESHES = [323713, 323725, 700815, 4842950, 700842]; // Darkandskiny worn meshindexes (look 13117 + manacles)
const aoc = Process.getModuleByName('AgeOfConan.exe');
const lo = aoc.base, hi = aoc.base.add(aoc.size);
const done = {};
Interceptor.attach(ptr(REQ), {
  onEnter(){
    const type=this.context.ecx.toUInt32(), id=this.context.edx.toUInt32();
    if (type!==1010030 || MESHES.indexOf(id)<0 || done[id]) return;
    done[id]=1;
    // FULL synchronous stack from the request up to the resolver
    const bt = Thread.backtrace(this.context, Backtracer.ACCURATE)
      .filter(a => a.compare(lo)>=0 && a.compare(hi)<0)
      .map(a => '0x'+a.sub(lo).add(0x400000).toString(16));
    send({id:id, stack:bt});
  }
});
send({armed:true});
""")
def on_msg(m, d):
    p = m.get('payload') if m.get('type') == 'send' else m
    if isinstance(p, dict) and p.get('armed'):
        print("ARMED", flush=True)
    else:
        OUT.write(json.dumps(p) + "\n"); OUT.flush(); print("STACK", json.dumps(p)[:300], flush=True)
script.on('message', on_msg)
script.load()
while True:
    time.sleep(2)
