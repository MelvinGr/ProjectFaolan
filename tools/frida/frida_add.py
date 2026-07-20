import frida, time, json, sys
WIN_PID = int(sys.argv[1])
OUT = open('/tmp/claude-1000/-home-melvin-Development-C--Project-Faolan/1056f362-b1be-4721-a7fb-d2143b69e32b/scratchpad/frida_add.jsonl', 'w')
dev = frida.get_device_manager().add_remote_device('127.0.0.1:27042')
session = dev.attach(WIN_PID)
print("ATTACHED", WIN_PID, flush=True)
script = session.create_script(r"""
const ADD = 0xfed5d0;   // VisualCatMesh_c::AddSkinnedMesh(ecx=this, edx=nIdxOrFmeId, ...)
const aoc = Process.getModuleByName('AgeOfConan.exe');
const lo = aoc.base, hi = aoc.base.add(aoc.size);
const ARMOR = [323713, 323725, 700815, 4842950, 700842];
const seen = {};
Interceptor.attach(ptr(ADD), {
  onEnter(){
    const id = this.context.edx.toUInt32();
    if (ARMOR.indexOf(id) < 0 || seen[id]) return;
    seen[id] = 1;
    const bt = Thread.backtrace(this.context, Backtracer.ACCURATE)
      .filter(a => a.compare(lo)>=0 && a.compare(hi)<0)
      .map(a => '0x'+a.sub(lo).add(0x400000).toString(16));
    send({meshid:id, ecx:'0x'+this.context.ecx.toUInt32().toString(16), stack:bt});
  }
});
send({armed:true});
""")
def on_msg(m, d):
    p = m.get('payload') if m.get('type') == 'send' else m
    if isinstance(p, dict) and p.get('armed'):
        print("ARMED", flush=True)
    else:
        OUT.write(json.dumps(p) + "\n"); OUT.flush(); print("ADD", json.dumps(p)[:120], flush=True)
script.on('message', on_msg)
script.load()
while True:
    time.sleep(2)
