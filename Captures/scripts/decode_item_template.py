#!/usr/bin/env python3
"""Decode the stat-modifier grammar inside a decrypted RDB item-template body.

Source: the decrypted item templates the importer writes with
  dotnet run --project Faolan.Importer -- --skip-characters --rdb <RDB> --rdb-items --dump-templates <dir>
Each file is `<type>_<id>.bin` = the full canonical template: a 12-byte RDB-Id header
([type u32][id u32][marker=1000000 u32], all LE) + the body.

Body grammar (recovered 2026-06-15):
  After a short fixed header, the body is an RDB PROPERTY TABLE: a run of
  [fieldId u32][value u32] pairs with STRICTLY DECREASING fieldId (LE). Field ids are
  RDB template field numbers (973, 972, 971, 857, 615, 499, ...), NOT wire stat ids.

  VALIDATED stat slot - the PRIMARY ATTRIBUTE modifier:
    field 972 = the modifier's WIRE stat id  (804=Str 806=Con 808=Int 810=Wis 814=Dex,
                i.e. 0x324/0x326/0x328/0x32a/0x32e from devStuff/StatIds.txt)
    field 971 = the amount (+1/+2/+3)
  Across the full item set field 972 ONLY ever holds one of those 5 attribute ids
  (0 non-attribute values over 253 items), so this slot is safe to import.

  OPEN - the SECONDARY modifier list: a separate region after the property table holds
  repeating [2][9][statId][amount][...] records that ALSO carry attribute ids (Con/Int/
  Wis turn up only here). Their amounts look too large to be raw attribute points
  (Con=30 on a Tortage bow), so the semantic is unconfirmed - needs a reference item
  tooltip to disambiguate before import. Vital/HP gear bonuses (the bar-fill case, #3)
  have NOT been cleanly located: the 0x1b/0x1f9 hits in a raw scan were coincidental
  field VALUES, not modifiers.
"""
import struct, glob, os, sys, collections

ATTR_STAT_IDS = {804: 'Str', 806: 'Con', 808: 'Int', 810: 'Wis', 814: 'Dex'}


def u32s(body):
    return [struct.unpack_from('<I', body, i)[0] for i in range(0, len(body) - 3, 4)]


def property_table(u):
    """Longest run of [fieldId][value] pairs with strictly-decreasing fieldId in (0,2000]."""
    best_len, best = 0, {}
    n = len(u)
    for start in range(0, n - 1):
        d = {}
        last = None
        j = start
        while j + 1 < n:
            fid, val = u[j], u[j + 1]
            if fid <= 0 or fid > 2000 or (last is not None and fid >= last):
                break
            d[fid] = val
            last = fid
            j += 2
        if len(d) > best_len:
            best_len, best = len(d), d
    return best


def primary_attribute(template_body):
    """The validated (statId, amount) primary-attribute modifier, or None."""
    d = property_table(u32s(template_body))
    if 972 in d and d[972] in ATTR_STAT_IDS and 971 in d:
        return ATTR_STAT_IDS[d[972]], d[971]
    return None


def main(dump_dir):
    files = sorted(glob.glob(os.path.join(dump_dir, '*.bin')))
    if not files:
        print(f'no *.bin templates in {dump_dir} (run the importer with --dump-templates first)')
        return
    by_attr = collections.Counter()
    rows = []
    for f in files:
        body = open(f, 'rb').read()[12:]  # skip the 12-byte RDB-Id header
        mod = primary_attribute(body)
        if mod:
            by_attr[mod[0]] += 1
            rows.append((os.path.basename(f), mod[0], mod[1]))
    print(f'{len(files)} templates; {len(rows)} carry a clean primary-attribute modifier (field 972/971)')
    print('by attribute:', dict(by_attr))
    for name, attr, amt in rows[:20]:
        print(f'  {name:24} +{amt} {attr}')


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else '/tmp/itemtpl')
