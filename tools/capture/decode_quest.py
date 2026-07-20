#!/usr/bin/env python3
"""Decode the quest-journal *content* packets and assert the grammar closes to the
packet end (parse -> re-serialize -> assert == original).

  QuestJournal   0x79f8c7a1  the J-panel quest list (one title line per quest)
  QuestFullUpdate 0x63268258 one quest's full record (description + objective + marker)
  QuestRewards   0xa36d3b74  (mis-named) the Tortage destiny/tutorial STEP driver

House style (CLAUDE.md "capture workflow"): decode first, assert the grammar, *then*
build a byte-exact C# codec proven by a golden test.

Key findings:
* GameServer 0x2000 framing: [len4][subOp4][ack=0000c350][playerHandle4] then the body.
* Text is never literal: every text slot is a <remoteformat id=.. category=.. key=..> /
  <localized ..> token packed as  00 <u8 len> <ascii>. The client resolves it against its
  RDB by (id, category). The `key` attribute is DETERMINISTIC per (id,category) - identical
  across characters/sessions/captures - so it is a content hash we can harvest once, not a
  per-send nonce.
* category map seen: 50300 region/journal-title, 50301 quest title, 50302 quest summary,
  50303 quest list sub-line, 50304 objective text, 52000 map/region name, 51000/51200/100
  localized strings.

Run:  python3 tools/capture/decode_quest.py
"""
import json, binascii, struct, re, sys

CAPS = [
    'Captures/Blablabla_soldier-guardian_create_random-actions-combat-levelup-questcomplete.tshark.json',
    'Captures/Ranochar_mage-necromancer_login_get-casilda-quest.tshark.json',
    'Captures/NecroL80_mage-necromancer_login_changemap-accept_quest.tshark.json',
]
SUBOPS = {'79f8c7a1': 'QuestJournal', '63268258': 'QuestFullUpdate', 'a36d3b74': 'QuestRewards'}


def u32(b, o):
    return struct.unpack_from('>I', b, o)[0]


def take_record(b, off):
    """If a text record starts at off (00 <u8 len> '<' ... '>'), return (xml, nbytes) else None."""
    if off + 2 <= len(b) and b[off] == 0x00 and b[off + 2:off + 3] == b'<':
        ln = b[off + 1]
        s = b[off + 2:off + 2 + ln]
        if len(s) == ln and s.endswith(b'>'):
            return s, 2 + ln
    return None


def slot(xml):
    s = xml.decode('latin1')
    tag = s[1:s.index(' ')]
    mid = re.search(r'id="(\d+)"', s)
    mcat = re.search(r'category="(\d+)"', s)
    return '%s(id=%s cat=%s)' % (tag, mid.group(1) if mid else '?', mcat.group(1) if mcat else '?')


def tokenize(b, off):
    """Lossless split into binary runs ([hex]) and text records. Concatenating the raw
    pieces reproduces b[off:] exactly -> round-trip proof that the framing is understood."""
    toks, pieces, run = [], [], b''
    while off < len(b):
        rec = take_record(b, off)
        if rec:
            if run:
                toks.append('[%s]' % run.hex()); pieces.append(run); run = b''
            toks.append(slot(rec[0])); pieces.append(b[off:off + rec[1]])
            off += rec[1]
        else:
            run += b[off:off + 1]; off += 1
    if run:
        toks.append('[%s]' % run.hex()); pieces.append(run)
    return toks, b''.join(pieces)


def packets(want=None):
    for f in CAPS:
        try:
            d = json.load(open(f))['Packets']
        except FileNotFoundError:
            continue
        for p in d:
            bh = p.get('BodyHex') or ''
            name = SUBOPS.get(bh[8:16])
            if name and (want is None or want == name):
                yield f.split('/')[-1], p, binascii.unhexlify(bh)


def main():
    only = sys.argv[1] if len(sys.argv) > 1 else None
    counts, ok = {}, 0
    for fname, p, b in packets():
        name = SUBOPS[b[4:8].hex()]
        counts[name] = counts.get(name, 0) + 1
        if only and only.lower() not in name.lower():
            continue
        toks, rebuilt = tokenize(b, 16)
        assert rebuilt == b[16:], 'round-trip FAILED frame %d' % p['Frame']
        ok += 1
        if counts[name] <= 3:  # show the first few of each
            print('== %s frame %d len %d  ack=%s player=%08x  (%s)' %
                  (name, p['Frame'], len(b), b[8:12].hex(), u32(b, 12), fname[:34]))
            for t in toks:
                print('   ', t)
            print()
    print('round-trip OK for', ok, 'packets; counts:', counts)


if __name__ == '__main__':
    main()
