#!/usr/bin/env python3
"""Find protocol ids that appear on the wire (in the capture dumps) but are MISSING from Opcodes.cs.

This is how FeatIIR (0x28da68a0) and SetShadowCoverageIIR (0xd6c1f0cc) were found: sweep every capture for the
32-bit IIR hashes and report the ones our enum doesn't define, then reverse the BKDR-31 hash against
AgeOfConan.exe to name them. Also lists opcodes whose METHOD name the dissector doesn't know (shown as "Ox<hex>").

BKDR-31: h = h*31 + byte. The client hashes each RTTI class name (usually the "_c"/"_t"-suffixed form) to the
32-bit id on the wire, so a match against an exe string is authoritative.

Usage: find_missing_opcodes.py [AgeOfConan.exe]
"""
import glob
import json
import re
import subprocess
import sys
from collections import Counter
from pathlib import Path
import sys as _sys, pathlib as _pl
_sys.path.insert(0, str(_pl.Path(__file__).resolve().parent))
from captures import capture_files, capture  # corpus lives in the ProjectFaolan-Data repo

ROOT = Path(__file__).resolve().parent.parent.parent
EXE = sys.argv[1] if len(sys.argv) > 1 else '/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/AgeOfConan.exe'


def bkdr31(s):
    h = 0
    for b in s.encode('ascii', 'ignore'):
        h = (h * 31 + b) & 0xFFFFFFFF
    return h


def main():
    # 1) every 32-bit hash constant defined in Opcodes.cs
    op = (ROOT / 'Faolan.Core.Network/Constants/Opcodes.cs').read_text()
    defined = {int(h, 16) for h in re.findall(r'=\s*0x([0-9A-Fa-f]{8})\b', op)}

    # 2) sweep captures for IIR hashes + unnamed methods
    iir, unnamed = Counter(), Counter()
    for cap in capture_files("*.tshark.json"):
        try:
            d = json.load(open(cap))
        except Exception:
            continue
        for p in d.get('Packets', []):
            ii = p.get('Iir')
            if ii:
                try:
                    iir[int(ii, 16)] += 1
                except ValueError:
                    pass
            m = p.get('Method') or ''
            if re.fullmatch(r'Ox[0-9A-Fa-f]+', m):  # the dissector's "unknown method" marker
                unnamed[(p['Direction'], p['Opcode'])] += 1

    # 3) reverse-hash the unknown IIRs against the exe
    rev = {}
    if Path(EXE).exists():
        strings = subprocess.run(['strings', '-n', '4', EXE], capture_output=True, text=True, errors='replace').stdout
        for t in set(re.findall(r'[A-Za-z_][A-Za-z0-9_]{2,}', strings)):
            rev.setdefault(bkdr31(t), []).append(t)

    def name(h):
        c = rev.get(h)
        if not c:
            return '?? (no exe match)'
        return ([x for x in c if x.endswith(('IIR', 'IIR_c', 'IIR_t'))] or sorted(c, key=len))[0]

    unknown = {h: n for h, n in iir.items() if h not in defined}
    print(f'IIR hashes: {len(iir)} on the wire, {len(defined)} defined in Opcodes.cs, {len(unknown)} MISSING')
    for h, n in sorted(unknown.items(), key=lambda kv: -kv[1]):
        print(f'  0x{h:08X}  x{n:<5} -> {name(h)}')
    if not unknown:
        print('  (none - every IIR on the wire is in Opcodes.cs)')

    print(f'\nunnamed methods (Ox…) - opcode known, friendly name not: {len(unnamed)} distinct')
    for (dr, o), n in unnamed.most_common():
        print(f'  {dr} {o}  x{n}')

    # 4) IIR CLASSES the client defines (exe RTTI) that have NO enum member and whose hash forms are all undefined
    #    - i.e. record types we've never named AND never seen in a dump. Match by NAME or any {bare,_c,_t} hash so
    #    records we already have under a different enum name (MagicBuffIIR==ApplySpell, PetIIR, …) are not re-flagged.
    have_names = {m.lower() for m in re.findall(r'(\w+IIR)\s*=\s*0x', op)}
    junk = {'AnyIIR', 'SendIIR', 'AVIIR'}
    raw = set(re.findall(r'[A-Za-z_][A-Za-z0-9_]*IIR(?:_[ct])?', open(EXE, 'rb').read().decode('latin1'))) if Path(EXE).exists() else set()
    exe = {re.sub(r'_[ct]$', '', re.sub(r'^AV', '', n)) for n in raw}
    exe = {n for n in exe if n.endswith('IIR') and len(n) > 5 and n[0].isupper() and n not in junk}

    def known(n):
        return n.lower() in have_names or any(bkdr31(n + s) in defined for s in ('', '_c', '_t'))

    unseen = sorted(n for n in exe if not known(n))
    print(f'\nIIR classes in the client (exe RTTI) that we have NEVER named or seen: {len(unseen)}')
    for n in unseen:
        tag = ' [debug]' if 'Debug' in n else (' [base/proto?]' if n.startswith(('Core', 'Full', 'Proto', 'GameCodeDynel', 'V')) else '')
        print(f'  {n:<30} bare=0x{bkdr31(n):08X} _c=0x{bkdr31(n + "_c"):08X}{tag}')


if __name__ == '__main__':
    main()
