# Player stats — where they live in the packets

Cross-referencing the character-sheet **screenshots** (captured under `Captures/screenshots/demol80/`, since
removed from the repo in the tshark-JSON migration) with the capture packets. Reference character:
**AssassinL23, Stygian Assassin, level 23** (screenshot 2026-06-07 17:17), decoded against
`AssassinL23_rogue-assassin_login_randomActions-changemap.tshark.json`. Generated 2026-06-08.

## Where the data is

The character stats are inside the big **`0x737A6DF9`** inventory/character-state snapshot (the same packet
family that carries inventory; see [[inventory_db_items_work]]). In AssassinL23's snapshot (79431 bytes) the stat
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

## Regen moves the SAME stats, through the same client setter (`RegenIIR` 0x28D0AC99)

Retail does not push a full `0x96B8DC59` block per regen tick — it sends `RegenIIR`, a 12-byte
`[+health][+mana][+stamina]` signed delta in the same ×100 scale, which the client ADDS to the three **current**
ids above. Both applies end in the same virtual call, so a client that renders one renders the other:

| IIR | apply | what it does |
|---|---|---|
| `StatUpdateIIR` `0x96B8DC59` | `FUN_007a00e0` | per record: `(entity+0x10c)->vt[0xc](statId, value)` |
| `RegenIIR` `0x28D0AC99` | `FUN_0079dd10` | per pool: `v = vt[8](statId)`, then `vt[0xc](statId, v + delta)` for `0x1B`, `0x1FA`, `0x1F8` in field order |

Both resolve the target from the carrier's `[0xC350][handle]` and bail to a log channel (`GameCode/Regen`,
`GameCode/Stats`) when the handle names no character. All 1502 captured `RegenIIR`s are 29-byte bodies with
`env == 0`, so there is no conditional field [V3: V1IirCodecTests.Regen_RoundTripsCapturedBody].

Retail pins the endpoint with an authoritative block on the tick a pool **tops off** — in
`Hoxxling_mage-heraldofxotli_login_gameplay` at 18:49:49.912 a `RegenIIR(2060, 1992, 882)` ships in the same frame
as a stat block setting stamina `0x1F8` = 14000 and mana `0x1FA` = 28800 (their maxes). `WorldSimulation`'s regen
tick sends the delta and then the block on **every** tick (delta first, so the absolute value lands last and the
client can never double-apply); trimming that to retail's top-off-only shape needs the delta channel confirmed
in-client — Gitea #2.

## The regen RATES — attribute-driven, and confirmed against the captured deltas

A `RegenIIR` delta is the per-second rate ×100, so the corpus states retail's rate for a known character to two
decimals. That is what pins these formulas; they use the coefficients out of the client's own tweak table (RDB
1000516, [`docs/rdb/client-tweak-table.md`](../rdb/client-tweak-table.md)), in DISPLAY units, per second:

| pool | rate | attributes |
|---|---|---|
| health | `0.15 × (Con + Str) + 20` | Constitution, Strength |
| stamina | `0.15 × (Con + Str + Dex) + 10` | + Dexterity |
| mana | `0.38 × (Int + Wis)` | Intelligence, Wisdom (no flat term) |

Each against the captured delta for that character, exactly [V1: Faolan.Tests/RegenRatesTests.cs]:

| character | scores | rate | captured `RegenIIR` |
|---|---|---|---|
| NecroL80, L80 Necromancer | Con 375, Str 70 | 86.75 | hp `8675` |
| DemoL80, L80 Demonologist | Con 345, Str 70 | 82.25 | hp `20353` = `8225` + 12128 |
| AssassinL23, L23 Assassin | Con 56, Str 20, Dex 79, Int 39, Wis 26 | 31.40 / 33.25 / 24.70 | hp `3140`, stam `3325`, mana `2470` |

Three things worth keeping:

- **The cadence is 1 Hz.** Median inter-arrival across all 1502 captured `RegenIIR`s is 1.00 s (min 0.77); the longer
  gaps are pools already full, not a slower tick. So the sheet's "Non Combat Health Regen" row IS the per-second rate.
- **The flat terms come from TRUNCATION, and reading the sheet as rounded gets them wrong.** The sheet prints the stat
  with an integer divide by 100 (client `FUN_016d9cb0`, case `0x116` = stat 278 `Non Combat Health Regen`), so 86.75
  and 82.25 both display as their floor. Solving the three displayed integers as *rounded* values yields 19.5 and
  fits all of them; solving them as *truncated* yields 20 — and only 20 reproduces the captured deltas.
- **DemoL80's bare rate was never captured**, only `20353`. It is identified as `8225` because NecroL80's boosted
  `20803` exceeds her bare `8675` by exactly the same 12128 — one shared additive term (a buff or rest bonus, not
  decoded), not a per-character rate.

⚠️ **The server cannot yet compute these for a real character**, because the attribute scores are client-side (next
section): retail sends a flat 10 for every attribute, and the class/level base is in no client data table either — of
the 25 named `CoreTables` tables, `RDBPLAYERCLASSDATA` turns out to read RDB types 1000505–1000508, which are name and
id maps, and none of 1000509–1000521 has a 12-class × 80-level × attribute shape. So `RegenRates` is fed
gear + buffs only (`WorldEntity.AttributeScore`) and a geared character regenerates slower than retail. Open work
tracked in Gitea #444.

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
  client-derived.** The sheet attribute values (NecroL80: Str 20 / Con 56 / Int 39 / Wis 26 / Dex 79, and ×10)
  appear **nowhere** as the value following their stat id (`0x0324`/`0x0326`/`0x0328`/`0x032A`/`0x032E`) across
  any NecroL80 capture — the only clean value the wire carries for those ids is a base `10` (= 1.0 at ×10). So the
  displayed attributes are computed client-side (base + gear + buffs), NOT transmitted as plain stat records.
  (Corroborating from the binary: the on-wire stat set is bounded by `g_Int32StatsOrder`, asserted
  `== generic_stats_size()` in `CoreCharacterFullUpdateIIR::ReadSubClass`; the array is built indirectly via
  vector growth, so enumerating its full id list needs the registration-call route, not a direct-write xref.)
- Combat (HP changes mid-fight) should reuse this block — see the combat work in [[archive_cpp_reference]] and
  `docs/tooling/capture-events.md`.
