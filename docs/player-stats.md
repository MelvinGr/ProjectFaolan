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
| `0x001B` | HealthMax | 127000 | 1270 | 1270 / 1270 ✓ |
| `0x01FA` | ManaMax | 7400 | 74 | 74 / 74 ✓ |
| `0x01F8` | StaminaMax | 114800 | 1148 | 1148 / 1148 ✓ |
| `0x0290` | KillingBlows | 9 | 9 | Kills overall 9 ✓ |
| `0x0292` | Death | 11 | 11 | Deaths overall 11 ✓ |
| `0x0033` | PvpExp | 0 | 0 | PvP exp 0 ✓ |
| `0x0161` | PvpMaxExp | 3500 | 3500 | PvP exp `/ 3500` ✓ |
| `0x0046` | PvpLevel | 0 | 0 | PvP level 0 ✓ |
| `0x0034` | Exp | 242199 | — | cumulative total (sheet shows within-level 11959/33400) |
| `0x015E` | MaxExp | 263640 | — | cumulative total |

The screenshot was at full, so the Health/Mana/Stamina **max** ids above read the same as current. The matching
**current** ids were recovered separately from the vitals packet — see next section.

## Vitals are sent as a (current, max) PAIR — current/max ids recovered (2026-06-09)

The `0x96B8DC59` vitals packet sends a **current AND a max** record for each of Health/Stamina/Mana. Decoded
across the captures: in the 6-record updates the two values per vital satisfy `current <= max` **100% of the
time** (Health 32/32, Stamina 32/32, Mana 26/26), and during a level-up sequence both rise together — so the
larger-valued id of each pair is the max. This **overturns the old labels, which had current/max swapped**:

| vital | **current** id | **max** id |
|---|---|---|
| Health | `0x0001` | `0x001B` |
| Stamina | `0x01F9` | `0x01F8` |
| Mana | `0x01FB` | `0x01FA` |

(Stamina/Mana pairs are adjacent ids; Health's current id `0x0001` is not adjacent to its max `0x001B`.) The
most common vitals update is **max-only** (`{0x1B,0x1F8,0x1FA}`) — every entity announces its full vitals on
spawn — which is why the screenshot anchor (taken at full) and the spawn-capture test both match the max ids.
This is now encoded in `LegacyCharacterStatId` (`HealthCurrent`/`HealthMax`/…) and `EntityVitals.ToVitalsBlock`
emits both ids per vital (the old code sent the *current* value under the *max* id, so a damaged entity reported
`max == currentHP` and the bar never moved).

## Wrong / unconfirmed ids in `CapturedCharacterStats.cs` (fix later)

- ✅ **RESOLVED (2026-06-09): the old `MaxHealth`/`MaxStamina`/`MaxMana` labels were current/max swapped.** The
  values that looked "wrong" were correct all along, just mislabeled: `0x0001` = **current** Health (= 772 in
  assassinl23's vitals, the 19655/196.55 in the *snapshot* was a misaligned record); `0x01F9` = **current** Stamina
  (736, not max); `0x01FB` = **current** Mana (0). The real maxes are `0x001B`/`0x01F8`/`0x01FA` (the ids confirmed
  against the screenshot). See the (current, max) table above. Encoded correctly now in `LegacyCharacterStatId`.
- **Attributes** `Strength 0x0324 / Constitution 0x0326 / Intelligence 0x0328 / Wisdom 0x032A / Dexterity 0x032E`:
  0x0324 and 0x0328 decode to 10 (→1.0 at ×10), not the sheet's Str 20 / Int 39; the others don't appear as
  clean records at all. The sheet **Attributes** (Str 20, Int 39, Con 56, Dex 79, Wis 26, Ferocity 0) are most
  likely **computed client-side** (base + gear + buffs) and not transmitted as plain stat records — searching
  for their values (20/56/39/26/79, or ×10) finds only mis-aligned/coincidental matches.

## Stat-block framing (resolved via the combat vitals packet)

The stat records are **count-prefixed**: a 1-byte record count, then N × 8-byte `[statId][value]` records. This
is clearest in the `0x96B8DC59` entity-vitals packet (`...<entityHandle><00000000><count:1><records>`, see
`docs/archive/quests-and-combat.md`); the same record encoding is embedded in the larger `0x737A6DF9` snapshot.

## Open follow-ups
> Tracked in `TODO.md` §2 (the single tracker). The notes below are the technical context.

- ✅ ~~Locate real Max-Health / Max-Mana / Max-Stamina ids~~ — DONE (current/max pair table above).
- ✅ ~~Determine whether attributes are sent at all, or derived client-side~~ — **DONE (2026-06-15, #6):
  client-derived.** The sheet attribute values (necrol80: Str 20 / Con 56 / Int 39 / Wis 26 / Dex 79, and ×10)
  appear **nowhere** as the value following their stat id (`0x0324`/`0x0326`/`0x0328`/`0x032A`/`0x032E`) across
  any necrol80 capture — the only clean value the wire carries for those ids is a base `10` (= 1.0 at ×10). So the
  displayed attributes are computed client-side (base + gear + buffs), NOT transmitted as plain stat records.
  (Corroborating from the binary: the on-wire stat set is bounded by `g_Int32StatsOrder`, asserted
  `== generic_stats_size()` in `CoreCharacterFullUpdateIIR::ReadSubClass`; the array is built indirectly via
  vector growth, so enumerating its full id list needs the registration-call route, not a direct-write xref.)
- Combat (HP changes mid-fight) should reuse this block — see the combat work in [[archive_cpp_reference]] and
  `docs/capture-events.md`.
