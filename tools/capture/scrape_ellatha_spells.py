#!/usr/bin/env python3
"""Scrape spell LEVELS from ellatha (DaOpa's AoC fansite) - the only text source for per-spell unlock level.

The client RDB spell record (type 1040005) does NOT carry the character-level requirement (level is class-
progression/trainer data - see decode_spell_fields.py), so we source it here. USE THIS FOR LEVELS ONLY:
ellatha's other stats are wrong (e.g. Chill rank 5 lists mana 107 vs the in-game 417).

The list paginates at spellslist.asp?start=N (N = 1, 21, 41, ... step 20; "Records 1 to 20 of 587"). Each row is
  <td><a ...spellsview.asp?title=NAME...>NAME</a>&nbsp;</td><td>RANK</td><td>LEVEL</td><td>MANA</td><td>CAST</td>
Ellatha lists each spell at its lowest rank, so this yields the rank-1 unlock level for ~587 spells. Higher
ranks unlock on the class rank-level progression (not covered here).

Usage:
  scrape_ellatha_spells.py            # scrape all pages, print TSV: name<TAB>rank<TAB>level<TAB>mana<TAB>cast
  scrape_ellatha_spells.py --json     # emit a JSON list instead
  scrape_ellatha_spells.py --out F    # write to file F
Output is intended as the input to a (future) RdbSpellImporter level backfill: match faolan.db
Spells.Name == f"{name} (Rank {rank})" and set Level.
"""
import re, sys, json, time, subprocess

BASE = "https://www.ellatha.com/aoc/spellslist.asp"
ROW = re.compile(
    r'spellsview\.asp\?title=[^"]+">([^<]+)</a>&nbsp;</td>\s*'
    r'<td>\s*(\d+)\s*&nbsp;</td>\s*'      # rank
    r'<td>\s*(\d+)\s*&nbsp;</td>\s*'      # level
    r'<td>\s*(\d*)\s*&nbsp;</td>\s*'      # mana (may be blank)
    r'<td>\s*([^<&]*)\s*&nbsp;</td>',     # cast (may be blank)
    re.S)


def fetch(start):
    # curl is more robust here than urllib (the server uses chunked transfer that urllib stalls on).
    url = BASE + (f"?start={start}" if start > 1 else "?cmd=resetall")
    out = subprocess.run(["curl", "-s", "--max-time", "30", "-A", "Mozilla/5.0", url],
                         capture_output=True)
    return out.stdout.decode("latin-1")


def scrape():
    rows, start = [], 1
    while True:
        html = fetch(start)
        found = ROW.findall(html)
        if not found:
            break
        for name, rank, level, mana, cast in found:
            rows.append((name.strip(), int(rank), int(level), int(mana) if mana.strip() else None, cast.strip()))
        m = re.search(r"Records\s+\d+\s+to\s+\d+\s+of\s+(\d+)", html)
        total = int(m.group(1)) if m else None
        if total and len(rows) >= total:
            break
        start += 20
        time.sleep(0.4)  # be polite
        if start > 2000:  # safety
            break
    # de-dup (a row can repeat if pagination overlaps), keep first
    seen, uniq = set(), []
    for r in rows:
        k = (r[0], r[1])
        if k not in seen:
            seen.add(k); uniq.append(r)
    return uniq


def main():
    rows = scrape()
    out = sys.stdout
    if "--out" in sys.argv:
        out = open(sys.argv[sys.argv.index("--out") + 1], "w")
    if "--json" in sys.argv:
        json.dump([{"name": n, "rank": rk, "level": lv, "mana": mn, "cast": ct} for n, rk, lv, mn, ct in rows], out, indent=1)
    else:
        for n, rk, lv, mn, ct in rows:
            out.write(f"{n}\t{rk}\t{lv}\t{mn if mn is not None else ''}\t{ct}\n")
    sys.stderr.write(f"scraped {len(rows)} spells\n")


if __name__ == "__main__":
    main()
