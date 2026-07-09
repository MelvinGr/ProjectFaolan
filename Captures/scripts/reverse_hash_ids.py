#!/usr/bin/env python3
"""Stage 5 #2: name ANY BKDR-31-hashed 32-bit id found inside packet bodies (not just opcodes/endpoints).

The client hashes class/message/token names with BKDR-31 (h = h*31 + c). We already use this for opcodes and
endpoint types; many other 32-bit ids on the wire are the same kind of hash (RemoteFormat / TradePost tokens,
GameTweak keys, trigger/scry ids, ...). This hashes every identifier in AgeOfConan.exe, then scans every 4-byte
word in every capture body (both endiannesses) and reports the ones that resolve to a real exe name.

Usage: reverse_hash_ids.py <AgeOfConan.exe> <Captures-dir> [out.md]
"""
import re, json, sys, glob, os, struct, subprocess, collections

EXE = sys.argv[1] if len(sys.argv) > 1 else "/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/AgeOfConan.exe"
CAPDIR = sys.argv[2] if len(sys.argv) > 2 else os.path.dirname(os.path.abspath(__file__))
OUT = sys.argv[3] if len(sys.argv) > 3 else "/tmp/reverse_hash_ids.md"

def bkdr31(s):
    h = 0
    for b in s.encode("ascii", "ignore"):
        h = (h * 31 + b) & 0xFFFFFFFF
    return h

# reverse map: every identifier token in the exe -> its hash
strings = subprocess.run(["strings", "-n", "4", EXE], capture_output=True, text=True, errors="replace").stdout
tok = re.compile(r"[A-Za-z_][A-Za-z0-9_]{2,}")
rev = collections.defaultdict(set)
for t in set(tok.findall(strings)):
    rev[bkdr31(t)].add(t)

# the ids we already account for elsewhere (opcodes / endpoint types) - skip so we surface NEW ones
KNOWN = {
    0x6C67C147, 0xB171CE13, 0xA9ECB95D, 0x8B87F791, 0x5B9756FC, 0x28377648,  # endpoint types
    0x7D155738, 0xBBD560C8, 0x6DD55614, 0x82F20484, 0x0C384054, 0x604DDBA0, 0xF2C2D5B5, 0x3D744981,
}

def best_name(idv):
    cands = rev.get(idv)
    if not cands:
        return None
    # prefer names that look like real client identifiers (CamelCase / known suffixes), longest-ish
    def score(n):
        s = 0
        if re.search(r"(IIR|_e|_c|_t|GUI|Format|Token|Tweak|Trigger|Script|Message|Type)$", n): s += 50
        if re.search(r"[a-z][A-Z]", n): s += 20   # camelCase
        s += min(len(n), 30)
        return s
    return max(cands, key=score)

# scan capture bodies — the .tshark.json dump already de-frames each packet (opcode + body are fields), and
# all Conan services collapse to Protocol="Conan", so the context label is the server-side endpoint class id.
def service_label(p):
    if p.get("Protocol") == "AgentServer":
        return "AgentServer"
    cls = p.get("SenderClass") if p.get("IsDownload") else p.get("ReceiverClass")
    return cls or p.get("Protocol") or "?"

hits = collections.Counter()
hit_ctx = collections.defaultdict(set)   # name -> {(proto, opcode)}
hit_name = {}

for fn in sorted(glob.glob(os.path.join(CAPDIR, "*.tshark.json"))):
    try:
        d = json.load(open(fn))
    except Exception:
        continue
    for p in d.get("Packets", []):
        op = int(p["Opcode"], 16) if p.get("Opcode") else None
        if op is None:
            continue
        body = bytes.fromhex(p.get("BodyHex") or "")
        proto = service_label(p)
        for i in range(0, len(body) - 3):
            for endian in ("<I", ">I"):
                v = struct.unpack_from(endian, body, i)[0]
                if v < 0x10000 or v in KNOWN:
                    continue
                nm = best_name(v)
                if nm:
                    hits[v] += 1
                    hit_name[v] = nm
                    hit_ctx[v].add((proto, f"0x{op:04X}"))

# emit: rank by count, keep meaningful names (require a letter run >= 4 and not pure-hex-ascii coincidence)
def meaningful(n):
    return bool(re.search(r"[A-Za-z]{4,}", n)) and not n.isdigit()

rows = [(v, hit_name[v], c, hit_ctx[v]) for v, c in hits.items() if meaningful(hit_name[v])]
rows.sort(key=lambda r: -r[2])

out = ["# Reverse-hashed body ids (BKDR-31) — Stage 5 #2\n",
       "32-bit ids found INSIDE packet bodies across all captures that BKDR-31-resolve to a real `AgeOfConan.exe`\n"
       "identifier. Opcode/endpoint ids are excluded (covered in `docs/re/aoc-opcode-ids.md`). High-count rows are\n"
       "almost certainly real names; single-count rows may be hash collisions — treat as leads, confirm in context.\n",
       "\n| id | count | resolved name | seen in (proto/opcode) |\n|---|---|---|---|"]
for v, nm, c, ctx in rows[:200]:
    ctxs = ", ".join(sorted(f"{a}:{b}" for a, b in ctx)[:6])
    out.append(f"| 0x{v:08X} | {c} | {nm} | {ctxs} |")
open(OUT, "w").write("\n".join(out) + "\n")

print(f"distinct resolved body ids: {len(rows)}  (wrote {OUT})")
print("top 30:")
for v, nm, c, ctx in rows[:30]:
    print(f"  0x{v:08X} x{c:<5} {nm}")
