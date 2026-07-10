# Quest_c region (0x737A6DF9 FullChar snapshot) — deep decode (2026-07-05)

The Quest_c region is the quest JOURNAL at the front of the snapshot tails. `QuestcRegion.cs` models it and
round-trips the **beach create template** byte-exact, but the beach template is the *simplest* possible journal
(fresh char: full quests, empty objective lists, no progress). This doc records the FULL structure recovered
from the client reader (Ghidra) + validated against NecroL80's lvl80 login (27 quests, real progress), so the
generator can eventually emit arbitrary journals.

## Readers (Ghidra `AgeOfConan.exe`, project `Captures/ghidra/output`)

- `FUN_0091f5c0` — **List-A driver**: reads `u32 count = 0x3F1*(records+1)`, then loops `records` times, each
  iteration reading one `u32` (the record's listKey) + calling the per-record reader.
- `FUN_0091df50` — **per-record reader**. Read primitives: `FUN_0048f900`=ReadU32, `FUN_0048f840`=ReadString
  (`[u16 len][utf8]`), `FUN_0048f720`=ReadU64, `FUN_00cc0540`=Read4×U32. Post-obj4 calls are `FUN_009202d0`
  (std::sort introsort) + vector-push, NOT stream reads.

## Record grammar (validated byte-exact on NecroL80 rec0, snapshot offset 11024)

```
[listKey=questId]
u32 ver(=1)
u32 questId          (+0xc)
u32 state            (+0x10)
u32 field114         (+0x114)          # beach codec MISLABELS this as "Zero"
u32 field118         (+0x118)          # beach codec MISLABELS this as "N"
u32 n                (+0x14)  goal count; beach codec MISLABELS this "Marker" and ASSERTS ==0x412 (beach-only!)
string title, summary, area, region1, region2   # cats 50301/50302/50300/52000/52000
u32 fieldEc          (+0xec)
u32 areaId           (+0xcc)
u32 field2c          (+0x2c)           # beach codec MISLABELS this "questId2" and ASSERTS ==questId (beach-only!)
u32 field11c         (+0x11c)
u64 accept           (+0x20)
u32 field28          (+0x28)
objlist obj1, obj2                     # each: [u32 count][count × ObjRec]
u32 fieldF0          (+0xf0)
u64 fieldF8          (+0xf8)
objlist obj3, obj4
progress-sublist                       # <-- the piece the beach template never exercises
```

`ObjRec` = **16 u32** on the wire (reader scrambles them into an 18-word struct: 2 words, then `Read4×U32`,
2 words, `Read4×U32`, 2 words). Reading them as 16 raw u32 in wire order (what `QuestcRegion.cs` does) IS
byte-exact.

## progress-sublist (per record, after obj4) — the real blocker

`[u32 frame = 0x3F1*(k+1)][k × progress-entry]`. rec0 (quest 13907): `frame=0x7E2 ⇒ k=1`. Full byte map of the
one entry (offsets from NecroL80's snapshot):

```
+0  u32 flag        = 1
+4  u32 goalCount    = 12 (0xC)
+8  u32 playerHandle = 0x0802E5D4          # the character's own handle
+12 u32 questId      = 13907
+16 u32 goalId       = 6451
+20 u32              = 1
+24 u32 goalId       = 6451
+28 u32              = 1
+32 5× u32 = 0
+52 u32 id           = 70099 (0x111D3)
+56 'WTHD' (0x57544844) block: [WTHD][0][3042][171][171][0][0]   # server data, see below
+84 u32 strlen16?    = 100 ; then a remoteformat string id=6451 category=50304 (the GOAL description)
    then a second string: [u16 len=81] <localized id="8235" category="51000" flags="m" …>
    then ~7 more u32 fields (0x34A1.. flags/timestamps) before the next quest record
```

### Why this can't be fully generated from client RE

The **`WTHD` block is opaque server data**: the magic `0x57544844` is referenced NOWHERE in the client
(`FindConst` = 0 hits in either byte order), so there is no client reader to decompile for its internal
field semantics — it is stored/echoed as a blob. This is the same class of blocker as the server-held RDB
keys: the client never *interprets* these bytes, so their meaning isn't recoverable from `AgeOfConan.exe`.
Records are also VARIABLE (rec1 quest 6451 has empty header strings + is itself a goal-style entry), so a
faithful arbitrary-journal generator needs either a retail server-side spec or a large corpus of progressed
captures to infer the WTHD/progress fields empirically.

## Status / what to change in `QuestcRegion.cs`

- **Safe now** (preserves the beach round-trip): rename the mislabeled fields (`Zero→Field114`, `N→Field118`,
  `Marker→N`, `QuestId2→Field2c`) and DROP the two beach-only asserts (`!=0x412`, `questId2!=questId`) — the
  read/write order is unchanged so the golden stays byte-exact, and the field semantics become correct.
- **Blocked** (needs the above corpus/spec): reading/generating the progress-sublist for progressed chars.
  Production is unaffected — fresh chars (the only journals we generate) have `k=0` empty progress lists.

Fresh-char / beach journal generation already works. This decode de-risks the eventual progressed-journal work
and corrects the field model; the opaque WTHD block is the hard limit of client-only RE here.
