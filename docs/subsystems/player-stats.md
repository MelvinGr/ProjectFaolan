# Player stats — where they live in the packets

Cross-referencing the character-sheet **screenshots** (captured under `Captures/screenshots/demol80/`, since
removed from the repo in the tshark-JSON migration) with the capture packets. Reference character:
**AssassinL23, Stygian Assassin, level 23** (screenshot 2026-06-07 17:17), decoded against
`AssassinL23_rogue-assassin_login_randomActions-changemap.tshark.json`. Generated 2026-06-08.

## Where the data is

The character stats are inside the big **`0x737A6DF9`** inventory/character-state snapshot (the same packet
family that carries inventory; see [[inventory_db_items_work]]). In assassinl23's snapshot (79431 bytes) the stat
block sits near the end, roughly offset **78000–78800**.

## Encoding

A run of fixed **8-byte records: `[uint32 statId BE][uint32 value BE]`** — the `(statId << 32) | value`
tagged-uint64 encoding now documented on the `CharacterStatId` enum
(`Faolan.Core/Enums/CharacterStatId.cs`) and emitted/parsed by the `CharacterStatBlock` codec
(`Faolan.Core.Network/Codecs/FullCharSnapshot/CharacterStatBlock.cs`). (An earlier decode pass also floated a
`PackedStatId24AndUInt32Value` / "packed-7" 7-byte variant; it was a false positive — just a t64 record minus
its leading zero byte, so every t64 hit produced a spurious p7 hit. The shipped codec uses this **single
8-byte encoding**.)

Scale (raw → displayed): vitals (Health/Mana/Stamina) are **×100**; everything else seen is ×1.

## Confirmed stat ids (decoded value matches the screenshot exactly)

| statId | name | raw | displayed | screenshot |
|--------|------|-----|-----------|------------|
| `0x0036` | Level | 23 | 23 | 23 ✓ |
| `0x001B` | HealthCurrent | 127000 | 1270 | 1270 / 1270 ✓ |
| `0x01FA` | ManaCurrent | 7400 | 74 | 74 / 74 ✓ |
| `0x01F8` | StaminaCurrent | 114800 | 1148 | 1148 / 1148 ✓ |
| `0x0290` | KillingBlows | 9 | 9 | Kills overall 9 ✓ |
| `0x0292` | Death | 11 | 11 | Deaths overall 11 ✓ |
| `0x0033` | PvpExp | 0 | 0 | PvP exp 0 ✓ |
| `0x0161` | PvpMaxExp | 3500 | 3500 | PvP exp `/ 3500` ✓ |
| `0x0046` | PvpLevel | 0 | 0 | PvP level 0 ✓ |
| `0x0034` | Exp | 242199 | — | cumulative total (sheet shows within-level 11959/33400) |
| `0x015E` | MaxExp | 263640 | — | cumulative total |

The screenshot was at full, so the Health/Mana/Stamina **current** ids above read the same as max. The matching
**max** ids were recovered separately from the vitals packet — see next section.

## Vitals are sent as a (current, max) PAIR — current/max ids proven in-client (2026-06-14)

The `0x96B8DC59` vitals packet sends a **current AND a max** record for each of Health/Stamina/Mana. The
current/max id assignment is **proven in-client** (2026-06-14, live `.setstat` probes on Darktemplarchar,
recorded on `CharacterStatId`): setting `0x0001` moved the player's MAX health bar, `0x001B` moved CURRENT;
`0x01F8` moved stamina CURRENT, `0x01FA` moved mana CURRENT:

| vital | **current** id | **max** id |
|---|---|---|
| Health | `0x001B` | `0x0001` |
| Stamina | `0x01F8` | `0x01F9` |
| Mana | `0x01FA` | `0x01FB` |

(Stamina/Mana pairs are adjacent ids; Health's current id `0x001B` is not adjacent to its max `0x0001`.) An
earlier 2026-06-09 pass had guessed the *opposite* mapping from a "the larger-valued id of each pair is the max"
heuristic across the captures; that heuristic was **backwards** — a full-health capture has current == max, so
the common announce packet that uses `{0x1B,0x1F8,0x1FA}` was carrying the CURRENT ids all along. That
**current-only** update (`{0x1B,0x1F8,0x1FA}`) — every entity announces its full vitals on spawn — is why the
screenshot anchor (taken at full) and the spawn-capture test both match those ids. This is now encoded in
`CharacterStatId` (`HealthCurrent`/`HealthMax`/…) and `EntityVitals.ToVitalsBlock` emits both ids per vital (the
old code sent the delta under the *max* id, so a heal/damage grew the player's MAX bar instead of moving
current). See `CharacterStatBlockTests`.

## Wrong / unconfirmed ids (now tracked on the `CharacterStatId` enum)

- ✅ **RESOLVED (2026-06-14, in-client): the vitals current/max ids are `0x001B`/`0x01F8`/`0x01FA` =
  **current** and `0x0001`/`0x01F9`/`0x01FB` = **max** (Health/Stamina/Mana).** A 2026-06-09 pass had guessed
  these swapped (see the section above); the live `.setstat` probe settled it — `0x0001` moved the MAX health
  bar, `0x001B` moved current. See the (current, max) table above. Encoded correctly now in `CharacterStatId`
  (`Faolan.Core/Enums/CharacterStatId.cs`).
- **Attributes** `Strength 0x0324 / Constitution 0x0326 / Intelligence 0x0328 / Wisdom 0x032A / Dexterity 0x032E`:
  0x0324 and 0x0328 decode to 10 (→1.0 at ×10), not the sheet's Str 20 / Int 39; the others don't appear as
  clean records at all. The sheet **Attributes** (Str 20, Int 39, Con 56, Dex 79, Wis 26, Ferocity 0) are most
  likely **computed client-side** (base + gear + buffs) and not transmitted as plain stat records — searching
  for their values (20/56/39/26/79, or ×10) finds only mis-aligned/coincidental matches.

## Stat-block framing (resolved via the combat vitals packet)

The stat records are **count-prefixed**: a 1-byte record count, then N × 8-byte `[statId][value]` records. This
is clearest in the `0x96B8DC59` entity-vitals packet (`...<entityHandle><00000000><count:1><records>`, see
`docs/re/quests-and-combat.md`); the same record encoding is embedded in the larger `0x737A6DF9` snapshot.

## Open follow-ups
> Tracked as Gitea issues (see the `area:combat` / `area:aoc-systems` labels). The notes below are the technical context.

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
  `docs/tooling/capture-events.md`.
