#!/usr/bin/env python3
"""Stage 1 of docs/re/opcode-re-plan.md: extract the client's RPC method catalog from AgeOfConan.exe strings.

Two evidence sources in the binary:
  - proxy  (client->server): "<Class>Proxy_t::Method(<typed sig>) streamed payload of size "
  - dispatch(server->client): "<Class>Dispatch_t::DispatchMethod() ... stream bad after '<field>'!"
    (one line per field, emitted in field/read order -> recovers the wire field order + names)
"""
import re, json, sys, subprocess, collections

# Usage: extract_rpc_catalog.py <AgeOfConan.exe | strings-dump.txt> [out-prefix]
# Runs `strings` on the exe (or reads a pre-dumped text file), writes <prefix>.md + <prefix>.json.
SRC = sys.argv[1] if len(sys.argv) > 1 else "/tmp/aoc_strings.txt"
OUT = sys.argv[2] if len(sys.argv) > 2 else "/tmp/aoc_rpc_methods"
if SRC.lower().endswith((".exe", ".dll")):
    lines = subprocess.run(["strings", "-n", "6", SRC], capture_output=True, text=True, errors="replace").stdout.splitlines()
else:
    lines = open(SRC, errors="replace").read().splitlines()

# --- client->server: proxy signatures -------------------------------------------------
proxy_re = re.compile(r"^([A-Za-z0-9_]+)(Proxy_t)::([A-Za-z0-9_]+)\((.*?)\)\s+streamed payload")
# --- server->client: dispatch field reads ---------------------------------------------
disp_re  = re.compile(r"^([A-Za-z0-9_]+)(Dispatch_t)::([A-Za-z0-9_]+)\(\).*?stream bad after '([^']*)'")
# dispatch methods that DO carry an inline signature (some do)
disp_sig_re = re.compile(r"^([A-Za-z0-9_]+)(Dispatch_t)::([A-Za-z0-9_]+)\((.+?)\)")

proxy = collections.OrderedDict()   # (iface, method) -> sig
disp  = collections.OrderedDict()   # (iface, method) -> [fields in order]
disp_sig = {}                       # (iface, method) -> inline sig if present

def split_sig(sig):
    sig = sig.strip()
    if not sig:
        return []
    # split on top-level commas (no nested templates in these sigs in practice)
    return [p.strip() for p in sig.split(",") if p.strip()]

for ln in lines:
    m = proxy_re.match(ln)
    if m:
        iface, _, method, sig = m.groups()
        proxy.setdefault((iface, method), split_sig(sig))
        continue
    m = disp_re.match(ln)
    if m:
        iface, _, method, field = m.groups()
        lst = disp.setdefault((iface, method), [])
        if field not in lst:
            lst.append(field)
        continue
    m = disp_sig_re.match(ln)
    if m:
        iface, _, method, sig = m.groups()
        if sig.strip():
            disp_sig.setdefault((iface, method), split_sig(sig))

# also list dispatch methods that appear with empty () but no field lines (no args)
empty_disp_re = re.compile(r"^([A-Za-z0-9_]+)(Dispatch_t)::([A-Za-z0-9_]+)\(\)")
for ln in lines:
    m = empty_disp_re.match(ln)
    if m:
        iface, _, method = m.groups()
        disp.setdefault((iface, method), [])

# --- build per-interface model --------------------------------------------------------
ifaces = collections.defaultdict(lambda: {"client_to_server": [], "server_to_client": []})
for (iface, method), sig in proxy.items():
    ifaces[iface]["client_to_server"].append({"method": method, "params": sig})
for (iface, method), fields in disp.items():
    entry = {"method": method, "fields": fields}
    if (iface, method) in disp_sig:
        entry["sig"] = disp_sig[(iface, method)]
    ifaces[iface]["server_to_client"].append(entry)

result = {k: ifaces[k] for k in sorted(ifaces)}

json.dump(result, open(OUT + ".json", "w"), indent=1)

# --- markdown -------------------------------------------------------------------------
tot_c2s = sum(len(v["client_to_server"]) for v in result.values())
tot_s2c = sum(len(v["server_to_client"]) for v in result.values())
out = []
out.append("# AoC client RPC method catalog (extracted from AgeOfConan.exe)\n")
out.append("Stage 1 of `docs/re/opcode-re-plan.md`. Recovered from the client's own log strings - method **names**,\n"
           "**param names/types**, and **direction**. (Numeric ids: opcodes are framework-assigned - see\n"
           "`docs/re/aoc-opcode-ids.md`; the IIR/message + endpoint ids are BKDR-31 name hashes, decoded there.)\n")
out.append(f"\n**{len(result)} interfaces, {tot_c2s} client->server (proxy) methods, {tot_s2c} server->client (dispatch) methods.**\n")
out.append("\n- **client->server** = `<Iface>AgentProxy_t::Method(...)`, signature is authoritative (typed params).\n"
           "- **server->client** = `<Iface>InterfaceDispatch_t::DispatchMethod()`; `fields` are the param NAMES in\n"
           "  wire/read order (from the per-field `stream bad after '<field>'` strings); types aren't in the string.\n")
out.append("\n### Reading the field names (Hungarian prefixes -> wire type)\n"
           "The server->client field names encode their type by prefix (verified against the typed client->server\n"
           "signatures). Use this to read the layout; the proto schema / retail captures stay authoritative for exact\n"
           "bytes. `i_` just marks an input param (ignore it):\n"
           "\n"
           "- `c<Name>` / `i_c<Name>` -> an **ID32** (4-byte object/char/inventory handle) or a serialized object\n"
           "  (e.g. `i_cTargetID`, `i_cInventoryID`, `cName` when it's a string object).\n"
           "- `n<Name>` / `i_n<Name>` -> an **integer** (uint32/int32) (e.g. `i_nSpellID`, `i_nItemPos`, `nShotType`).\n"
           "- `b<Name>` -> a **bool** (e.g. `bAutoface`).\n"
           "- `i_v<Name>` -> a **vector / vec3** (e.g. `i_vHeadPaddingY`) - sometimes a scalar component; check size.\n"
           "- `f<Name>` -> a **float**. Names without a prefix follow the same root convention.\n"
           "- A field whose name is plural / `...List` / `...Data` (e.g. `overrides`, `cQuestData`, `fbFriends`) is a\n"
           "  **repeated/collection** type - it has a nested element encoding not visible in the field name alone.\n")
for iface, m in result.items():
    if not m["client_to_server"] and not m["server_to_client"]:
        continue
    out.append(f"\n## {iface}\n")
    if m["client_to_server"]:
        out.append(f"\n### client -> server ({len(m['client_to_server'])})\n")
        for e in m["client_to_server"]:
            params = ", ".join(e["params"])
            out.append(f"- `{e['method']}({params})`")
    if m["server_to_client"]:
        out.append(f"\n### server -> client ({len(m['server_to_client'])})\n")
        for e in m["server_to_client"]:
            if e.get("sig"):
                params = ", ".join(e["sig"])
            else:
                params = ", ".join(e["fields"])
            out.append(f"- `{e['method']}({params})`")
open(OUT + ".md", "w").write("\n".join(out) + "\n")

print(f"interfaces={len(result)} c2s={tot_c2s} s2c={tot_s2c}")
print("top interfaces by method count:")
for iface, m in sorted(result.items(), key=lambda kv: -(len(kv[1]['client_to_server'])+len(kv[1]['server_to_client'])))[:15]:
    print(f"  {iface:28} c2s={len(m['client_to_server']):3} s2c={len(m['server_to_client']):3}")
