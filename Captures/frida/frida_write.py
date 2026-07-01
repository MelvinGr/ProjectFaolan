import frida, time, json, sys
WIN_PID = int(sys.argv[1])
OUT = open('/tmp/claude-1000/-home-melvin-Development-C--Project-Faolan/1056f362-b1be-4721-a7fb-d2143b69e32b/scratchpad/frida_write.jsonl', 'w')
dev = frida.get_device_manager().add_remote_device('127.0.0.1:27042')
session = dev.attach(WIN_PID)
print("ATTACHED", WIN_PID, flush=True)
script = session.create_script(r"""
const aoc = Process.getModuleByName('AgeOfConan.exe');
const lo = aoc.base, hi = aoc.base.add(aoc.size);
// Map handle->name via NtCreateFile so we can tell which write is Preview.bin
const nameByHandle = {};
const ntCreate = Process.getModuleByName('ntdll.dll').getExportByName('NtCreateFile');
Interceptor.attach(ntCreate, {
  onEnter(args){ this.ph = args[0]; this.oa = args[2]; },
  onLeave(ret){
    try{
      // OBJECT_ATTRIBUTES.ObjectName (UNICODE_STRING*) at +8; UNICODE_STRING: len@0, buf@4
      const us = this.oa.add(8).readPointer();
      const len = us.readU16();
      const buf = us.add(4).readPointer();
      const name = buf.readUtf16String(len/2);
      if (name && name.toLowerCase().indexOf('preview.bin') >= 0){
        const h = this.ph.readPointer().toString();
        nameByHandle[h] = name;
      }
    }catch(e){}
  }
});
const ntWrite = Process.getModuleByName('ntdll.dll').getExportByName('NtWriteFile');
Interceptor.attach(ntWrite, {
  onEnter(args){
    const h = args[0].toString();
    if (nameByHandle[h]){
      const bt = Thread.backtrace(this.context, Backtracer.ACCURATE)
        .filter(a => a.compare(lo)>=0 && a.compare(hi)<0)
        .map(a => '0x'+a.sub(lo).add(0x400000).toString(16));
      send({write:nameByHandle[h], len:args[6].toInt32(), stack:bt});
    }
  }
});
send({armed:true});
""")
def on_msg(m, d):
    p = m.get('payload') if m.get('type') == 'send' else m
    if isinstance(p, dict) and p.get('armed'):
        print("ARMED", flush=True)
    else:
        OUT.write(json.dumps(p) + "\n"); OUT.flush(); print("WRITE", json.dumps(p)[:200], flush=True)
script.on('message', on_msg)
script.load()
while True:
    time.sleep(2)
