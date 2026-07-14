# Feats subsystem — handoff (Gitea #251)

Status as of 2026-07-14. Feats are **decoded at the wire level but not implemented as a subsystem.**
This is the pickup document: what a feat is (vs. what the code currently calls a "skill"), the wire
protocol we recovered, what the capture dumps do and don't contain, and the ordered next steps.

## TL;DR

- **`FeatIIR` (`0x28DA68A0`) is the feat train/unlock record** — a handshake carrying a `0x3F1`-framed list
  of feat ids. Decoded; added to `Opcodes.cs`.
- **The full unlocked-feat set IS in the captures — it rides the player spawn (`FA015807`), NOT the FullChar
  snapshot.** It lives in the spawn's **pre-IIR** region (the block `parse_spawn_scaffold.py` keeps raw), as a
  `0x3F1`-framed `u32` feat-id list. Decoded 2026-07-14 by `Captures/scripts/decode_unlocked_feats.py`. **This
  retires the former "#1 blocker".** Every high-level captured char yields a stable set (NecroL80 necro 25, DemoL80
  demo 25, Hakotep hox 6); every fresh/create char yields the empty `0x3F1` marker.
- **The spawn scaffold is NOT feats.** `CharacterSkill` / `PlayerSpawnScaffoldRows` / `decode_skill_scaffold.py` read the
  character's **ability/skill** presence list — the spawn's **post-IIR** id-list, a different region *and* a
  different id-space. It gates spell *usability* (see #298) but is unrelated to the feat tree. An earlier RE
  conflated the two; that has been corrected across code + memories.
- **No feat model exists yet** (no table, no tree, no training protocol). `CharacterSkill` is the skills scaffold, not feats.

## Feats vs. skills — the key distinction

AoC has two separate systems, and the codebase's naming predates the distinction:

| | what it is | where it lives on the wire | in our code |
|---|---|---|---|
| **Feats** (talent tree) | points spent to unlock/rank tree feats | full set in the **`FA015807` player-spawn pre-IIR** feat list; train/unlock via `FeatIIR 0x28DA68A0` | **nothing yet** |
| **Skills / abilities** (spawn scaffold) | the flat id list a char carries; gates spell usability | the **`FA015807` player-spawn post-IIR** id-list | `CharacterSkill(SkillId, Rank)`, built by `PlayerSpawnScaffoldCodec` |

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
`Captures/scripts/parse_spawn_scaffold.py` deliberately keeps RAW ("its internal structure is analyzed
separately"), which is why earlier passes never saw it: `decode_skill_scaffold.py` reads the *post-IIR* skill id-list, a
different region.

Grammar (body-relative; a constant `0x07E2` landmark sits 9 zero bytes before the count marker):

```
... 07 E2  00×9  [u32 (count+1)*0x3F1]  [count × u32 featId BE]  00...
  count == 0  ->  the bare 0x3F1 marker, no ids (fresh char / all feats untrained)
```

Decoder: `Captures/scripts/decode_unlocked_feats.py` (`perchar` / `list` / `dump <cap> <idx>`). Per-character
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
    `0x05` ack (echoes the list, byte-exact via `FeatIirCodec`).
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
- Still missing: a `Feat` **definition** table (tree/tier/parent-prereqs/exact costs — the wiki job).
  `DoSkillSellback` (`0x205C`) is still a debug-log stub.
- The skills scaffold (`CharacterSkill`) is imported per captured char by `CaptureCharacterImporter` (via
  `PlayerSpawnScaffoldRows.ExtractTo`) and hand-transcribed per class in `PlayerSpawnScaffoldRows.FullSkillIdsFor`
  (from Necro/Demo/Assassin/Herald high-level captures). On level-up / a GM command, the class scaffold is granted so
  learned spells become castable (#298). **This is skills, not feats** — it just happens to share the `0x3F1`-list shape.

## RE assets & tools

- `Captures/scripts/decode_unlocked_feats.py` — **the feat decoder**: reads the char's full unlocked feat set from
  the `FA015807` player-spawn **pre-IIR** region (modes `perchar` / `list` / `dump <cap> <idx>`).
- `Captures/scripts/decode_skill_scaffold.py` — reads the **ability/skill scaffold** (id + tag) from the spawn's *post-IIR*
  region; self-IDs class from the profession id (`featId // 100 == Class` enum value). Modes: per-class / `discover` /
  `dump` / `json`. NB it reads skills, not feats (documented in its header).
- `Captures/scripts/find_missing_opcodes.py` — sweeps captures for IIR hashes/opcodes missing from `Opcodes.cs` and
  enumerates client IIR classes we've never named/seen. This is how `FeatIIR` was found.
- `Captures/ghidra/DumpVtableReaders.java` — recovers any IIR's `ReadSubClass` reader (vtable slot 8) → authoritative
  hash + grammar. Run headless via the `#`-free symlink (see the `ghidra_hash_path_symlink` memory).
- RDB feat catalog: type 1000020, ~20,417 plaintext feats (id + name + tree + rank; all tree names present); ~79% are
  TEA-encrypted with server-only keys (costs/prereqs unrecoverable from captures — see the `feat_trees_decode_251` memory).

## Open questions / blockers

1. ~~**Login-time full feat-set delivery — not captured.**~~ **RESOLVED 2026-07-14** — it rides the player spawn
   (`FA015807` pre-IIR), decoded by `Captures/scripts/decode_unlocked_feats.py`. No fresh capture needed.
2. **id → name / tree mapping.** Scaffold and `FeatIIR` ids are GDB ids; they do not join the RDB 1000020 catalog
   id-space. The tree STRUCTURE (trees/tiers/prereqs/costs) is in the client RDB (partly plaintext) and on the wiki
   (https://aoc.fandom.com/wiki/Feat_Tree); the id↔name bridge is server-GDB-only and never transmitted.
3. **Confirm the `featId = featBase*100 + rank` encoding.** The low digits track rank (hakotep `110004` = base
   `1100` rank 4 vs `FeatIIR` request base `110000`). Cross-check against a multi-rank train capture / the wiki tiers.
4. **Feat points** (spent / available per tree) — not yet located in the spawn; may be a nearby pre-IIR field or a
   separate record. Decode alongside the id list.

## Next steps (ordered)

1. ~~**Model + generation.**~~ **DONE 2026-07-14.** `CharacterFeat` table (a per-character unlocked-feat list,
   distinct from `CharacterSkill`; `FeatId` composite + `Sequence` for the train order), migration `AddCharacterFeat`.
   `PlayerSpawnScaffoldRows.ExtractPreFrameFeats` / `FreshPreFrame(class, mask, feats)` extract & regenerate the
   pre-IIR feat list; `ExtractTo`/`TryApplyFrom` map it to/from rows (golden tests in `PlayerSpawnScaffoldRowsTests`).
   The importer seeds it from captures (`CaptureCharacterImporter`, `>` level 5 only): NecroL80/DemoL80 25, Hakotep 6.
2. **Tree structure.** Ingest the wiki tree (trees / tiers / prereqs / point costs / names) as reference data — the RDB
   can't give costs/prereqs (encrypted), the wiki can. A `Feat` definition table (id → name/tree/tier) is the join
   target for `CharacterFeat.FeatId`.
3. ~~**Training protocol.**~~ **DONE 2026-07-14** — `FeatIIR` 0x04→0x05 train, the `UntrainFeat` →
   `DispatchFeatUntrainMessageBox` → `FeatUntrainAnswer` reset dialog, and the feat-point prerequisite guard
   (`FeatRules`), all in `GameServerListener.Feats.cs`. Remaining: exact per-feat costs + the tier/parent-prereq
   graph (needs the `Feat` definition table from step 2).
3. **Training protocol.** Implement `AdjustSkill` (`0x202E`) train / `UntrainFeat` (`0x2041`) / the feat-window
   request-reply, emitting `FeatIIR` on train (its builder is grammar-complete) and updating the per-char feat table
   so the next spawn reflects it.

## References

- Issues: **#251** (this tracker), **#298** (skills scaffold gates spell usability), **#313** (create-flow combos,
  same server-held-key class).
- Memories: `feat_trees_decode_251` (the correction + FeatIIR), `snapshot_known_spell_list`, `player_spawn_scaffold_generated`.
- Session commits (2026-07-14): `FeatIIR` added to `Opcodes.cs`; `decode_skill_scaffold.py`; skills-vs-feats corrections across
  code + docs + memories.
