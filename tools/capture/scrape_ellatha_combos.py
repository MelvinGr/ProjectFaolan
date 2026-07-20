#!/usr/bin/env python3
"""Scrape COMBO stats from ellatha (DaOpa's AoC fansite) - the melee/physical-class combos (#139, ability defs #78).

Sibling of scrape_ellatha_spells.py. The combos list (comboslist.asp) has columns
  Icon | Name | Level | Cooldown | Stamina | Archetype
so - unlike the spells list - it DOES carry a cooldown (e.g. Sweep: L2, CD 10, Stam 20, Guardian). USE FOR
LEVEL/COOLDOWN/STAMINA cross-reference only; ellatha's spell stats were found unreliable (mana), so sanity-check
combo numbers against retail too.

Pagination: comboslist.asp?start=N (N = 1, 21, ... step 20; "Records 1 to 20 of 285"). Row:
  <td><a ...combosview.asp?title=NAME...>NAME</a>&nbsp;</td><td>LEVEL</td><td>CD</td><td>STAMINA</td><td>ARCH</td>

Usage:
  scrape_ellatha_combos.py            # TSV: name<TAB>level<TAB>cooldown<TAB>stamina<TAB>archetype
  scrape_ellatha_combos.py --json     # JSON list
  scrape_ellatha_combos.py --out F    # write to F
"""
import re, sys, json, time, subprocess

BASE = "https://www.ellatha.com/aoc/comboslist.asp"
ROW = re.compile(
    r'combosview\.asp\?title=[^"]+">([^<]+)</a>&nbsp;</td>\s*'
    r'<td>\s*(\d+)\s*&nbsp;</td>\s*'      # level
    r'<td>\s*(\d*)\s*&nbsp;</td>\s*'      # cooldown
    r'<td>\s*(\d*)\s*&nbsp;</td>\s*'      # stamina
    r'<td>\s*([^<&]*)\s*&nbsp;</td>',     # archetype
    re.S)


def fetch(start):
    url = BASE + (f"?start={start}" if start > 1 else "?cmd=resetall")
    return subprocess.run(["curl", "-s", "--max-time", "30", "-A", "Mozilla/5.0", url],
                          capture_output=True).stdout.decode("latin-1")


def scrape():
    rows, start, seen = [], 1, set()
    while True:
        html = fetch(start)
        found = ROW.findall(html)
        if not found:
            break
        for name, level, cd, stam, arch in found:
            k = (name.strip(), arch.strip(), int(level))
            if k in seen:
                continue
            seen.add(k)
            rows.append((name.strip(), int(level),
                         int(cd) if cd.strip() else None,
                         int(stam) if stam.strip() else None, arch.strip()))
        m = re.search(r"Records\s+\d+\s+to\s+\d+\s+of\s+(\d+)", html)
        total = int(m.group(1)) if m else None
        # advance until a page yields no NEW rows (the list has many same-name dupes across classes)
        start += 20
        time.sleep(0.4)
        if (total and start > total + 20) or start > 2000:
            break
    return rows


def main():
    rows = scrape()
    out = open(sys.argv[sys.argv.index("--out") + 1], "w") if "--out" in sys.argv else sys.stdout
    if "--json" in sys.argv:
        json.dump([{"name": n, "level": lv, "cooldown": cd, "stamina": st, "archetype": ar} for n, lv, cd, st, ar in rows], out, indent=1)
    else:
        for n, lv, cd, st, ar in rows:
            out.write(f"{n}\t{lv}\t{cd if cd is not None else ''}\t{st if st is not None else ''}\t{ar}\n")
    sys.stderr.write(f"scraped {len(rows)} combos\n")


if __name__ == "__main__":
    main()
