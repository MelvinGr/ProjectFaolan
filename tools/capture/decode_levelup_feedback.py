#!/usr/bin/env python3
"""Decode the level-up / attribute-reward on-screen feedback (#209).

When a character gains a level (or a quest awards attribute points) retail does NOT send a "You have
reached level N" FIFO banner - no such token exists in ANY of the 40 captures. The visible feedback is a
burst of `FormatFeedbackIIR` (0x8D5A8B6B) messages carrying the `AttributeChanged`/`Increased` token
family - one packet PER attribute that went up - followed by a `0x96B8DC59` vitals push with the new
Health/Stamina/Mana maxima. (The level number itself is never pushed as a standalone 0x0036 stat; the
client tracks it from the snapshot / quest stream.)

Grammar of one AttributeChanged packet (FormatFeedbackCodec wrapper + this XML payload):

  typeFlags = 0x40000006   footer = 0      (constant across every AttributeChanged message)
  XML (entity-escaped; &quot; = "  &lt;/&gt; = </>  &#36; = $  &#39; = '):
    <remoteformat token="AttributeChanged" category="100" key="{OUTERKEY}" knubot="0"  >
      <parameter value="7;<remoteformat id="{ATTRID}" category="615" key="{ATTRKEY}" knubot="0"  ></remoteformat>" />
      <parameter value="7;<remoteformat token="Increased" category="100" key="{INCKEY}" knubot="0"  ></remoteformat>" />
      <parameter value="1;{AMOUNT}" />
    </remoteformat>

  {ATTRID}  = category-615 localized id of the primary attribute (804/806/808/810/814 = the 5 primaries;
              stable across every capture).
  {AMOUNT}  = how much it rose this level.
  the three key="..." are random per-burst nonces the client does not validate (reusing a captured key
  works in-client, same as the GenericDisplayAreaName banner) - the outer key and the Increased key are
  even constant within one burst; only the per-attribute key differs.

Run: python3 tools/capture/decode_levelup_feedback.py
"""
import json, glob, re, struct
import sys as _sys, pathlib as _pl
_sys.path.insert(0, str(_pl.Path(__file__).resolve().parent))
from captures import capture_files, capture  # corpus lives in the ProjectFaolan-Data repo

ATTR_IDS = {804, 806, 808, 810, 814}  # the 5 primary-attribute category-615 string ids
TOKEN = re.compile(rb'token="AttributeChanged"')
ID_RE = re.compile(r'id=&quot;(\d+)&quot; category=&quot;615&quot;')
AMT_RE = re.compile(r'value="1;(\d+)" /></remoteformat>')


def messages(packets):
    for p in packets:
        raw = bytes.fromhex(p["BodyHex"])
        if raw[4:8].hex() != "8d5a8b6b" or not TOKEN.search(raw):
            continue
        ln = struct.unpack_from(">I", raw, 0)[0]
        typeflags = struct.unpack_from(">I", raw, 25)[0]
        xmllen = struct.unpack_from(">H", raw, 29)[0]
        xml = raw[31:31 + xmllen]
        footer = struct.unpack_from(">I", raw, 31 + xmllen)[0]
        # grammar must consume the whole packet (4-byte length prefix + len)
        assert len(raw) == ln + 4, f"frame {p['Frame']}: trailing bytes"
        assert 31 + xmllen + 4 == len(raw), f"frame {p['Frame']}: xml/footer misframe"
        txt = xml.decode("latin1")
        aid = ID_RE.search(txt)
        amt = AMT_RE.search(txt)
        yield p["Frame"], typeflags, footer, int(aid.group(1)) if aid else None, int(amt.group(1)) if amt else None


def main():
    total = 0
    bursts = 0
    seen_ids = set()
    for f in capture_files("*.tshark.json"):
        try:
            packets = json.load(open(f))["Packets"]
        except Exception:
            continue
        rows = list(messages(packets))
        if not rows:
            continue
        bursts += 1
        print(f.split("/")[-1])
        for frame, tf, footer, aid, amt in rows:
            total += 1
            seen_ids.add(aid)
            assert tf == 0x40000006, f"unexpected typeFlags {tf:#x}"
            assert footer == 0, f"unexpected footer {footer}"
            assert aid in ATTR_IDS, f"unexpected attribute id {aid}"
            print(f"   frame {frame}: attr {aid} += {amt}")
    print()
    print(f"{total} AttributeChanged messages across {bursts} captures; "
          f"typeFlags=0x40000006, footer=0 invariant; attribute ids = {sorted(seen_ids)}")
    assert seen_ids == ATTR_IDS, "attribute id set drifted from the 5 primaries"
    print("OK: grammar parses to packet end for every AttributeChanged message.")


if __name__ == "__main__":
    main()
