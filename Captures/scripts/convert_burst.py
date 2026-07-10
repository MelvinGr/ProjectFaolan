#!/usr/bin/env python3
"""Codemod: convert a Retail*Burst.cs verbatim-blob method to Iir()/IirId() helper calls (byte-identical)."""
import re, sys
CS = sys.argv[1]
enum_txt = open("Faolan.Core.Network/Constants/Opcodes.cs").read()
m = re.search(r"enum GameServer0X2000RespondsOpcodes\s*:\s*uint\s*\{(.*?)\n\t\t\}", enum_txt, re.S)
val2name = {}
for name, val in re.findall(r"(\w+)\s*=\s*0x([0-9A-Fa-f]+)", m.group(1)):
    val2name.setdefault(int(val, 16), name)
src = open(CS).read()
subids, unknown = set(), set()
def pb(s): return [int(x, 16) for x in re.findall(r"0x([0-9A-Fa-f]{1,2})", s)]
def conv(block):
    if ", 0x2000)" in block: marker = ", 0x2000)"
    elif ", 0x00002000)" in block: marker = ", 0x00002000)"
    else: return block
    if re.search(r"\.WriteArray\(\s*[A-Za-z]", block) or "EncodeVarint" in block or "WithEmbedded" in block: return block
    sarr = re.findall(r"\[([^\]]*)\]", block)
    if not sarr or pb(sarr[0])[:5] != [0x0D, 0x13, 0xCE, 0x71, 0xB1]: return block  # only GameAgent->GameInterface 0x2000 IIR
    after = block[block.index(marker) + len(marker):]
    toks = []
    for mm in re.finditer(r"\.WriteArray\(([^)]*)\)|\.WriteUInt32\(id\)", after):
        toks.append("ID") if mm.group(0).startswith(".WriteUInt32") else toks.extend(pb(mm.group(1)))
    if len(toks) < 8 or toks[0] == "ID" or toks[4] == "ID": return block
    subop = (toks[4] << 24) | (toks[5] << 16) | (toks[6] << 8) | toks[7]
    body = toks[8:]
    name = val2name.get(subop)
    ref = f"GameServer0X2000RespondsOpcodes.{name}" if name else f"(GameServer0X2000RespondsOpcodes)0x{subop:08X}u"
    if not name: unknown.add(subop)
    f = lambda bs: ", ".join(f"0x{b:02X}" for b in bs)
    subids.add(pb(re.findall(r"\[([^\]]*)\]", block)[0])[6])
    nid = body.count("ID")
    if nid == 0: return f"await Iir({ref}, {f(body)});"
    if nid == 1:
        i = body.index("ID"); return f"await IirId({ref}, [{f(body[:i])}], {f(body[i+1:])});"
    return block
new = re.sub(r"await new PacketStream\(\)\.WriteHeader\(.*?\.SendAsync\(client\);", lambda mo: conv(mo.group(0)), src, flags=re.S)
if len(subids) != 1:
    print(f"!! sub-ids {subids}; aborting", file=sys.stderr); sys.exit(1)
sid = next(iter(subids))
helpers = ("var id = client.Session.Account.ClientInstance;\n"
 f"\t\t\tvar sender = new byte[] {{ 0x0D, 0x13, 0xCE, 0x71, 0xB1, 0x10, 0x{sid:02X} }};\n"
 "\t\t\tvar receiver = AppendVarint([0x0D, 0x47, 0xC1, 0x67, 0x6C, 0x10], id);\n\n"
 "\t\t\tTask Iir(GameServer0X2000RespondsOpcodes iir, params byte[] body) =>\n"
 "\t\t\t\tnew PacketStream().WriteHeader(sender, receiver, 0x00002000)\n"
 "\t\t\t\t\t.WriteArrayUInt32Length(s => s.WriteUInt32(iir).WriteArray(body)).SendAsync(client);\n\n"
 "\t\t\tTask IirId(GameServer0X2000RespondsOpcodes iir, byte[] prefix, params byte[] suffix) =>\n"
 "\t\t\t\tnew PacketStream().WriteHeader(sender, receiver, 0x00002000)\n"
 "\t\t\t\t\t.WriteArrayUInt32Length(s => s.WriteUInt32(iir).WriteArray(prefix).WriteUInt32(id).WriteArray(suffix)).SendAsync(client);\n")
new = new.replace("var id = client.Session.Account.ClientInstance;", helpers, 1)
if "using Faolan.Core.Network.Packets.Opcodes;" not in new:
    new = new.replace("using Faolan.Core.Network.Packets;\n", "using Faolan.Core.Network.Packets;\nusing Faolan.Core.Network.Packets.Opcodes;\n", 1)
open(CS, "w").write(new)
print(f"converted {CS}; sub-id 0x{sid:02X}; unknown: {[hex(x) for x in unknown]}")
