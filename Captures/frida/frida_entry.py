import frida, time, json, sys
WIN_PID = int(sys.argv[1]) if len(sys.argv) > 1 else 1156
OUT = open('/tmp/claude-1000/-home-melvin-Development-C--Project-Faolan/1056f362-b1be-4721-a7fb-d2143b69e32b/scratchpad/frida_entry.jsonl', 'w')
dev = frida.get_device_manager().add_remote_device('127.0.0.1:27042')
session = dev.attach(WIN_PID)
print("ATTACHED", WIN_PID, flush=True)
script = session.create_script(r"""
// return-addresses seen in armor-mesh load backtraces; find each containing function's ENTRY (safe to hook)
const RET = [0x1842846, 0x18574ba, 0x1845ea3, 0x1061aaf, 0x1071b45];
const WANT = [13117, 323725, 323713, 700815, 700842, 4842950, 700816, 323726, 700843, 4842951];
function findEntry(ret){
  // scan back up to 0x800 bytes for a standard prologue: 8B FF 55 8B EC (hotpatch) or 55 8B EC
  for (let off=0; off<0x800; off++){
    const p = ptr(ret - off);
    try {
      const b = p.readByteArray(5); const u = new Uint8Array(b);
      if (u[0]==0x8B && u[1]==0xFF && u[2]==0x55 && u[3]==0x8B && u[4]==0xEC) return p;
      if (u[0]==0x55 && u[1]==0x8B && u[2]==0xEC) return p;
    } catch(e){ return null; }
  }
  return null;
}
function ints(ctx){
  const r={eax:ctx.eax.toUInt32(),ecx:ctx.ecx.toUInt32(),edx:ctx.edx.toUInt32(),ebx:ctx.ebx.toUInt32(),esi:ctx.esi.toUInt32(),edi:ctx.edi.toUInt32()};
  const args=[]; for(let i=1;i<=6;i++){ try{ args.push(ctx.esp.add(i*4).readU32()); }catch(e){ args.push(0);} }
  return {r, args};
}
RET.forEach(ret=>{
  const e = findEntry(ret);
  if (!e){ send({err:'no entry for 0x'+ret.toString(16)}); return; }
  const entry = '0x'+e.toString(16);
  try {
    Interceptor.attach(e, {
      onEnter(a){
        const s = ints(this.context);
        const hitIn = [];
        for (const v of s.args) if (WANT.indexOf(v)>=0) hitIn.push(v);
        for (const k in s.r) if (WANT.indexOf(s.r[k])>=0) hitIn.push(k+'='+s.r[k]);
        this._log = (hitIn.length>0);
        if (this._log) send({fn:entry, ret:'0x'+ret.toString(16), phase:'enter', args:s.args, ecx:s.r.ecx, in:hitIn});
      },
      onLeave(rv){
        const v = rv.toUInt32();
        if (this._log || WANT.indexOf(v)>=0) send({fn:entry, ret:'0x'+ret.toString(16), phase:'leave', eax:v});
      }
    });
    send({mapped: ret.toString(16)+' -> entry '+entry});
  } catch(err){ send({err:'hook '+entry+': '+err}); }
});
send({armed:true});
""")
def on_msg(m, d):
    p = m.get('payload') if m.get('type') == 'send' else m
    if isinstance(p, dict) and p.get('armed'):
        print("ARMED", flush=True)
    else:
        OUT.write(json.dumps(p) + "\n"); OUT.flush(); print("MSG", p, flush=True)
script.on('message', on_msg)
script.load()
while True:
    time.sleep(2)
