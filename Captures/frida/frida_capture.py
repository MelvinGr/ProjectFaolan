import frida, time, json, sys
OUT = open('/tmp/claude-1000/-home-melvin-Development-C--Project-Faolan/1056f362-b1be-4721-a7fb-d2143b69e32b/scratchpad/frida_reads.jsonl','w')
dev = frida.get_device_manager().add_remote_device('127.0.0.1:27042')
session = dev.attach(308)
print("ATTACHED", flush=True)
script = session.create_script(r"""
const nt = Process.getModuleByName('ntdll.dll').getExportByName('NtReadFile');
const aoc = Process.getModuleByName('AgeOfConan.exe');
const lo = aoc.base, hi = aoc.base.add(aoc.size);
let cap = 0;
Interceptor.attach(nt, {
  onEnter(args) {
    // NtReadFile(handle,ev,apc,apcctx,iosb,buf,len,ByteOffset,key) - stdcall 32-bit
    const len = args[6].toInt32();
    const off = args[7].isNull() ? -1 : args[7].readU64().valueOf();
    if (off < 0 || len < 64 || cap > 6000) return;
    // backtrace, keep only frames inside AgeOfConan.exe
    const bt = Thread.backtrace(this.context, Backtracer.FUZZY)
      .filter(a => a.compare(lo) >= 0 && a.compare(hi) < 0)
      .map(a => '0x'+a.sub(lo).add(0x400000).toString(16));
    if (bt.length === 0) return;
    cap++;
    send({o: off, l: len, bt: bt});
  }
});
send({armed:true});
""")
def on_msg(m, d):
    p = m.get('payload') if m.get('type')=='send' else None
    if p is None:
        print("ERR", m, flush=True); return
    if p.get('armed'): print("ARMED", flush=True)
    else: OUT.write(json.dumps(p)+"\n"); OUT.flush()
script.on('message', on_msg)
script.load()
# keep alive; parent will kill
while True: time.sleep(2)
