#!/usr/bin/env python3
"""Decode every DispatchShopData (DL 0x2033) vendor-window packet in a capture JSON.

Wire grammar (decoded 2026-06-12, validated on all 11 captured vendor windows):
  [shop ID32 (type 0xC75B)][vendor NPC ID32 (type 0xC350)][u32 contentLength][ProtoInventoryGame]

The content is a plain ProtoInventoryGame (the SAME message the FullChar 0x737A6DF9 snapshot uses for the
player's bag, docs/aoc-client-protos.proto):
  f1 inventory_id        = ProtoID32{type=111, instance=0}   (111 = the shop container, bag is 104)
  f2 max_size            = shop capacity (observed 10/20/40/80/160)
  f3 disabled_slots_count= 0
  f4 items[]             = InventoryListEntry { pos, item }
    item = ProtoInventoryGameEntry {
      item_id = 0                                  (shop wares have no instance GUID)
      stat_list = [(0, 0x200000), (26, count), (356, 0x394E0034)]
                                                   (the bag uses (0,1),(23,recordId),(26,count) - stat 356
                                                    replaces the record id for shop slots; it is a CONSTANT
                                                    0x394E0034 across all wares, NOT a price - the item value is
                                                    RDB template field 74, sell=round(value/10), see #137)
      item_data = ProtoAcgItem { key1=key2=key3 = the Data1..4 identity quad (the item's TEA key!),
                                 template1=template2 = the RDB item template id,
                                 level (1 or 40 observed), hash = the 4-char archetype code }
    }

So every vendor window carries the FULL identity of every ware - quad + template + archetype - i.e. exactly
what an `Items` row needs (ArchetypeHarvester's third source) and what SnapshotItemBuilder needs to rebuild
the window from the DB. Shop ids are session-volatile; the stable shop key is the vendor NPC NAME (resolved
from the same capture's FA015807 spawns, like conversations).
"""
import json, struct, sys, sqlite3, os
from collections import OrderedDict

SHOP_TYPE = 0x0000C75B
CHAR_TYPE = 0x0000C350


def read_varint(b, i):
    shift = 0; val = 0
    while True:
        c = b[i]; i += 1
        val |= (c & 0x7f) << shift
        if not (c & 0x80): break
        shift += 7
    return val, i


def walk(b):
    i = 0; out = []
    while i < len(b):
        tag, i = read_varint(b, i)
        field, wt = tag >> 3, tag & 7
        if wt == 0:
            val, i = read_varint(b, i); out.append((field, 'v', val))
        elif wt == 2:
            ln, i = read_varint(b, i); out.append((field, 'm', b[i:i + ln])); i += ln
        elif wt == 5:
            out.append((field, 'f32', struct.unpack('<I', b[i:i + 4])[0])); i += 4
        elif wt == 1:
            out.append((field, 'f64', struct.unpack('<Q', b[i:i + 8])[0])); i += 8
        else:
            raise ValueError(f'wire type {wt}')
    return out


def first(fields, n):
    return next(v for f, w, v in fields if f == n)


def parse_shop(body):
    assert struct.unpack('>I', body[0:4])[0] == SHOP_TYPE
    shop_id = struct.unpack('>I', body[4:8])[0]
    npc_handle = struct.unpack('>I', body[12:16])[0]
    ln = struct.unpack('>I', body[16:20])[0]
    assert 20 + ln == len(body), f'content length {ln} vs body {len(body)}'
    root = walk(body[20:])
    inv_id = walk(first(root, 1))
    assert first(inv_id, 1) == 111, 'shop container type'
    out = dict(shopId=shop_id, npcHandle=npc_handle, maxSize=first(root, 2), items=[])
    for f, w, v in root:
        if f != 4: continue
        sf = walk(v)
        pos = first(sf, 1)
        ef = walk(first(sf, 2))
        stats = {walk(t)[0][2]: walk(t)[1][2] for ff, ww, t in ef if ff == 2 and ww == 'm'}
        tail = walk(first(ef, 3))
        quad = tuple(v for f2, w2, v in walk(first(tail, 1)))
        template = first(tail, 4)
        level = first(tail, 7)
        archetype = first(tail, 8)
        out['items'].append(dict(pos=pos, count=stats.get(26, 1), stat0=stats.get(0), stat356=stats.get(356),
                                 quad=quad, template=template, level=level, archetype=archetype))
    return out


def spawn_names(packets):
    """handle -> name from the capture's FA015807 spawns (cheap scan: [sub][C350][handle] ... name)."""
    names = {}
    for p in packets:
        if not p['IsDownload']: continue
        raw = bytes.fromhex(p.get('BodyHex') or '')
        idx = raw.find(bytes.fromhex('fa015807'))
        if idx < 0: continue
        handle = struct.unpack('>I', raw[idx + 8:idx + 12])[0]
        # the entity name is the first length-prefixed ASCII string in the body
        for j in range(idx + 12, min(idx + 160, len(raw) - 2)):
            n = struct.unpack('>H', raw[j:j + 2])[0]
            if 2 < n < 32 and j + 2 + n <= len(raw):
                cand = raw[j + 2:j + 2 + n]
                if all(0x20 <= c < 0x7f for c in cand) and cand[0:1].isalpha():
                    names.setdefault(handle, cand.decode())
                    break
    return names


def main(path):
    d = json.load(open(path))
    pkts = sorted(d['Packets'], key=lambda p: p['Frame'])
    names = spawn_names(pkts)
    db = None
    dbpath = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'faolan.db')
    if os.path.exists(dbpath):
        db = sqlite3.connect(dbpath)
    seen = OrderedDict()
    for p in pkts:
        if not p['IsDownload'] or (p.get('Opcode') or '').lower() != '0x2033': continue
        body = bytes.fromhex(p.get('BodyHex') or '')
        if len(body) < 48: continue
        try:
            shop = parse_shop(body)
        except Exception as e:
            print(f'!! parse failed ({e}) for {len(body)}B blob'); continue
        key = shop['shopId']
        if key in seen: continue
        seen[key] = shop
        vendor = names.get(shop['npcHandle'], f'handle {shop["npcHandle"]:#x}')
        print(f"shop {shop['shopId']:#x} vendor \"{vendor}\" maxSize={shop['maxSize']} items={len(shop['items'])} [parsed to end]")
        for it in shop['items'][:5]:
            name = ''
            if db:
                row = db.execute('SELECT Name FROM Items WHERE Id=?', (it['template'],)).fetchone()
                name = row[0] if row and row[0] else ''
            arch = struct.pack('>I', it['archetype']).decode('latin1')
            print(f"   pos {it['pos']:3} template {it['template']:8} x{it['count']} lvl {it['level']:3} arch '{arch}' {name}")
        if len(shop['items']) > 5:
            print(f'   ... +{len(shop["items"]) - 5} more')


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else
         'Captures/NecroL80_mage-necromancer_login_sell_items_to_trader-intract-with-auctionhouse.tshark.json')
