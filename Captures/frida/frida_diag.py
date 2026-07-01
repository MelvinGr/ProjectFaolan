import frida, time
dev = frida.get_device_manager().add_remote_device('127.0.0.1:27042')
session = dev.attach(308)
print("ATTACHED")
script = session.create_script(r"""
try {
  const m = Process.getModuleByName('AgeOfConan.exe');
  send({module: {name: m.name, base: m.base.toString(), size: m.size}});
} catch (e) { send({err: 'module: '+e}); }
try {
  const nt = Module.getExportByName('ntdll.dll', 'NtReadFile');
  send({nt: nt.toString()});
  let n=0;
  Interceptor.attach(nt, { onEnter(a){ if(n<3){n++; send({bt: Thread.backtrace(this.context, Backtracer.FUZZY).map(x=>x.toString())});} } });
  send({armed:true});
} catch(e){ send({err: 'hook: '+e}); }
""")
def on_msg(m, d):
    print("MSG:", m.get('payload') if m['type']=='send' else m)
script.on('message', on_msg)
script.load()
time.sleep(5)
session.detach()
