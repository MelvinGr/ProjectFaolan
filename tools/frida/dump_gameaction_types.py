import frida, json, sys, time
OUT=sys.argv[1] if len(sys.argv)>1 else "/tmp/ga271.json"
dev=frida.get_device_manager().add_remote_device('127.0.0.1:27042')
aoc=[p for p in dev.enumerate_processes() if 'AgeOfConan' in p.name][0]
print("attaching", aoc.pid, flush=True)
session=dev.attach(aoc.pid)
script=session.create_script(r"""
var STATKEYS = ptr('0x025073c0');   // byte-index -> real stat key (256 u32)
var TABLE    = ptr('0x025077c8');   // descriptor table base
var STRIDE   = 0x41;
var FIRST=0xCF09, LAST=0xD026;

function rdU32(p){ try { return p.readU32(); } catch(e){ return null; } }

var keyTable=[];
for (var i=0;i<256;i++){ keyTable.push(rdU32(STATKEYS.add(i*4))); }

var out=[];
for (var t=FIRST;t<=LAST;t++){
  var desc = TABLE.add((t-FIRST)*STRIDE);
  var kl = desc.add(0xc);
  var keys=[];
  var rawdesc=[];
  for (var b=0;b<0x41;b+=4){ rawdesc.push(rdU32(desc.add(b))); }
  for (var k=0;k<48;k++){
    var e=rdU32(kl.add(k*4));
    if (e===null) break;
    if ((e & 0xff)===0xff) break;
    var idx=e & 0xff;
    keys.push({idx:idx, key:keyTable[idx]});
  }
  out.push({t:t, keys:keys, rawdesc:rawdesc});
}
send({statkeys:keyTable, types:out});
""")
result={}
def on_msg(m,d):
    global result
    if m.get('type')=='send':
        result=m['payload']
    else:
        print("MSG", m, flush=True)
script.on('message', on_msg)
script.load()
time.sleep(2)
json.dump(result, open(OUT,'w'))
print("WROTE", OUT, "types:", len(result.get('types',[])), flush=True)
session.detach()
