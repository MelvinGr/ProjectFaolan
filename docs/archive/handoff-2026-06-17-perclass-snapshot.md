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

## Ground truth (retail L1, read off the client 2026-06-17) — see the 4-way table in the CORRECTION below
- Necromancer (Mage): **HP 89, Mana 246, Stamina 0**
- Guardian (Soldier): **HP 130, Mana 0, Stamina 179**
- Assassin (Rogue): **HP 98, Mana 0, Stamina 236** (+ AssassinL23 L23: HP 1270, Stam 736)
- Bear Shaman (Priest-hybrid): **HP 108, Mana 171, Stamina 178**

## Captures — the decode data
Captures are named `<realname>_<archetype>-<class>_<create|login>_<desc>` (the real in-game name, from the
create flow's `TheNameIs`). Per-archetype create+login pairs (use the **login** snapshot for vitals):
- `Stickienecro_mage-necromancer_*` (Necromancer), `Fwfnwfjwk_soldier-guardian_*` (Guardian),
  `Efjwlnfj_priest-bearshaman_*` (Bear Shaman), `Strie_rogue-assassin_*` (Assassin).
- Snapshot pkt indices: necro create pkt#528 (unsettled), necro login (magic-combat) pkt#47, Strie login
  pkt#52, guardian login pkt#45, bearshaman login pkt#50. Screenshots: `~/Pictures/Screenshots/stats/`.
- Decode scripts now live in `Captures/scripts/` (run from the repo root).

## What's PROVEN
- The `0x737A6DF9` body is `[u32 len][737a6df9][c350][instance][front protobuf root ~4..498][PFAccessWindow_
  table ~498..7066 (game-static, identical for everyone)][tails]`.
- **Tails STAT BLOCK** is at the END of the tails — signature `00 00 03 F1  00 00 03 F1  00 00 46 F2`, then
  338×u32 + a 1-byte trailer; indices **35..182 are copied to 183..330** (a +148 repeat). `SetValue` writes both
  copies. (NB: a fresh Mage snapshot has a 6-word-longer block, 344, because of its quest-index region — not a
  class difference.)

### CORRECTION 2026-06-17c — verified across FOUR archetypes' retail LOGIN snapshots + character-sheet screenshots (`python3 Captures/scripts/decode_statblock.py --necro-vs-strie`). The ONLY class-derived slots are vitals; everything else is session/map/quest. **The earlier "class is in the front root", "mana is not in the stat block", and (my own interim) "use index 62 for mana" claims were ALL WRONG.**

> **Use the LOGIN snapshot, never the create one.** At create the vitals are unsettled — the Guardian's create
> world-entry snapshot showed HP 100.3 / Stam 11.45, but its login snapshot (and the sheet) show HP 130 / Stam 178.5.

**Retail L1 ground truth (sheet + login stat block, ×100 in the block):**

| Class (archetype) | HP `[23]` | Mana `[60]`=`[64]` | Stamina `[68]` |
|---|---|---|---|
| Guardian (Soldier)        | 130 | — (0) | 179 |
| Assassin/Strie (Rogue)    | 98  | — (0) | 236 |
| Necromancer (Mage)        | 89  | 246   | — (0) |
| Bear Shaman (Priest-hybrid, BOTH) | 108 | 171 | 178 |

(Also captured: **AssassinL23 = Assassin L23 → HP 1270, Stam 736** — the only multi-level point; gives Rogue growth ≈ 53 HP / 23 Stam per level.)

- **CLASS LABEL / weapon proficiency (#239) is NOT in this snapshot at all.** Proven three ways: (a) the two
  create-editor snapshots (Necro vs Assassin, both 8082 B, byte-aligned) differ in only **24 bytes — all instance
  ids**; (b) all four archetypes' stat blocks differ **only in vitals magnitudes** (+ session + a Soldier-only
  slot) — no class enum; (c) the front protobuf root is **`ProtoFullCharacterIIR`** = inventory + account flags
  only, no class field. ⇒ #239's class source is a **separate packet** (CoreCharacterIIR / self-spawn /
  stat-update), **still to be located** — that is the real #239 hunt, NOT the FullChar snapshot.
- **Displayed-vitals slot map** (each ×100, + a +148 repeat copy that `SetValue` keeps in sync):
  - HP = `[23]` (=`[54]`, rep `[202]`); a lower secondary HP at `[21]` (=`[56]`, rep `[204]`).
  - **MANA = `[60]` = `[64]`** (rep `[208]`/`[212]`) — the value matching the sheet; `[62]` (rep `[210]`) is a
    ~6-lower secondary, **NOT** the displayed value. So **`CurrentMana = 64` is CORRECT** (an interim change to 62,
    based on an unsettled create snapshot, was reverted).
  - STAMINA = `[68]` (rep `[216]`); a lower secondary at `[66]` (rep `[214]`).
  - `[78]` is **not** archetype (every class = 3). `[92]`/`[240]` is a Soldier-only value (Guardian 12060, others 1000).

## What FAILED (reverted — don't repeat)
Patching the tails stat-block archetype/vitals per-class in `BuildBeachInventoryPacket` — in-client the sheet was
unchanged AND **equipped gear vanished** (the client rejected the inconsistent snapshot). The setter indices are
now confirmed correct (mana 64, stam 68, hp 23/54), so the likely remaining cause is **inconsistency**: the setters
only touch the *displayed* slot and leave the *secondary* slots (`[21]`/`[62]`/`[66]`) at the Strie template's
Rogue values — e.g. a Necro would carry Strie's `[66]`/`[68]` stamina. The robust fix is to GENERATE the whole
per-class block (BuildFromScratch), not patch individual slots on Strie's template.

## DONE this session (2026-06-17c)
- Reverted the bad mana-index change; `CurrentMana = 64` confirmed against 4 archetypes.
- Recalibrated `CharacterMaxStats.CreateDefaults` to the retail L1 anchors above (was ~2× off — e.g. Guardian 260→130).
  Rogue per-level growth fitted to the Strie(L1)+AssassinL23(L23) pair. Classes without a captured sheet
  (Dark Templar, ranged Priests, Herald of Xotli) keep clearly-flagged ESTIMATES. `CharacterMaxStatsTests` updated.

## NEXT STEPS (fresh session)
1. **#239 class label — hunt the SEPARATE packet** (NOT the FullChar snapshot — proven). Diff the per-archetype
   LOGIN captures for the player's own CoreCharacterIIR / self-spawn / stat-update; find the class/profession field.
2. **#4 vitals — generate the per-class block** from `CharacterMaxStats` (set BOTH the displayed AND secondary
   slots so the snapshot is internally consistent) and wire `BeachTailsBuilder.BuildFromScratch` into the login
   path (also retires `BeachTailsTemplate.bin`, #186). The setter indices are correct; the gap is the secondaries.
3. **Verify in-client** (the only real proof): a fresh Necromancer shows mana ≈ 246; Guardian HP 130 / Stam 179;
   Bear Shaman 108/171/178. Watch for the "gear vanished" failure = an inconsistent snapshot.
4. **More captures wanted** to fill the estimated classes: a Soldier-hybrid (Dark Templar), a ranged Priest
   (Priest of Mitra / Tempest of Set), and a Mage-hybrid (Herald of Xotli) — login + sheet.

## Pointers
- Build/run: `dotnet Faolan/bin/Debug/Faolan.dll` from the **repo root** (not `bin/Debug`).
- Snapshot code: `Faolan/GameServer/Packets/FullCharSnapshot/` (`SnapshotTails.cs`, `TailsStatBlock.cs`,
  `BeachTailsBuilder.cs`, `InventorySnapshotInspector.cs`); the login assembly is
  `RetailLoginReplay.BuildBeachInventoryPacket`.
- Agent memories: `necro_snapshot_decode_state`, `create_flow_state`, `fullchar_generation`,
  `snapshot_tails_construction_scope`.
