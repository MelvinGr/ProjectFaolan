import frida, time, json, sys
WIN_PID = int(sys.argv[1])
OUT = open('/tmp/claude-1000/-home-melvin-Development-C--Project-Faolan/1056f362-b1be-4721-a7fb-d2143b69e32b/scratchpad/frida_attr.jsonl', 'w')
dev = frida.get_device_manager().add_remote_device('127.0.0.1:27042')
session = dev.attach(WIN_PID)
print("ATTACHED", WIN_PID, flush=True)
script = session.create_script(r"""
const GET = 0x89bd80;    // FUN_0089bd80(this=ecx, attrId=[esp+4]) -> EAX = runtime attribute value
const BUILD = 0x8b9ca0;  // FUN_008b9ca0 body-armor visual builder (documented)
const aoc = Process.getModuleByName('AgeOfConan.exe');
const lo = aoc.base, hi = aoc.base.add(aoc.size);
// attribute getter: capture return when the worn-armor meshindex attr (0x2da) or secondary (0x30b) is read
Interceptor.attach(ptr(GET), {
  onEnter(){ try { this.attr = this.context.esp.add(4).readU32(); this.ecx = this.context.ecx.toUInt32(); } catch(e){ this.attr = -1; } },
  onLeave(rv){
    if (this.attr === 0x2da || this.attr === 0x30b){
      send({getter:{attr:this.attr, ret:rv.toUInt32(), this:'0x'+this.ecx.toString(16)}});
    }
  }
});
// the documented armor builder: does it fire at all (fresh resolve)?
Interceptor.attach(ptr(BUILD), {
  onEnter(){
    const bt = Thread.backtrace(this.context, Backtracer.ACCURATE)
      .filter(a => a.compare(lo)>=0 && a.compare(hi)<0)
      .map(a => '0x'+a.sub(lo).add(0x400000).toString(16));
    send({builder:'0x8b9ca0', ecx:'0x'+this.context.ecx.toUInt32().toString(16), stack:bt.slice(0,10)});
  }
});
send({armed:true});
""")
def on_msg(m, d):
    p = m.get('payload') if m.get('type') == 'send' else m
    if isinstance(p, dict) and p.get('armed'):
        print("ARMED", flush=True)
    else:
        OUT.write(json.dumps(p) + "\n"); OUT.flush(); print("HIT", json.dumps(p)[:160], flush=True)
script.on('message', on_msg)
script.load()
while True:
    time.sleep(2)
