# The character capability bitfield in `CoreCharacterFullUpdateIIR::Activate` (Gitea #648)

Raw RE artifact. What the client does with the per-character flags word, and how far that gets us naming
`ProtoCoreCharacterIIR.CharacterStats.flags`.

## What is established

`CharacterStats.flags` is a real bitfield, and **bit 28 is the "this is an NPC" bit** — measured over the whole
corpus slice, 17,400 NPC records all carry it and all 317 player records do not, while bit 0 is set on both.
That much is gated by `NpcStatCoreColumnsTests.CharacterStatsFlags_Bit28_IsTheNpcBit`.

Two further bits appear only on players and only transiently: **3 (`0x8`)** and **5 (`0x20`)**. Seven characters
each appear with two different values, so they are STATE rather than identity, and the two never co-occur.

## The client functions

Found from the assert strings, which carry the source path (`StrXref REGEX=CoreCharacterFullUpdateIIR`):

| function | is |
|---|---|
| `FUN_0145ac80` | `CoreCharacterFullUpdateIIR::ReadSubClass` — the wire reader; called at the tail of the FA015807 `SimpleCharFullUpdateIIR` reader `FUN_01121c00` |
| `FUN_01458d40` | `CoreCharacterFullUpdateIIR::Activate` — the APPLY, ~1,360 decompiled lines |
| `FUN_01458700` / `FUN_01458c00` | ctor / `Create` |

`Activate` is an apply function in the sense of the technique that decoded the spawn scaffold: it pushes each
slot into the character under an explicit stat id, through the `+0xc` virtual setter, so **the stat id names the
slot**. It makes 47 such calls.

## The bitfield

Three of them take a BIT of the word at `this + 0x40` rather than a whole word:

| bit | shift in the decompile | stat id | client name (LDB 615) |
|---|---|---|---|
| 1 | `>> 1 & 1` | `0xC2` (194) | **In Play** |
| **3** | `>> 3 & 1` | `0xDF` (223) | **Can Change Clothes** |
| 27 | `>> 0x1b & 1` | `0x300` (768) | **Has Knubot Data** |

Bit 22 (`>> 0x16 & 1`) gates a separate call — `(**(code **)(piVar17[0x5c] + 4))(0x400)` — rather than pushing a
stat, so it is a mode switch of some kind and is not named here.

The three names are coherent as a set: they are per-character *capabilities*, which is what a flags word on a
spawn record should carry.

## Why this is a LEAD and not yet the answer

⚠️ **The correspondence between `this + 0x40` and wire field 16 is not proven.** Field 16 was never traced through
the protobuf parse to that offset; `+0x40` is an IIR member, and `CharacterStats.flags` is the only flags word in
the whole message, which is suggestive and no more.

One fact argues against a naive reading: **no captured NPC sets bit 27**, yet dialogue NPCs plainly have knubot
data. Either the bit numbering does not line up (our NPC bit is 28, one place above the knubot bit the client
reads at 27), or `+0x40` is a locally-composed word rather than the wire one, or that push is reached only on a
path our captures do not exercise (it sits behind a virtual predicate, `(**(code **)(*piVar17 + 0x60))()`).

Note also that the two bits every capture DOES set — 0 and 28 — are not read anywhere in `Activate`.

**"Can Change Clothes" for bit 3 fits the observations** (transient, players only, never an NPC — an NPC cannot
change clothes) but rests on the unproven offset correspondence. **Bit 5 is not read in this apply at all.**

## Next steps

1. **Prove or break the offset.** Trace `CharacterStats` field 16 through the protobuf parse to a member offset,
   then check it against `+0x40`. That single step decides everything above.
2. **In-client value sweep** for bit 5 — the method that named wire fields before: watch the word on a second
   character while toggling candidate states (combat, sitting, PvP flag, AFK, stealth). Bit 5 has no reader in
   `Activate`, so a static hunt for it needs the consumer found first.
3. The other 44 `SetStat` calls in `Activate` name whole-word slots off a stats holder object; that table is
   worth transcribing once the holder is identified, but it is deliberately NOT reproduced here — a
   hand-transcribed Ghidra table needs two independent corroborations before it is worth trusting.

Open work tracked in Gitea #648.
