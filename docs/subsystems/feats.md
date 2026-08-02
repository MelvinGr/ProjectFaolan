# Feats subsystem (Gitea #251)

Feats are **decoded at the wire level but not implemented as a subsystem.** This documents what a feat is
(vs. what the code currently calls a "skill"), the wire protocol recovered, and what the capture dumps do
and don't contain.

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
- **The feat CATALOG now exists** (#251): `Feat` (**19,777** rows from the RDB 1000020 plaintext templates - id, name,
  description, tree, group, rank) + `FeatEffect` (**23,321** stat modifiers, 173 distinct stats). Built by
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
  rows; dropping the whole KIND rather than the rows that failed a stat lookup took the residue to **81 of 23,321**.
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
  **2,939 of the 6,593 mapped feats have readable effects.** The captured characters DO now resolve - all 56 of
  their feats name a real feat - but only 9 of those 56 carry stat effects.
- ⚠️ **The other 47 are NOT feats "whose record has no modifier region" — that claim was wrong (#590).** Their
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

  It is carried by the SPELL templates themselves, in **property-table field 128** — so a feat names a spell LINE
  and therefore all of its ranks at once, which is exactly what a feat needs to do. Resolve one with
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

  Catalog-wide there are **2,361 feat templates** carrying an out-of-stat-range record across ~10 kinds (53209
  x3235, 53019 x1010, 53030 x962, 53054 x593, 53212 x561, 53062 x475, 53075 x342, …); only 53075 is decoded here,
  the rest are surveyed but unread. Survey with `--report feat-spell-modifiers [--report-arg <kind>]`; dump and
  diff a feat's ranks with `--report feat-record --report-arg [<rdbType>/]<id>[,<id>...]` (the word that MOVES
  across ranks is the magnitude, the one that stays is the target).

  **Not yet imported and not yet applied.** There is no table for these and no consumer: feats reach the server
  only as a flat `CharacterStatId → value` map, so a per-spell modifier has nowhere to land. Open work: Gitea #590.
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
- A resolved `TemplateId` is not always a `Feat` row: the catalog holds only the plaintext ~21% of type 1000020, and
  the captured characters' feats resolve largely into the TEA-encrypted remainder (id 1173628 is a real 217-byte
  record we cannot read). The MAPPING is complete; the catalog is not.

## Feats vs. skills — the key distinction

AoC has two separate systems, and the codebase's naming predates the distinction:

| | what it is | where it lives on the wire | in our code |
|---|---|---|---|
| **Feats** (talent tree) | points spent to unlock/rank tree feats | full set in the **`FA015807` player-spawn pre-IIR** feat list; train/unlock via `FeatIIR 0x28DA68A0` | **nothing yet** |
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
- Across all captures the only other feat traffic is a single untrain **confirm dialog** (`DispatchFeatUntrainMessageBox`
  → `FeatUntrainAnswer`), carrying no feat data.

The feat-window request/reply opcodes exist in `Opcodes.cs` but are unimplemented and unobserved: `GMRequestFeatData`
(`0x202C`), `GMRequestFeatClassData` (`0x202D`), `AdjustSkill` (`0x202E`, train), `UntrainFeat` (`0x2041`),
`FeatUntrainAnswer` (`0x203E`), `DoSkillSellback` (`0x205C`), s2c `DispatchUntrainSingleFeat` (`0x2011`).

## Current server state

- **Per-character unlocked-feat list: modeled + generated (2026-07-14).** `CharacterFeat` rows carry each char's
  trained feats; the player spawn regenerates the pre-IIR feat list from them (`PlayerSpawnScaffoldRows`).
- **Train + reset: implemented (2026-07-14).** `GameServerListener.Feats.cs`:
  - **Train** — c2s `FeatIIR` state `0x04` → `Database.AddCharacterFeats` (append, dedup) → s2c `FeatIIR` state
    `0x05` ack (echoes the list, byte-exact via `FeatCodec`).
  - **Reset** (untrain-all) — c2s `UntrainFeat` (`0x2041`) → server sends the **confirm dialog**
    `DispatchFeatUntrainMessageBox` (`0x2064`, `FeatUntrainMessageBoxCodec`, byte-exact) → client confirms with
    `FeatUntrainAnswer` (`0x203E`, echoes the dialog's 16-byte payload + a leading `0x01`) → `Database.ClearCharacterFeats`
    → world reload (`DoTeleport` to the current spot), so the fresh spawn carries the cleared list (retail's "loading
    screen"). A session token in the dialog is matched against the answer so a stray answer can't reset.
  - **Prerequisite guard** — training is rejected when the resulting feat set would exceed the character's feat-point
    budget (`FeatRules`: `max(1, rank)` per feat vs `max(0, level−9)` points, i.e. 1/level from L10, calibrated so
    every captured char fits with headroom). This is the enforceable subset without the client's RDB catalog; the
    tier/parent-feat prerequisite graph still needs the `Feat` definition table.
  - GM command `.feat list | train <id>... | reset` is the test bootstrap (the retail trigger is the client's Feats
    window + its point economy, which the server can't drive).
- `DoSkillSellback` (`0x205C`) is a debug-log stub.
- The skills scaffold (`CharacterSkill`) is imported per captured char by `CaptureCharacterImporter` (via
  `PlayerSpawnScaffoldRows.ExtractTo`) and hand-transcribed per class in `PlayerSpawnScaffoldRows.FullSkillIdsFor`
  (from Necro/Demo/Assassin/Herald high-level captures). On level-up / a GM command, the class scaffold is granted so
  learned spells become castable (#298). **This is skills, not feats** — it just happens to share the `0x3F1`-list shape.

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
- RDB feat catalog: type 1000020, ~20,417 plaintext feats (id + name + tree + rank; all tree names present); ~79% are
  TEA-encrypted with server-only keys (costs/prereqs unrecoverable from captures — see the `feat_trees_decode_251` memory).

## References

- Issues: **#251** (this tracker), **#298** (skills scaffold gates spell usability), **#313** (create-flow combos,
  same server-held-key class).
- Memories: `feat_trees_decode_251` (the correction + FeatIIR), `snapshot_known_spell_list`, `player_spawn_scaffold_generated`.
- Session commits (2026-07-14): `FeatIIR` added to `Opcodes.cs`; `decode_skill_scaffold.py`; skills-vs-feats corrections across
  code + docs + memories.
