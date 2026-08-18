# Feats subsystem (Gitea #251)

Feats are **a live subsystem**: the catalog, the wire↔template id map, the prerequisite graph and the
per-level point budget all come off the client's own RDB, a character's trained set round-trips through the
player spawn, train/untrain runs on the wire, and a feat's effects are applied to the live character. This
documents what a feat is (vs. what the code calls a "skill"), the wire protocol, and where each piece of the
data comes from.

## TL;DR

- **`FeatIIR` (`0x28DA68A0`) is the feat train/unlock record** — a handshake carrying a `0x3F1`-framed list
  of feat ids. Decoded; added to `Opcodes.cs`.
- **The full unlocked-feat set IS in the captures — it rides the player spawn (`FA015807`), NOT the FullChar
  snapshot.** It lives in the spawn's **pre-IIR** region (the block `parse_spawn_scaffold.py` keeps raw), as a
  `0x3F1`-framed `u32` feat-id list. Decoded 2026-07-14 by `tools/capture/decode_unlocked_feats.py`. **This
  retires the former "#1 blocker".** Every high-level captured char yields a stable set (NecroL80 necro 25, DemoL80
  demo 25, Hakotep hox 6); every fresh/create char yields the empty `0x3F1` marker.
  Read in C# by `PlayerSpawnScaffoldRows.ExtractPreFrameFeats` (at a fixed offset in the parsed frame) and written
  back from `CharacterFeat` rows by `FreshPreFrame`. `SpawnFeatListTests` gates that over the WHOLE corpus: **109 of
  the 259 captured player-scaffold spawns carry feats, 150 frame an empty list, 204 distinct feat ids**, with the
  frame length (a fixed basis + 4 bytes per feat) corroborating every count independently.
- **The spawn scaffold is NOT feats.** `CharacterSkill` / `PlayerSpawnScaffoldRows` / `decode_skill_scaffold.py` read the
  character's **ability/skill** presence list — the spawn's **post-IIR** id-list, a different region *and* a
  different id-space. It gates spell *usability* (see #298) but is unrelated to the feat tree. An earlier RE
  conflated the two; that has been corrected across code + memories.
- ⭐ **A feat's point cost is ONE PER RANK, and an AUTO-TRAINED feat is never on the wire** (#862). The client's
  own featclass table (RDB 1000043, imported as `FeatClassEntry`) marks 3,839 of its 9,916 rows auto-trained and
  hands them over itself at the level it names; all 56 captured feat ids are on the other side of that flag, and
  summing `rank + 1` over each captured feat list reproduces its level's point budget exactly (79/79/16). See the
  last section of this page.
- **The feat CATALOG now exists** (#251): `Feat` (**19,777** rows from the RDB 1000020 plaintext templates - id, name,
  description, tree, group, rank) + `FeatEffect` (**23,033** stat modifiers, 161 distinct stats). Built by
  `RdbFeatImporter` (the `rdb-feats` module), no TEA key and no capture needed.
- **Feats, spells and items are the SAME RDB record layout** - `[u32 type][u32 id][u32 1000000][...]` - so they
  share the stat-modifier region AND its parser, `ItemTemplateStatModifiers.Parse(body[12..])`. (The "TB magic" that
  identifies a plaintext feat record is simply the TYPE word 1000020: `0x000F4254` renders as `TB\x0f\x00`.)
  That parser anchors on the 6-word signature `4,0,1,0,2,9` inside the 10-word record rather than on a stride, which
  is what makes it right where five hand-rolled attempts were wrong (fixed stride 3.1% bad, kind-range delimiter
  24.2%, measured per-kind width table 7.2%). It scores **2.2% on MORE rows**, and it carries the effect KIND that
  the hand-rolled version dropped - 53045 flat vs 53067 percent-stored-x100.
- **Why items score 0.00% and feats 0.35%**: `RdbItemImporter` WHITELISTS (`ApplicableStatIds.Contains`), accepting
  only the 17 stats it can name; feats touch 173, so that curated list does not transfer.
  **Kind 53182 is excluded** - decoded, and it is NOT a stat modifier: its element is `(index, FOURCC)`, the "amount"
  being four ASCII characters identical across a feat family (`0x4450434E` "DPCN" on every Perception feat,
  `0x5633534D` "V3SM" on Hide In Shadows, `0x454F4854` "EOHT" on the crafting ranks). It carried 480 of the 561 bad
  rows; dropping the whole KIND rather than the rows that failed a stat lookup took the residue to **81 rows**.
  The 1,145 of its rows that landed on named stats were just as bogus - a small index is often a valid stat id.
  ⚠️ 17 stat ids remain unnamed by LDB 615, including the `1084..1094` family (values 100..900) which looks
  deliberate - identify those before anyone adds a whitelist, which would silence exactly them.
- **TWO record shapes carry the name**, and knowing only the first hid every feat the captured characters hold:
  `[u32 len][name]` and `[u16 nameLen][u16 descLen][name][description]`. The u16 shape also yields the TOOLTIP.
- **The feat/item discriminator is the client's own feat TABLE, not the Flags word.** A template that RDB 1000042
  points at is a feat by definition; Hellfire (3187268) carries Flags `0x5` rather than `0x10000005` and was
  silently excluded while the heuristic was the only gate.
- **The PREREQUISITE GRAPH is in the RDB** - in the 1000042 feat TABLE, not the 1000020 template. Columns 2/3/4 of
  each entry are up to three prerequisite wire feat ids: `FeatPrereq`, **5,501 edges over 5,489 feats**, and the
  chains resolve end to end (`Equip: One Handed Edged Weapons 2` requires `... 1`). Together with `FeatIdMap` this is
  the tree's shape. (An earlier pass concluded prereqs were not in the RDB at all - that was true of the 1000020
  TEMPLATE, which is what it diffed, and wrong about the client's data as a whole.)
- **One row per feat BASE.** A feat id is composite (`base*100 + rank`) and retail stores only the current rank -
  no captured character carries two ranks of one base. So `AddCharacterFeats` REPLACES on rank-up (keeping the
  feat's original `Sequence`, since the wire order is train order) and ignores a request at or below the rank held.
  It used to dedup on the exact id, which would have put an entry on the spawn's list that retail never sends.
- **Feats GRANT their stats.** `MaxStatAggregator.AggregateFeatBonuses` sums `FeatEffect` into the same additive
  bonus table gear feeds, applied at world entry (`GameServerListener.WorldEntry`).
  ⚠️ **No `GetScale()` division**, unlike the gear path: `FeatEffect.Value` is a DISPLAY value where
  `ItemStatModifier.Amount` is raw wire. Strength (scale 10) carries 1..9 in the feat table and HealthMax
  (scale 100) carries 100..700 - dividing those would under-grant every feat by 10x or 100x.
  **2,720 of the 6,593 mapped feats have readable effects.** The captured characters DO now resolve - all 56 of
  their feats name a real feat - but only 6 of those 56 carry stat effects.
- ⚠️ **The other 50 are NOT feats "whose record has no modifier region" — that claim was wrong (#590).** Their
  records carry a modifier record in the ordinary place, with the ordinary `4,0,1,0,2,9` signature; the parser
  finds it and then **drops it**, because `ItemTemplateStatModifiers.Parse` bounds the payload's first word to
  `< 0x1000` on the reasoning that it is a stat id. For these it is not a stat id — it is a REFERENCE to whatever
  the feat modifies, and the second word is the magnitude:

  | | |
  |---|---|
  | feat | `Decomposition`, the Necromancer's "+35% damage or healing on Flesh to Worms" |
  | ranks (wire 80000–80004) | templates 1173623 / 24 / 26 / 27 / 28 |
  | kind | 53075 |
  | payload across the five ranks | `(41000, 1000)` `(41000, 1500)` `(41000, 2000)` `(41000, 2500)` `(41000, 3500)` |

  The magnitude decodes cleanly as **percent × 100**: 10%, 15%, 20%, 25%, **35%** — and 35% is exactly what the
  in-client tooltip shows for rank 5. The reference (`41000`) is constant across ranks, which is what a target
  should be.

  **The reference is a SPELL-LINE id**, and it decodes as:

  ```
  spellLineId = ConanClass x 1000 + lineIndex        41000 = Necromancer (41), line 0 = Flesh to Worms
  ```

  It is carried by the SPELL templates themselves, in **property-table field 128** — so a feat names a whole
  GROUP of spells, and therefore all of a spell's ranks at once, which is exactly what a feat needs to do.
  ⚠️ That group is a STACKING line, not a "spell line": the client names field 128 `GUI Stacking Line`
  (Gitea #634). Nothing about the behaviour here changes — a feat boosting a stacking line boosts the same set
  of spells — only the name. Resolve one with
  `--report spell-line-ids --report-arg <lineId>`: 41000 returns the five `Flesh to Worms` ranks (144674–144677 +
  1206011) and nothing else. 3,158 of the 21,502 spell templates carry the field.

  ⚠️ **A spell template's property table ASCENDS**, where an item's strictly DESCENDS
  (`RdbItemImporter.TryReadPropertyField`). Same `[fieldId][value]` grammar, opposite direction — the item reader
  finds nothing at all on a spell.

  **Cross-validated from both ends, independently.** The feat side was derived from which feats share a reference
  (`Decomposition`, `Empowered Flesh to Worms` and `Worm Swarm` all point at 41000), the spell side from the
  templates' own field 128 — and the two agree on every case checked:

  | ref | predicted from the feat names | spell templates carrying field 128 = ref |
  |---|---|---|
  | 41000 | Flesh to Worms | Flesh to Worms ranks 1–5 |
  | 41001 | Pestilential Blast | Pestilential Blast ranks 2–6 |
  | 41005 | Ice Strike | Ice Strike ranks 2–6 |
  | 28101 | Call Lightning | Call Lightning (Tempest of Set) |
  | 24017 | Smite | Smite (Priest of Mitra) |
  | 44006 | Shockstrike | Shockstrike (Demonologist) |

  337 of the 342 kind-53075 records have a `ref / 1000` that is a real `ConanClass`, and the feats grouped under
  each class are that class's own. (The 5 exceptions all use `50000`, a class 50 that does not exist — unresolved.)

  Catalog-wide **2,789 feat templates** carry an out-of-stat-range record, across 30 kinds — every one of them
  now named and read by slot (see the kind table below). Survey with
  `--report feat-spell-modifiers [--report-arg <kind>]`; dump and diff a feat's ranks with
  `--report feat-record --report-arg [<rdbType>/]<id>[,<id>...]` (the word that MOVES across ranks is the
  magnitude, the one that stays is the target).

  **Imported and applied**, for kind 53075 only. `FeatSpellModifier` holds `(feat → spell line, percent × 100)`;
  `Spell.GuiStackingLine` holds field 128 (the client's `GUI Stacking Line`, #634); `DatabaseRepository.GetCharacterSpellBoosts` joins the two and fans the line out
  to every RANK, giving `spellId → percent × 100` in ONE query at world entry (`WorldEntity.SpellBoosts`), so the
  cast path is a dictionary lookup rather than a per-cast query. `CombatResolver.ResolveSpellDamage` applies it
  last, over the finished number including Magic Damage — the client's wording is "Increase to damage or healing on
  <spell>", not a bonus to one term.

  End to end on the captured Necromancer: NecroL80's `Decomposition` (wire 80004, rank 5) gives **+35.0% on all five
  Flesh to Worms ranks**, which is what the in-client tooltip reads; 52 of her spells are boosted in total.

  **Also imported** (#593), each into a table shaped like what it IS rather than like the record it shares:

  | table | kinds | rows | holds |
  |---|---|---:|---|
  | `FeatSpellStatModifier` | 53054 + 53062 | 1,078 | a stat scoped to one spell, or to a whole line |

  ⭐ The same two kinds appear on **spell** records too, and the spell-sourced half is `SpellSpellStatModifier`
  (#710) — one mechanism with two sources, sharing this parser and the `SpellModifierService` consumer. See
  [spell-stat-effects.md](../rdb/spell-stat-effects.md).
  | `FeatSpellGrant` | 53019 | 1,510 | a spell the feat TEACHES |

  All three resolve completely against the shipped catalogs — 1,510/1,510 grants and 485/485 line targets and
  593/593 spell targets — which is the check that makes a reference space decodable at all: resolve the ref and see
  whether it hits.

  ⚠️ **A grant is not a boost**, which is why 53019 gets its own table rather than a row in the modifier one.
  Filing it as a modifier would record a spell the feat GIVES you as a boost to one you already have.

  ⚠️ **`Value` and `Amount` are alternatives, not a pair** — exactly one is set on all 1,078 rows, so they are two
  NULLABLE columns rather than a magnitude plus a mode flag. What they MEAN comes from the client's own item-tooltip
  builder (`Misc\ItemInfoCreator.cpp`, the 53054/53062/53283 branch at `014a25a6`), which reads the three slots as
  `GetIntStat(0x2d2 / 0x2f9 / 0x4b7)` — the `GameActionStat_e` ids that key indices 0 / 39 / 175 map to through
  `DAT_0297cd48`, so the binding is the client's, not an inference from slot order:

  - **`Value` (key 39) is an ABSOLUTE delta** in the stat's own units. The tooltip renders it through the generic
    stat formatter (`FUN_0149a300`), which appends the STAT'S own unit suffix.
  - **`Amount` (key 175) is a PERCENTAGE**, in hundredths of a percent. The tooltip renders it through an inlined
    copy of the same formatter that appends a hardcoded `"%"` — the literal at `0x01fbcf18`, on all three of that
    branch's print paths. For `CastingTimeSeconds` it takes a separate path that prints `abs(amount) * 0.01` under
    a `"%.1f%%"` format, and the shipped values are a clean ladder off exactly that scale
    (-1000 / -2000 / -3300 / -5000 / -6600 / -10000 = -10%…-100%).

  The stat id is the ordinary wire stat space — the tooltip resolves it through **LDB category 615** (`0x267`, the
  literal it pushes at `014a2714`), the one stat-name table — but the ids the spell half uses name SPELL
  properties: `ManaCost`, `CastingTimeSeconds`, `SplashRadius`, `SpellCriticalChance`, `Duration`. Two of the
  fourteen were already in `CharacterStatId` from elsewhere (`SpellColumnRadius`, `AttackRange`), which is what
  says it is one space and not a parallel one; the other twelve were added from the client's names.

  ⚠️ Seven elements are REJECTED as mis-sliced, and counted rather than dropped silently: a `Value`/`Amount` slot
  holding a registered GameAction type id is the next element's opening word, read as ours. `Swift Pathing`
  (5031395..5031401) is the case — its 53054 elements tile the record at TEN words, not the twelve its key list
  predicts. Open as Gitea #595.

- **`UpgradeCombo` (53209) — 3,214 rows in `FeatComboModifier`** (#593). Its two unnamed slots are named by the
  client's own tooltip builder, which branches on each one's SIGN to pick between `UpgradeCombo_IncreaseDamage` /
  `_DecreaseDamage` (key `66`) and `UpgradeCombo_IncreaseCooldown` / `_DecreaseCooldown` (key `67`) — so 66 is the
  combo's DAMAGE delta and 67 its REUSE-TIME delta, both signed hundredths of a percent
  ([gameactionlist-grammar.md](../re/gameactionlist-grammar.md)). Every reference resolves against `Combos.Id`.
  `Dread Lord` carries `-100` cooldown across the whole `Leech Life` ladder and `Hexer` `+100` damage across
  `Blood for Aid`, matching each feat's own description. Nothing consumes the cooldown half yet — the recast path
  is Gitea #271.

- **The PROC family — 876 rows in `FeatProc`** (#593). 53212 `AddComboProc` and 53070 `AddSpellLineProc` are one
  shape with the target space swapped (a combo vs a spell line), so they share a table the way 53054/53062 and
  53075/53030 do. The two SPELL slots are named by the client's tooltip, which tests keys 88 and 236
  independently and renders `AddComboProcSelf` / `AddComboProcOther` / `AddComboProcBoth` — so 88 is the proc cast
  on the CARRIER and 236 the one cast on the TARGET. Both are real in the data: 171 of the 876 rows are
  target-only. The trigger (key 117) and the magnitude (key 237) are carried RAW — the trigger's value→condition
  mapping and the magnitude's unit are not pinned, and inventing either would be the exact mistake this catalog
  keeps punishing.

- **53030 `ChangeHitPercent` — 954 rows, folded into `FeatSpellModifier`** (#593). The per-SPELL twin of 53075:
  the same three slots with the line slot (key 1) replaced by the spell slot (key 187), the same signed percent
  ×100. 238 of the 240 distinct references resolve against `Spells.Id`, so `FeatSpellModifier` grew a nullable
  `SpellId` beside its now-nullable `SpellLineId` and the kind picks the column.

  ⚠️ This kind was written off in the code as "not a player feat at all — the `Dimach Item` crafting entries, all
  pointing at one constant ref (43464) with value 100". That was the first SAMPLE, not the census: the Dimach rows
  sort first by feat id, and behind them sit 239 other targets and 55 distinct values including a −20/−40/−60%
  ladder.

- **`ChangeInheritedWeaponAttack` (53250) — 315 rows in `FeatComboAttackModifier`** (#593). The SIBLING of
  `UpgradeCombo`, not a variant: 53209 moves a combo as a whole, this moves one of its two weapon ATTACKS, along
  axes 53209 has no slot for. It DOES have a tooltip branch (`014a1dc5`), which names every slot — key `84` picks
  `FinishingComboAttack` (0) or `OpeningComboAttack` (1) and the client refuses to render any other value; keys
  `66`/`67`/`68` are the attack's DPS, critical chance and hate adjustment, each with an Increase/Decrease string
  chosen on its sign; key `69` is a damage TYPE the client resolves through a name table rather than printing.
  Exactly one magnitude is set on all 315 records.

  ⚠️ **The SCALES are not `UpgradeCombo`'s, even though keys 66/67 are the same two `GameActionStat_e` slots.**
  53209 renders both `× 0.01`; 53250 renders key 66 and key 68 as RAW whole percents and key 67 as a crit RATING
  (`× 0.025`, 40 points to the point). Carrying the neighbour's units across would report `Fierce Opening`'s +30%
  opening damage as +0.30%. A name transfers by key; an arithmetic does not transfer at all.

  The feats corroborate the reading by their own text: `Fierce Opening` and `Dual Master` ("increases the
  **opening** damage done by all combos") carry attack 1, and the only two hate rows in the catalog are the
  Guardian's `Improved Shield Mastery`.
- **The "kind" was never a private id space — it is a GameAction TYPE ID, and the record IS a GameAction element**
  (#591, [gameactionlist-grammar.md](../re/gameactionlist-grammar.md)). Read against the client's own element
  grammar, `[kind][0][4][0][1][0][2][9][…]` is `[typeId][field][version 4][subCount 0][the four always-present
  positional values 1,0,2,9][one u32 per key-list entry]`. So:

  - the "6-word signature" is a constant prefix, not a signature;
  - **a record is `8 + len(keyList)` words wide**, not a fixed 10 — and the key list comes from the client
    ([`gameaction-keylists.tsv`](../re/gameaction-keylists.tsv), all 260 types, recovered statically by
    `tools/ghidra/DumpGameActionKeyLists.java`);
  - **a payload word is addressed by KEY, not by position.** `ItemTemplateStatModifiers.ParseElements` returns the
    whole element and `Element.Value(key)` picks a slot; `GameActionTypes` holds the table.

  Every kind the feat catalog uses now reads as named fields (`--report feat-spell-modifiers`, which prints them
  that way), and three of the old open questions dissolve because they were artifacts of the fixed-10-word read:

  | kind | name | payload | note |
  |---|---|---|---|
  | 53209 x3235 | `UpgradeCombo` | **combo, damage %, cooldown %** | `k66` is the DAMAGE delta and `k67` the REUSE-TIME one, both signed ×100 — named off the tooltip's Increase/Decrease string pairs. The old "value 0 throughout" was `k66` on a cooldown-only feat. IMPORTED (#593) |
  | 53019 x1749 | `Spell` | spell template id | a GRANT, and the **only** payload word. The old "its value repeats its own KIND" was the NEXT element's type id, read past the end of a 9-word record |
  | 53030 x962 | `ChangeHitPercent` | **spell, signed % ×100, `k55`** | the per-spell twin of 53075 — same payload, `Spell.Id` where 53075 has a line. IMPORTED (#593) |
  | 53054 x593 | `SpellStatModifier` | **spell, Stat, Value, Amount** | not "a varying value": the second word is a STAT ID. `Fiendish Hand` carries three records for one spell — stats 347/348/349 — which is why there were "several records per (feat, spell)". IMPORTED (#593) |
  | 53062 x475 | `SpellLineStatModifier` | **line, Stat, Value, Amount** | the same record with the spell slot replaced by a line slot. `Brutal Maiming` = line 20410 (Guardian `Disable`), stat 147, value 2750 — the "147 that does not read as a percent" was the stat id. IMPORTED (#593) |
  | 53212 x421 | `AddComboProc` | **self proc spell, target proc spell, combo, trigger, amount** | the "grant" reading was the pair view: the feat is named for its key-88 spell and key 236 is empty on most records. IMPORTED (#593) |
  | 53075 x342 | `ChangeLineHitPercent` | **line, Value, `k55`** | the decoded one; `k55` (1 or 2) was never read before |
  | 53250 x315 | `ChangeInheritedWeaponAttack` | **combo, attack (finishing/opening), DPS %, crit rating, hate %, damage type** | the sibling of 53209, one level down: one ATTACK of the combo rather than the combo. Its branch is at `014a1dc5`; note the scales are NOT 53209's. IMPORTED (#593) |
  | 53070 x289 | `AddSpellLineProc` | **self proc spell, target proc spell, line, trigger, amount, `k73`** | 53212's per-LINE twin; `k73` `Flags` is 0 on every shipped row. IMPORTED (#593) |
  | 53213 / 53211 | `AddProc` / `AddSpellProc` | six words | the live-Frida catalog says four; the RDB widths say six |

  ⚠️ **The magnitude is SIGNED.** `Set's Chosen` carries -2000/-4000/-6000/-8000/-10000 across its five ranks on
  Demonologist lines 44100/44101 — a clean -20%…-100% ladder, and unmistakably a rank ladder rather than a
  misread. The column is `uint` and every consumer casts to `int`, which recovers it; the DB text does not.
- **Training ENFORCES the tree.** `FeatService` rejects a train whose resulting set leaves a prerequisite unmet
  (`FeatRules.MissingPrerequisites`), alongside the existing point-budget check.
  ⚠️ A prerequisite is satisfied by the **same base at an equal-or-higher rank**, not by the exact id. The graph
  links adjacent ranks (`80004` requires `80003`) while a character's wire list carries only the HIGHEST rank per
  base - retail never stores the intermediate ones. An exact-id check would reject every captured retail character
  (NecroL80 19 unmet, DemoL80 18, Hakotep 3); the same-base rule clears all three at zero. The lower ranks are not
  missing data to backfill: rank 4 IS ranks 1-4.
- **Feat POINTS per level: RDB type 1000044 id 1**, 71 entries of `[level 10..80][points][tsPoints]`. Levels
  20/30/40/50/60/70/75/80 grant **two** points, so the level-80 budget is **79**, not the 71 that `1 per level from
  10` implies. `FeatRules.PointBudget` was wrong until it was checked against this table.
- **The wire<->catalog id map is SOLVED** (#467): the client does not derive it, it LOADS it.
  `FeatTable_t::LoadStaticData()` (`FUN_00788e20`) reads **RDB type 1000042, id 1** - a 20-byte header
  `[type][id][1][count][version]` then `count` 48-byte entries whose first two words are `(wireId, templateId)`.
  Imported as `FeatIdMap` (6,593 rows); it resolves **56 of 56** captured `CharacterFeat` ids.
- **`FeatIdMap.Rank` is the trustworthy rank** - `WireFeatId % 100`, the other half of the split `BaseIndex` tops,
  and **0-BASED** (the first rank is 0: "Corrupted Soul" is 84100..84104 = ranks 0..4). ⚠️ NOT `Feat.Rank`, which is
  parsed from the NAME: the name's trailing number is a rank sometimes ("Thrown Weapons 5") and an INDEX other times
  ("Guardian Class Feat 011" parses as 11), so the two disagree on 2394 of the 2491 feats where both have a value.
  ⚠️ `FeatIdMap.Rank` is only a RANK below **10**, and that bound is the client's: `FUN_007850c0` computes
  `(id / 100) * 100 + 10` and requires `id <` it. 5,742 of 6,593 entries fit; the other 851 are "<Class> Class Feat
  071"-style rows whose low digits index a family (24779 reads as 79). `HasRank` gates it.
- **The id encodes the class.** Each entry's 11th word is `wireId / 100`, and for the ROOT feats that value is the
  `ConanClass` enum value: wire 1800 = Barbarian (0x12), 2000 = Guardian (0x14), 2200 = Conqueror (0x16),
  2900 = BearShaman (0x1D), 3100 = DarkTemplar (0x1F). Below them sit the four ARCHETYPE roots -
  100 `Core Soldier Knowledge`, 200 `Priest`, 300 `Rogue`, 400 `Mage`.
- **Root feats are AUTO-GRANTED, never sent.** `FeatIdMap.RootFlag` (word 8 of a 1000042 entry) marks a ROOT feat - 1 for all twelve
  archetype/class roots (100 `Core Soldier Knowledge`, 1800 `Barbarian`, 4100 `Necromancer`, ...) and for the feat
  TREE roots (`Lich`, `Scion of Set`); 563 entries carry it. `ClassAutotrainLevelMap` is COMPUTED by the client
  (`FUN_0078d6a0`) from that table plus the character's class - it is not an RDB resource, which is why searching
  for one finds nothing. So the server neither grants nor transmits them, and `CharacterFeat` correctly holds only
  what was TRAINED.
- ⚠️ **No captured character carries its own class-root feat** (NecroL80/Necromancer has no 4100, Hakotep/HeraldOfXotli
  no 4300), and every id they DO carry is rank-bearing. So the roots look **auto-granted rather than trained** -
  which is what `ClassAutotrainLevelMap` (33,830 entries, via the client's `GameCode/Feats` debug channel) would
  encode. Not yet verified.
- **Every mapped feat resolves to a catalog row** — all 6,593 `FeatIdMap` entries name a `Feat`, and so do all 56
  ids the captured characters carry (`1173628` = `Decomposition`). An earlier pass reported the opposite, because
  it only knew the `[u32 len][name]` record shape and every one of those feats uses the `[u16 nameLen][u16 descLen]`
  one. What the catalog still does not cover is EFFECTS: 2,720 of the 6,593 mapped feats carry a readable
  stat-modifier region, so a trained feat naming no bonus is the norm rather than a decode failure.

## Feats vs. skills — the key distinction

AoC has two separate systems, and the codebase's naming predates the distinction:

| | what it is | where it lives on the wire | in our code |
|---|---|---|---|
| **Feats** (talent tree) | points spent to unlock/rank tree feats | full set in the **`FA015807` player-spawn pre-IIR** feat list; train/unlock via `FeatIIR 0x28DA68A0` | `CharacterFeat(FeatId, Sequence)` + the `Feat`/`FeatIdMap`/`FeatPrereq`/`FeatEffect` catalogs, trained through `FeatService` |
| **Skills / abilities** (spawn scaffold) | the flat id list a char carries; gates spell usability | the **`FA015807` player-spawn post-IIR** id-list | `CharacterSkill(SkillId, Rank)`, built by `SimpleCharFullUpdateCodec` |

**Proof they're distinct** (same packet, two different regions + id-spaces): unlocking the Hellfire feat
(`Captures/faolan-account-login-hakotep-hox-lvl24-unlock-feat-hellfire.tshark.json`) left the **post-IIR skill
scaffold** byte-identical (26 → 26 entries), while the **pre-IIR feat list** changed (5 → 6, gaining `112700`).
The feat id `112700` appears in `FeatIIR` and the pre-IIR feat list but never in the post-IIR scaffold; the
scaffold's ids (e.g. `302303`, `1445000`) share zero overlap with the feat ids (`80004`, `112700`, …).

## `FeatIIR` wire protocol (`0x28DA68A0`)

Name verified: `BKDR-31("FeatIIR") == 0x28DA68A0`, string present in `AgeOfConan.exe`. Body (after the standard
`[u32 len][u32 iir][u32 0x0000C350][u32 charHandle]` carrier prefix):

```
[u8 00][u8 state][u32 0][u32 (count+1)*0x3F1][count × u32 featId]
  state 0x04 = c2s train/unlock REQUEST
  state 0x05 = s2c ACK  (echoes the full id list back)
```

The `(count+1)*0x3F1` word is the standard `0x3F1`-framed list marker (`0x7E2` = 1 feat, `0xFC4` = 3 feats). Same
04→05 request/ack shape as the claims/redeem window (see the `claims_redeem_window` memory).

**Observed feat ids:** `112700` (Hellfire — in both feat captures), `109488`, `410600`. Captures that carry `FeatIIR`:

- `Captures/faolan-account-login-hakotep-hox-lvl24-unlock-feat-hellfire.tshark.json` — 1-feat train (`112700`).
- `Captures/Hakotep_hox_login_gameplay_untrain-feats-(causes_loading_screen)_train-feats_delete-quests.tshark.json` — 3-feat list.

## Full unlocked-feat set — DELIVERED IN THE PLAYER SPAWN (`FA015807`)

**The former #1 blocker ("the full unlocked-feat set at login is not in any capture") was wrong.** The set ships
in the char's own **player spawn** (`FA015807`), in the spawn's **pre-IIR** region — the block that
`tools/capture/parse_spawn_scaffold.py` deliberately keeps RAW ("its internal structure is analyzed
separately"), which is why earlier passes never saw it: `decode_skill_scaffold.py` reads the *post-IIR* skill id-list, a
different region.

Grammar (body-relative; a constant `0x07E2` landmark sits 9 zero bytes before the count marker):

```
... 07 E2  00×9  [u32 (count+1)*0x3F1]  [count × u32 featId BE]  00...
  count == 0  ->  the bare 0x3F1 marker, no ids (fresh char / all feats untrained)
```

Decoder: `tools/capture/decode_unlocked_feats.py` (`perchar` / `list` / `dump <cap> <idx>`). Per-character
sets recovered:

| char | class | feats | example ids |
|---|---|---:|---|
| NecroL80 | Necromancer L80 | 25 | `80004, 82704, 410604, 80200, …` |
| DemoL80 | Demonologist L80 | 25 | `102404, 103604, 103804, 102701, …` |
| Hakotep | HeraldOfXotli L24 | 6 | `110004, 110100, 111304, 111402, 110300, 112700` |
| every fresh/create char | — | 0 | (empty `0x3F1` marker) |

**Proof it is the feat set** (not a coincidental byte run):

- **Causal:** training the Hellfire feat (id `112700`) makes `112700` *appear* in this list. The hakotep
  pre-training login (`…-hakotep-hox-lvl24.tshark.json`) carries **5** feats (Hellfire absent); the same char's
  post-training gameplay spawn carries the same 5 **+ 112700** (= 6).
- **Same id-space as `FeatIIR`:** Hoxxling's spawn list == its `FeatIIR` list == `[110000, 112700, 410600]`.
- **Rank rides the low digits:** hakotep trained feat base `1100` to rank 4 → its spawn shows `110004`, while a
  rank-0 char (and the `FeatIIR` train *request*) shows `110000`. Working hypothesis: `featId = featBase*100 + rank`.

## What the dumps contain (and don't)

- **`FeatIIR` appears ONLY on train ACTIONS**, never on a plain login. Swept all captures for `FeatIIR` and for the
  candidate login-delivery records `ClonedFeatsIIR` (`0xF7BF33DC`) and `SkillIIR` (`0xD558FF16`): **zero occurrences.**
  The full-set login delivery we hunted for is not a separate record — it is folded into the player spawn (above).
  ⚠️ `SkillIIR` has since turned up in an annotated capture and is **not** a feat record at all — it is the
  skillpoint spend, see [`skillpoints.md`](skillpoints.md).
- Across all captures the only other feat traffic is a single untrain **confirm dialog** (`DispatchFeatUntrainMessageBox`
  → `FeatUntrainAnswer`), carrying no feat data.

The feat-window request/reply opcodes exist in `Opcodes.cs` but are unimplemented and unobserved: `GMRequestFeatData`
(`0x202C`), `GMRequestFeatClassData` (`0x202D`), `AdjustSkill` (`0x202E`, train), `UntrainFeat` (`0x2041`),
`FeatUntrainAnswer` (`0x203E`), `DoSkillSellback` (`0x205C`), s2c `DispatchUntrainSingleFeat` (`0x2011`).

## Current server state

- **Per-character unlocked-feat list: modeled + generated (2026-07-14).** `CharacterFeat` rows carry each char's
  trained feats; the player spawn regenerates the pre-IIR feat list from them (`PlayerSpawnScaffoldRows`).
- **Train + reset: implemented (2026-07-14).** `GameServerListener.Feats.cs`:
  - **Train** — c2s `FeatIIR` state `0x04` → `Database.AddCharacterFeats` (REPLACES the row for that feat BASE,
    keeping its original `Sequence`, because retail carries exactly one id per base at the current rank) → s2c
    `FeatIIR` state `0x05` ack (echoes the list, byte-exact via `FeatCodec`).
  - **Reset** (untrain-all) — c2s `UntrainFeat` (`0x2041`) → server sends the **confirm dialog**
    `DispatchFeatUntrainMessageBox` (`0x2064`, `FeatUntrainMessageBoxCodec`, byte-exact) → client confirms with
    `FeatUntrainAnswer` (`0x203E`, echoes the dialog's 16-byte payload + a leading `0x01`) → `Database.ClearCharacterFeats`
    → world reload (`DoTeleport` to the current spot), so the fresh spawn carries the cleared list (retail's "loading
    screen"). A session token in the dialog is matched against the answer so a stray answer can't reset.
  - **Prerequisite guard** — `FeatService` rejects a train that would exceed the character's feat-point budget
    (`FeatRules.PointBudget`, off the client's own RDB 1000044 per-level table: 79 points at L80, not the 71 a
    flat 1/level from L10 gives) **or** leave a prerequisite unmet (`FeatRules.MissingPrerequisites` over
    `FeatPrereq`, satisfied by the same BASE at an equal-or-higher rank — see the rank note above).
  - GM command `.feat list | train <id>... | reset` is the test bootstrap (the retail trigger is the client's Feats
    window + its point economy, which the server can't drive).
- `AdjustSkill` (`0x202E`) and `DoSkillSellback` (`0x205C`) belong to the SKILLPOINT pool, not to feats: the spend
  arrives as the `SkillIIR` record rather than the RPC, and is implemented — see [`skillpoints.md`](skillpoints.md).
  `DoSkillSellback` remains a debug-log stub because its refund amount is unrecoverable without a capture.
- The skills scaffold (`CharacterSkill`) is imported per captured char by `CaptureCharacterImporter` (via
  `PlayerSpawnScaffoldRows.ExtractTo`) and hand-transcribed per class in `PlayerSpawnScaffoldRows.FullSkillIdsFor`
  (from Necro/Demo/Assassin/Herald high-level captures). On level-up / a GM command, the class scaffold is granted so
  learned spells become castable (#298). **This is skills, not feats** — it just happens to share the `0x3F1`-list shape.
- ⚠️ **What a transcribed scaffold may carry is the class's CONTENT, never the captured player's own state.** A
  captured id list mixes the two, and granting it wholesale stated one player's facts about everybody else. Two bands
  are therefore held out of `FullSkillIdsFor`:
  - **The class-PROGRESSION entry**, which is the character's **level**. Every class has exactly one 80-long
    progression run in `FeatIdMaps` (`MaxRank >= 79`), and a character carries **exactly one** id from its own run, at
    `base + (level - 1)` — measured across five captured spawns at four levels: two fresh at rank 0, Hoxxling at
    `114804` (L5), and the Necro/Demo at `85279`/`104579` (L80). It resolves to that level's
    `<Class> Class Feat NNN` template, i.e. the class/level **attribute base**, which is where the client gets the
    five attributes and the MaxHealth it draws. So it is **derived per character** at spawn time
    (`PlayerSpawnScaffoldRows.ClassProgressionIdFor`) and never replayed from a row — a stored copy goes stale on
    every ding. This is the same exclusion the feat grant below already applies, arrived at from the other side.
  - **The tradeskill PROFESSION rungs**, which are what that player had **trained** (`.tradeskill` →
    `AddCharacterFeats`, [#735](tradeskill-recipes.md)). The captured Necromancer's were `Weaponsmith`/`Armorsmith
    Grandmaster` and four Masters; the Demonologist's eight more at its own ranks; the Assassin's a lone
    `Mining Apprentice`.

  Both reached the client together: a level-2 Necromancer's first relog after a ding rendered a level-80 attribute
  sheet (Str 71 / Int 231 / Con 61, health max 984) **and** a Grandmaster smith's crafting tab. Gated by
  `PlayerSpawnScaffoldRowsTests.ClassProgressionEntry_IsTheCharactersLevel` and
  `FullSkillScaffold_CarriesNoCapturedCharactersOwnState`.
- **The teaching feat is granted with the spell (2026-08-04, #298).** `FeatSpellGrants` (1,510 rows, 915 feats,
  667 spells — GameAction element kind `53019 Spell` on a feat template) is read *backwards* by
  `IDatabaseRepository.GetFeatsTeachingSpells`, so both level-up paths train exactly the feats that teach the
  spells just learned. That comes from the client RDB rather than a capture, so it covers all twelve classes
  instead of the four with a high-level player-spawn capture.
  ⚠️ **PROGRESSION TRACKS are excluded from that grant** — the bases whose low digits run past the client's own
  rank ceiling (`FeatIdMap.MaxRank`), so the digits index a family instead of naming a rank: the twelve
  `<Class> Class Feat 001` tracks with one entry per character level, plus `2HE 60-80`, `Bow 60-80` and
  `Class Specific HP 60-80`. Retail never puts one in a character's feat list — none appears across the four
  captured lists, and a level-80 Necromancer casts all 109 of her spells carrying 25 feats, none of them her
  class track. The feat list goes on the wire, so training one would be inventing content to satisfy a criterion
  that demonstrably does not need it.
- **…and the spell is granted with the TRAINED feat (2026-08-16, #847)** — the same table read forwards,
  `IDatabaseRepository.GetSpellsTaughtByFeats`, so `FeatService` hands over what a feat teaches at the moment it
  is trained. The two directions are not redundant, because `FeatSpellGrants` carries **two grant mechanisms** and
  they are separated by `Feat.AutomaticUnlockCategory` (7063):

  | category | feats | names | how the spell arrives |
  |---:|---:|---:|---|
  | **7063** | 271 | 24 — `Spell Unlocking <lo>-<hi>`, `<Class> Class Feat NNN`, `Pet Mastery` | on a LEVEL, from the ding path |
  | **8072** | 675 | 238, every one `Spell - <the spell's own name>` | on a TRAIN, from here |

  Measured over the 946 feats that grant a spell, with zero overlap. The captures settle which is which: gating
  the level path on the automatic category reproduces the captured spellbooks exactly (the level-80 Necromancer
  88 offered / 88 known, the Demonologist 95 / 95), while including the tree leaves 20 extra on the Necromancer —
  `Ritual of the Lich`, `Despoil the Soul`, `One with Death` — and the three of the same kind she DOES hold
  (`Cannibalism`, `No Rest For the Dead`, `Mark of the Devourer`) are 8072 too. What separates the two
  populations is a point spend, not a property of the spell. Standing measurement: `--report spell-grant-check`.

  ⚠️ Idempotent by construction — `AddCharacterSpell` reports whether the row was new and only a new one is
  announced — so re-training a feat re-toasts nothing.

## Why a learned spell may still refuse to cast — the feat criterion

⚠️ **This is not a reason for the server to grant a feat** (#862). The criterion runs against the client's own
`FeatHolder`, which already carries every auto-trained feat — the client puts them there itself, off RDB 1000043.
See the section at the end of this page.

A spell in the book is not a castable spell. The client runs a generic criteria evaluator
(`FUN_006dd970`, log channel `GameCode/Criteria`) on an actual action attempt, and **case `0x5d` is the FEAT
criterion**: it asks the character's feat holder whether it carries feat *F*, with **no level comparison of any
kind**. On failure it picks one of three LDB messages by what it can say about *F*:

| token | LDB | English text |
|---|---|---|
| `YouCanNotUse` | cat 130, id 89130261 | *"You cannot use this item"* — the template did not resolve |
| `CriteriaMissingFeat` | cat 100, id 194997716 | *"You must have trained the feat: %s"* |
| `YouCanNotUseYet` | cat 130, id 5372612 | *"Your class cannot use this item until level %d"* |

⚠️ **The third one reads like a level gate and is not.** The `%d` is a number the message carries about the
FEAT; the criterion that produced it compared nothing to the player's level. #298 spent a month on that reading
— at character level 15 a Dark Templar was told "until level 10", which looked like a third, undiscovered
player-level field stuck at 1, and the whole `.setstat` sweep of `0x00`–`0x600` that followed found nothing
because there was nothing to find. A real stat requirement is a *different* case (`2`) with a *different*
message, `Criteria_IsRequiredToBeAtLeast` = *"%s %s must be at least %d"*.

The one datum the level-gate reading rested on — "`GameAction_MinLevel` is stat `0x45B`" — was also wrong:
`CharacterStatId` carried an inherited label block there that the client's own LDB 615 table contradicts on all
seven slots at once (`0x45B`–`0x461` are the **PvP invulnerability ratings**, one per damage school). Corrected
2026-08-05; nothing had ever read one.

Resolving those tokens needs no live client — an LDB string id is the 28-bit ELF hash of the token name
(`LdbStringIndex.TokenId`), so any `FUN_0061f640(<cat>, "<Token>")` in the exe resolves statically:

```zsh
dotnet run --project Faolan.Importer -- --report ldb-resolve --Content:RdbDir <rdb-dir> \
  --report-arg YouCanNotUseYet,CriteriaMissingFeat,Criteria_IsRequiredToBeAtLeast
```

## RE assets & tools

- `tools/capture/decode_unlocked_feats.py` — **the feat decoder**: reads the char's full unlocked feat set from
  the `FA015807` player-spawn **pre-IIR** region (modes `perchar` / `list` / `dump <cap> <idx>`).
- `tools/capture/decode_skill_scaffold.py` — reads the **ability/skill scaffold** (id + tag) from the spawn's *post-IIR*
  region; self-IDs class from the profession id (`featId // 100 == Class` enum value). Modes: per-class / `discover` /
  `dump` / `json`. NB it reads skills, not feats (documented in its header).
- `OpcodesSource` — sweeps captures for IIR hashes/opcodes missing from `Opcodes.cs` and
  enumerates client IIR classes we've never named/seen. This is how `FeatIIR` was found.
- `tools/ghidra/DumpVtableReaders.java` — recovers any IIR's `ReadSubClass` reader (vtable slot 8) → authoritative
  hash + grammar. Run headless via the `#`-free symlink (see the `ghidra_hash_path_symlink` memory).
- RDB feat catalog: type 1000020, **19,777** readable feat templates (id + name + description + tree + rank).
  The remainder of that type is TEA-encrypted with server-only keys, but that costs the feat tree nothing:
  prerequisites and the wire↔template map come from type **1000042** and the point budget from **1000044**,
  both plaintext (see the `feat_trees_decode_251` memory).

## References

- The subsystem tracker **#251 is closed**; the remaining feat work is tracked per gap: **#593** (the modifier
  kinds still unimported — `ChangeInheritedWeaponAttack`, and the unnamed trigger/magnitude slots on the proc
  family), **#595** (the seven mis-sliced `Swift Pathing` elements), **#271** (nothing consumes
  `FeatComboModifier`'s reuse-time half yet), **#298** (skills scaffold gates spell usability), **#313**
  (create-flow combos, same server-held-key class).
- Memories: `feat_trees_decode_251` (the correction + FeatIIR), `snapshot_known_spell_list`, `player_spawn_scaffold_generated`.

## An AUTOMATIC feat costs no points (#850) — and is never in a feat list at all (#862)

`FeatRules.PointCost` used to be a placeholder — `max(1, the rank baked into the id's low two digits)` — and it
was charging for the feats a character never chose. Two paths disagreed about them: `LevelUpService.
GrantCastabilityFor` trained the feats that teach a newly learned spell with **no** budget check, and
`FeatService.TrainWithinBudget` then counted the character's whole feat set against `FeatRules.PointBudget`.

⛔ **The interaction was not marginal, it was fatal.** Measured over the shipped catalog, the automatic feats a
level-80 accumulated cost:

| class | automatic feats | points | budget at 80 |
|---|---|---|---|
| 41 | 77 | **153** | 79 |
| 44 | 54 | **97** | 79 |
| 24 | 47 | **89** | 79 |

So three classes overspent the entire tree budget before the player picked anything, and every train request was
rejected outright. Exempting them (#850) removed the interaction. **#862 removed the cause**: those feats are not
the server's to grant, and both halves of that ticket resolve to the same client table.

### The client trains them ITSELF — RDB 1000043, the featclass table

`FeatClassTable_c` loads **RDB type 1000043 id 1** at startup ("Could not load featclass info" is its own error
string). Imported as `FeatClassEntry` (**9,916** rows), it is a 20-byte header then 40-byte entries of ten words:

| word | column | what it holds |
|---:|---|---|
| 0 | `Id` | the client's own row id (106..738078) — a natural key, so a re-import renumbers nothing |
| 1 | `Class` | the `ConanClass` value, or 0 (→ `null`) on the 188 rows that name no class |
| 2 | `Level` | the character level the row applies at, 1..80 |
| 3 | `FeatId` | the **WIRE** feat id — 9,907 of 9,916 resolve through `FeatIdMap` |
| 4 | `Autotrain` | ⭐ **the client trains this feat by itself**; true on 3,839 rows |
| 5 | `TreeId` | 1000001..1000190 for a real tree, `900` innate, `9000` the class progression track |
| 6 | `TreeColumn` | the feat's column within its tier row in the tree UI |
| 7 | `Flag` | undecoded — see the column's own comment for the census |
| 8 | `Tier` | its tier (row) in the tree, 1..9 |
| 9 | `TreePointsRequired` | points that must already be spent IN THAT TREE — `(Tier - 1) * 5` on all but 57 of 3,197 ordinary-tree rows |

⭐ **`Autotrain` is why a captured feat list is so short.** The client builds a per-class level→feat map from these
rows (`FUN_0078c990`, which logs *"Autotrain feat: `<id>` for class: `<class>` at level: `<level>`"*) and trains
them with no server involvement. Everything the old level-up grant handed out is one of them: the
`Spell Unlocking <lo>-<hi>` bands, the `<Class> Class Feat NNN` tracks and `Pet Mastery`. Read straight off the
table, the Necromancer's schedule is `Necromancer Class Feat 001` ranks 0..9 at levels 1..10, then
`Spell Unlocking 20-40` at 20/25/30/35, `40-60` at 40/45/50/55 and `60-80` at 60/65/70/75/80.

**The captures agree without exception**: all **56** feat ids across the four captured characters are
`Autotrain = false`. Not one automatic feat is on the wire anywhere in the corpus, and the characters that DID
level up during a capture (Blablabla, Blablatest, Stihetfv) end with an empty feat list despite learning spells.

⛔ **And sending one is not merely redundant.** `FeatHolder::TrainFeat` (`FUN_01135e20`, `Feat\FeatHolder.cpp:74`)
refuses a class feat outright, asserting `pcFeat->GetCategory() != e_ClassFeat` and logging *"Character:%d called
TrainFeat on class feat : %d ( will break feats! )"*. So `LevelUpService.GrantCastabilityFor` no longer trains
anything; it grants the class skill scaffold and nothing else, and `GetAutotrainedFeatWireIds` (the client's flag,
replacing the `Feat.AutomaticUnlockCategory` proxy, which disagrees with it on 204 of 9,907 rows) is what the
budget exempts. The spellbook offer is unaffected — `--report spell-grant-check` is unchanged at 97/96/5 matched,
EXTRA 0, DROPPED 0.

⚠️ The bug was visible in one line once the table was read: a level-4 test character was carrying
`Spell Unlocking 20-40`, `40-60` and `60-80` — feats retail hands over at 30, 45 and 75.

### ⭐ The schedule is a MEMBERSHIP source as well as a level one (#864)

The same derivation answers a second question. A feat the client auto-trains **for a class**, teaching a spell,
says that class has access to that spell — so `ClassSpells` gains a row where it had none. It adds **12**, and
what makes them interesting is *which*: the Herald of Xotli's `Arcane Abatement` ranks 2–5, `Burning Skull` 1–3
and `Exultation of Xotli` 1–4, plus the Tempest of Set's `Lightning Sparks`.

Every one carries a `GuiStackingLine` of **71 / 76 / 232** — not the `ConanClass * 1000 + index` form membership
is normally derived from, so the class map was blind to them by construction. `Arcane Abatement` **rank 1** did
have a row, from the captured-spellbook source; ranks 2–5 had no source at all, which is why a Herald could never
have been offered them.

⛔ **`Level > 0` only, and that exclusion is the whole safety of it.** Every class auto-trains the four `Dance:`
emotes at level 5, and a Level-0 record belongs to no class — mounts, pets, emotes and travel unlocks are
restored from the character's own rows on login, never from a class query. It is the difference between 12 rows
and 57.

⚠️ No captured character can be over-offered by this: the Herald spells all gate at 30+ and the only captured
Herald is level 24, and no captured character is a Tempest of Set. `--report spell-grant-check` is unchanged at
EXTRA 0 / DROPPED 0 on every character.

### A feat costs ONE POINT PER RANK, and the captures pin it exactly

`FeatRules.PointCost` is `rank + 1` — the rank baked into the wire id's low two digits is 0-based, so holding
rank 4 means five points were spent walking it up. Summing that over each captured character's feat list
reproduces `PointBudget` for its level **to the point**:

| character | level | feats | points | budget |
|---|---:|---:|---:|---:|
| NecroL80 | 80 | 25 | **79** | 79 |
| DemoL80 | 80 | 25 | **79** | 79 |
| Hakotep (Herald of Xotli) | 24 | 6 | **16** | 16 |

Three characters at two different levels, each spending its budget to the last point, against a budget that comes
from the client's own `FeatPointsTable` and feat lists that come off the wire. Nothing was fitted. Under the old
`max(1, rank)` all three had points left over (60, 61, 13) — headroom that never binds, which is precisely why the
wrong cost survived so long (the same trap `PointBudget` itself fell into).

⚠️ **There is no per-feat cost table for an ordinary feat, and that is a finding rather than a gap.** RDB
**1000099** *does* exist — the client calls it "feat point cost info" and it is a flat vector of
`(featclassRowId, kind, value)` triples — but all **2,880** of its rows belong to the fourteen level-20/80
mastery trees (`TreeId` 1000177..1000190), whose values run to 30 and could not be point costs under a budget of
79. Not one feat any captured character holds has a row in it. Kinds 2 and 3 appear 1,548 times each.
`--report feat-autotrain-check` is the standing measurement for both halves of this section.

The client's other feat resources, found the same way (`FUN_0078de40` loads all four before building the
auto-train map): **1000506** id 1 "classmembership info" and **1065101** id 0 "featspecializedtrees info" — both
present in the RDB, neither yet read.
