import frida, json, sys, time
GA=json.load(open(sys.argv[1]))       # ga271.json
OUT=sys.argv[2]
ptrs=[t['rawdesc'][0] for t in GA['types']]
dev=frida.get_device_manager().add_remote_device('127.0.0.1:27042')
aoc=[p for p in dev.enumerate_processes() if 'AgeOfConan' in p.name][0]
session=dev.attach(aoc.pid)
script=session.create_script(r"""
rpc.exports.readstrs = function(ptrs){
  var res=[];
  for (var i=0;i<ptrs.length;i++){
    var p=ptr(ptrs[i]);
    var s=null, s2=null;
    try { s = p.readCString(); } catch(e){}
    // also try one level of indirection (p -> char*)
    try { s2 = p.readPointer().readCString(); } catch(e){}
    res.push([s, s2]);
  }
  return res;
};
""")
script.load()
strs=script.exports_sync.readstrs([hex(p) for p in ptrs])
result=[]
for t,(s,s2) in zip(GA['types'], strs):
    result.append({'t':t['t'], 'realid':t['rawdesc'][2], 'name':s, 'name_ind':s2,
                   'keys':[k['key'] for k in t['keys']]})
json.dump(result, open(OUT,'w'), indent=0)
# print candidates
def printable(s): return s and all(32<=ord(c)<127 for c in s) and len(s)>2
named=[r for r in result if printable(r['name']) or printable(r['name_ind'])]
print("named:", len(named), "of", len(result), flush=True)
for r in result[:8]:
    print(hex(r['t']), repr(r['name']), repr(r['name_ind']), flush=True)
session.detach()
