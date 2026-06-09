#!/usr/bin/env python3
"""Stage 3 of docs/opcode-re-plan.md: decode the wire ids using the recovered BKDR-31 name hash.

Stage 2 cracked the client's id hash: h = h*31 + c over the ASCII class name (Java String.hashCode / BKDR-31).
  - ProtoEndpoint.type ids   = hash(interface/agent class name), e.g. hash("GameCharInterface")=0x8B87F791
  - 32-bit sub-opcodes (in 0x2000) = hash(IIR message-type name), e.g. hash("StatUpdateIIR_c")=0x96B8DC59

This scans every Captures/*.json across ALL servers (Universe/Player/CSPlayer/Game), collects the distinct
endpoint type ids and 32-bit sub-opcodes, and resolves each to a name by hashing every identifier in the client
exe and matching. (16-bit RpcHeader opcodes like 0x2052 are per-interface method indices, NOT hashes -> Stage 3b.)

Usage: decode_opcode_ids.py <AgeOfConan.exe> <Captures-dir> [out.md]
"""
import re, json, sys, glob, os, struct, subprocess, collections

EXE = sys.argv[1] if len(sys.argv) > 1 else "/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/AgeOfConan.exe"
CAPDIR = sys.argv[2] if len(sys.argv) > 2 else os.path.dirname(os.path.abspath(__file__))
OUT = sys.argv[3] if len(sys.argv) > 3 else "/tmp/aoc_opcode_ids.md"

def bkdr31(s):
    h = 0
    for b in s.encode("ascii", "ignore"):
        h = (h * 31 + b) & 0xFFFFFFFF
    return h

# --- reverse map: hash every identifier token in the exe ---
strings = subprocess.run(["strings", "-n", "4", EXE], capture_output=True, text=True, errors="replace").stdout
tok_re = re.compile(r"[A-Za-z_][A-Za-z0-9_]{2,}")
rev = collections.defaultdict(set)
for t in set(tok_re.findall(strings)):
    rev[bkdr31(t)].add(t)

def rank(name, want_iir):
    # prefer the real client type names: IIR message names for sub-opcodes; Interface/Agent for endpoint types
    score = 0
    if want_iir:
        if "IIR" in name: score += 100
        if re.search(r"IIR(_[tc])?$", name): score += 50
    else:
        if re.search(r"(Interface|Agent)(Project)?$", name): score += 100
    score -= len(name) * 0.1
    return score

def resolve(idv, want_iir):
    cands = rev.get(idv)
    if not cands:
        return None, []
    best = max(cands, key=lambda n: rank(n, want_iir))
    return best, sorted(cands, key=lambda n: -rank(n, want_iir))

# --- parse captures ---
def parse(raw):
    import base64
    b = base64.b64decode(raw)
    if len(b) < 14 or b[12] != 0x0a:
        return None
    off = 12
    sl = b[off + 1]; sender = b[off + 2:off + 2 + sl]; off += 2 + sl
    if off + 2 > len(b) or b[off] != 0x12:
        return None
    rl = b[off + 1]; recv = b[off + 2:off + 2 + rl]; off += 2 + rl
    if off + 2 > len(b):
        return None
    op = struct.unpack(">H", b[off:off + 2])[0]; off += 2
    return sender, recv, op, b[off:]

def endpoint_type(blob):
    # [0x0d <type LE32> 0x10 <instance varint>]
    if len(blob) >= 5 and blob[0] == 0x0d:
        return struct.unpack("<I", blob[1:5])[0]
    return None

endpoint_ids = collections.Counter()
subops = collections.Counter()          # 32-bit subopcode -> count
subops_by_proto = collections.defaultdict(collections.Counter)
op16_by_proto = collections.defaultdict(collections.Counter)

for fn in sorted(glob.glob(os.path.join(CAPDIR, "*.json"))):
    try:
        d = json.load(open(fn))
    except Exception:
        continue
    for p in d.get("Packets", []):
        r = parse(p["RawBytesBase64"])
        if not r:
            continue
        sender, recv, op, body = r
        proto = p.get("ProtocolName", "?")
        for blob in (sender, recv):
            t = endpoint_type(blob)
            if t is not None:
                endpoint_ids[t] += 1
        op16_by_proto[proto][op] += 1
        if op == 0x2000 and len(body) >= 8:
            sub_bytes = body[4:8]
            sub = struct.unpack(">I", sub_bytes)[0]
            # skip the tiny 0x00002cXX family, and bodies whose "sub" is plainly ASCII text (auth challenges /
            # session-handoff hex ids carried in 0x2000 bodies that don't use the [len][subopcode] layout).
            all_ascii = all(0x20 <= c <= 0x7E for c in sub_bytes)
            if sub > 0xFFFF and not all_ascii:
                subops[sub] += 1
                subops_by_proto[proto][sub] += 1

# --- emit ---
out = []
out.append("# AoC wire-id decode (BKDR-31 name hash) - all servers\n")
out.append("Stage 3 of `docs/opcode-re-plan.md`. The client id hash is **BKDR-31** (`h = h*31 + c`, Java's\n"
           "`String.hashCode`), cracked in Stage 2 (18/18 known anchors). Ids below are resolved by hashing every\n"
           "identifier in `AgeOfConan.exe` and matching. `[?]` = no exe string hashes to it (name not in binary, or\n"
           "a collision picked wrong); 16-bit RpcHeader opcodes are method indices, not hashes (see Stage 3b).\n")

out.append("\n## ProtoEndpoint.type ids (object-class / interface names)\n")
out.append("\n| id | count | resolved name | other candidates |\n|---|---|---|---|")
for idv, cnt in sorted(endpoint_ids.items(), key=lambda kv: -kv[1]):
    best, cands = resolve(idv, want_iir=False)
    others = ", ".join(c for c in cands[1:4]) if len(cands) > 1 else ""
    out.append(f"| 0x{idv:08X} | {cnt} | {best or '[?]'} | {others} |")

out.append("\n## 32-bit sub-opcodes inside 0x2000 (IIR / message type names)\n")
out.append("\n| sub-opcode | count | resolved name | servers |\n|---|---|---|---|")
for idv, cnt in sorted(subops.items(), key=lambda kv: -kv[1]):
    best, cands = resolve(idv, want_iir=True)
    protos = ",".join(sorted({pr for pr, c in subops_by_proto.items() if idv in c}))
    out.append(f"| 0x{idv:08X} | {cnt} | {best or '[?]'} | {protos} |")

out.append("\n## 16-bit RpcHeader opcodes per server (method indices - Stage 3b, NOT hashes)\n")
for proto in sorted(op16_by_proto):
    ops = op16_by_proto[proto]
    listing = " ".join(f"0x{o:04X}({c})" for o, c in sorted(ops.items(), key=lambda kv: -kv[1]))
    out.append(f"\n**{proto}**: {listing}")

open(OUT, "w").write("\n".join(out) + "\n")

# stdout summary
res = sum(1 for i in subops if resolve(i, True)[0])
print(f"endpoint type ids: {len(endpoint_ids)} ({sum(1 for i in endpoint_ids if resolve(i,False)[0])} resolved)")
print(f"32-bit sub-opcodes: {len(subops)} ({res} resolved)")
print(f"wrote {OUT}")
print("\nsub-opcode resolutions:")
for idv, cnt in sorted(subops.items(), key=lambda kv: -kv[1]):
    best, _ = resolve(idv, True)
    print(f"  0x{idv:08X} x{cnt:<5} {best or '[?]'}")
