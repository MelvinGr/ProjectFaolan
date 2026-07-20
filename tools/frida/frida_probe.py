import frida, sys, time
dev = frida.get_device_manager().add_remote_device('127.0.0.1:27042')
session = dev.attach(308)
print("ATTACHED to AgeOfConan.exe (win pid 308)")
script = session.create_script(r"""
const nt = Module.getExportByName('ntdll.dll', 'NtReadFile');
let n = 0;
Interceptor.attach(nt, {
  onEnter(args) {
    if (n >= 6) return;
    n++;
    const bt = Thread.backtrace(this.context, Backtracer.ACCURATE)
      .map(a => a.toString());
    send({bt: bt});
  }
});
send({ready: true});
""")
seen = []
def on_msg(m, data):
    p = m.get('payload', {})
    if p.get('ready'): print("HOOK ARMED")
    elif 'bt' in p:
        seen.append(p['bt'])
        print("BT:", p['bt'])
script.on('message', on_msg)
script.load()
time.sleep(8)
print("captured", len(seen), "backtraces")
session.detach()
