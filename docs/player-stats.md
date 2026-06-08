# Player stats — where they live in the packets

Cross-referencing the character-sheet **screenshots** (`Captures/screenshots/assassinl23/`) with the capture
packets. Reference character: **AssassinL23, Stygian Assassin, level 23** (screenshot 2026-06-07 17:17), decoded
against `account-faolan-character-assassinl23-login-randomActions-changemap.json`. Generated 2026-06-08.

## Where the data is

The character stats are inside the big **`0x737A6DF9`** inventory/character-state snapshot (the same packet
family that carries inventory; see [[inventory_db_items_work]]). In assassinl23's snapshot (79431 bytes) the stat
block sits near the end, roughly offset **78000–78800**.

## Encoding

A run of fixed **8-byte records: `[uint32 statId BE][uint32 value BE]`**. (This is what
`CapturedCharacterStats.cs` calls `TaggedUInt64` = `(statId << 32) | value` — identical bytes. The
`PackedStatId24AndUInt32Value` / "packed-7" variant is a false positive: it's just a t64 record minus its
leading zero byte, so every t64 hit produces a spurious p7 hit. **There is only one encoding.**)

Scale (raw → displayed): vitals (Health/Mana/Stamina) are **×100**; everything else seen is ×1.

## Confirmed stat ids (decoded value matches the screenshot exactly)

| statId | name | raw | displayed | screenshot |
|--------|------|-----|-----------|------------|
| `0x0036` | Level | 23 | 23 | 23 ✓ |
| `0x001B` | Health | 127000 | 1270 | 1270 / 1270 ✓ |
| `0x01FA` | Mana | 7400 | 74 | 74 / 74 ✓ |
| `0x01F8` | Stamina | 114800 | 1148 | 1148 / 1148 ✓ |
| `0x0290` | KillingBlows | 9 | 9 | Kills overall 9 ✓ |
| `0x0292` | Death | 11 | 11 | Deaths overall 11 ✓ |
| `0x0033` | PvpExp | 0 | 0 | PvP exp 0 ✓ |
| `0x0161` | PvpMaxExp | 3500 | 3500 | PvP exp `/ 3500` ✓ |
| `0x0046` | PvpLevel | 0 | 0 | PvP level 0 ✓ |
| `0x0034` | Exp | 242199 | — | cumulative total (sheet shows within-level 11959/33400) |
| `0x015E` | MaxExp | 263640 | — | cumulative total |

`Health`/`Mana`/`Stamina` read full (current == max) because the screenshot was taken at full; the "current"
and "max" the sheet shows are the same field when full.

## Wrong / unconfirmed ids in `CapturedCharacterStats.cs` (fix later)

- **`MaxHealth = 0x0001`** → decodes to 19655 (196.55), not 1270. `0x0001` is some other ~196 stat; real
  max-health is not isolated (Health 0x1B already carries the full value here).
- **`MaxStamina = 0x01F9`** → 73600 (736), not 1148. **`MaxMana = 0x01FB`** → 0, not 7400. Both suspect.
- **Attributes** `Strength 0x0324 / Constitution 0x0326 / Intelligence 0x0328 / Wisdom 0x032A / Dexterity 0x032E`:
  0x0324 and 0x0328 decode to 10 (→1.0 at ×10), not the sheet's Str 20 / Int 39; the others don't appear as
  clean records at all. The sheet **Attributes** (Str 20, Int 39, Con 56, Dex 79, Wis 26, Ferocity 0) are most
  likely **computed client-side** (base + gear + buffs) and not transmitted as plain stat records — searching
  for their values (20/56/39/26/79, or ×10) finds only mis-aligned/coincidental matches.

## Stat-block framing (resolved via the combat vitals packet)

The stat records are **count-prefixed**: a 1-byte record count, then N × 8-byte `[statId][value]` records. This
is clearest in the `0x96B8DC59` entity-vitals packet (`...<entityHandle><00000000><count:1><records>`, see
`docs/quests-and-combat.md`); the same record encoding is embedded in the larger `0x737A6DF9` snapshot.

## Open follow-ups
- Locate real Max-Health / Max-Mana / Max-Stamina ids (or confirm max == current-when-full convention).
- Determine whether attributes are sent at all, or derived client-side.
- Combat (HP changes mid-fight) should reuse this block — see the combat work in [[archive_cpp_reference]] and
  `docs/capture-events.md`.
