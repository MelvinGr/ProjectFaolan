# Handoff 2026-06-17 — per-class snapshot identity (#239 class label / #4 stats / #186 retire BeachTailsTemplate.bin)

## The problem (one root cause, several symptoms)
The Tortage beach world-entry snapshot (`0x737A6DF9`, built in `RetailLoginReplay.BuildBeachInventoryPacket`)
is the **captured Strie packet** — and Strie is an **Assassin (Rogue)**. The login path patches a few things
(level, current HP/Mana/Stamina, XP, quest region, DB containers) but **never regenerates the character's CLASS
or class-derived stats**, so every fresh character inherits Strie's Rogue identity. In-client this shows as:

- **#239** — the character sheet labels every class **Barbarian**, and the client applies **Rogue weapon
  proficiency** (a Necromancer can equip a club but not a dagger/staff/talisman). Confirmed: a club works, a
  dagger doesn't — exactly a Barbarian's proficiency.
- **#4** — mana/stat display is wrong (Necromancer shows ~0 mana, regen confused).
- **#240** (face tattoo), **#220** (caster starter weapon should be a dagger/staff, not a Broken Bottle) are
  adjacent create-flow data issues; **#186** (retire `BeachTailsTemplate.bin`) is the same "construct the
  snapshot per-character instead of shipping Strie's" work.

## Ground truth (retail, read off the client 2026-06-17)
Fresh Necromancer on the beach: **HP 89, Mana 246, Stamina 0**.

## Captures committed (0a0df6e) — the decode data
- `Captures/account-stricken997-character-necro-create.tshark.json` — Necro create → beach world-entry. TWO
  `0x737A6DF9` snapshots: create-editor (pkt#60, 8082 B) and **world-entry (pkt#329, 8497 B)** — use the
  world-entry one.
- `Captures/account-stricken997-character-necro-login-magic-combat-ice-strike.tshark.json` — a Necro casting.
- Baseline to diff against: `account-stricken997-character-Strie-create` (Strie = Assassin/Rogue, **same create
  flow**) — a clean Rogue-vs-Mage diff with appearance/name/instance as the only other variables.

## What's PROVEN
- The `0x737A6DF9` body is `[u32 len][737a6df9][c350][instance][front protobuf root ~4..498][PFAccessWindow_
  table ~498..7066 (game-static, identical for everyone)][tails]`.
- **Tails STAT BLOCK** is at the END of the tails — signature `00 00 03 F1  00 00 03 F1  00 00 46 F2`, then
  338×u32 + a 1-byte trailer; indices **35..182 are copied to 183..330** (a +148 repeat). `SetValue` writes both
  copies.
- **HP IS in the stat block:** retail Necro HP 89 → slots **[23] and [54]** (×100 = 8900, repeat at [202]). The
  existing `TailsPostAccessBlock.CurrentHp` setter already targets these.
- **Mana is NOT in the stat block** — 246 / 24600 appear nowhere. The client **computes displayed mana from
  ATTRIBUTES**, a separate region not yet located.
- `Captures/decode_statblock.py`'s `[64]=mana` and `[78]=archetype` labels are **WRONG/unreliable**: the retail
  Necro (a Mage) has `[78]=3` (not the claimed "Mage=2") and `[64]=2276` (not 24600). Re-decode against the
  retail values, don't trust that map.
- The **CLASS label is not a stat slot** — it's in the **front protobuf root** (`ProtoFullCharacterIIR`, bytes
  ~4..498) or its sub-messages. It is not a plain ASCII string, so it needs protobuf **parsing**, not a byte-scan.

## What FAILED (reverted — don't repeat)
Patching the tails stat-block archetype/vitals per-class in `BuildBeachInventoryPacket`. A diagnostic confirmed
the patch path runs (`HasKnownRepeatedStatTable=True`, class read correctly), and `WritePostAccessBlock` writes
`block.Values` — yet in-client the sheet was unchanged AND **equipped gear vanished** (the client rejected the
inconsistent snapshot). So editing the tails stat block in isolation is a dead end; the class/mana sources are
elsewhere.

## NEXT STEPS (fresh session)
1. **Find the class field.** Parse the front protobuf root of the Necro vs Strie world-entry snapshots
   (`ProtoFullCharacterIIR`), diff the fields, isolate the one that differs by class (Mage vs Rogue). Map it to
   the `Class`/`ArchType` enum. (Strie=Assassin/Rogue is the baseline; a Soldier + Priest create dump would also
   give the remaining values.)
2. **Find the attribute region** that yields the displayed mana (246) / the class HP/mana/stamina maxima, so #4's
   numbers can be generated.
3. **Generate both per-character** in the snapshot build — this is also the substance of retiring
   `BeachTailsTemplate.bin` (#186): `BeachTailsBuilder.BuildFromScratch` already reproduces the tails byte-exact
   but isn't wired into the login path, and even wired it would still ship Strie's class/stats until (1)+(2) land.
4. **Verify in-client** (the only real proof): a fresh Necromancer must show class=Necromancer, mana ≈ 246, and
   equip a dagger/staff. Watch for the "gear vanished" failure mode = an inconsistent snapshot.

## Pointers
- Build/run: `dotnet Faolan/bin/Debug/Faolan.dll` from the **repo root** (not `bin/Debug`).
- Snapshot code: `Faolan/GameServer/Packets/FullCharSnapshot/` (`SnapshotTails.cs`, `TailsStatBlock.cs`,
  `BeachTailsBuilder.cs`, `InventorySnapshotInspector.cs`); the login assembly is
  `RetailLoginReplay.BuildBeachInventoryPacket`.
- Agent memories: `necro_snapshot_decode_state`, `create_flow_state`, `fullchar_generation`,
  `snapshot_tails_construction_scope`.
