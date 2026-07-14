# Feats subsystem — handoff (Gitea #251)

Status as of 2026-07-14. Feats are **decoded at the wire level but not implemented as a subsystem.**
This is the pickup document: what a feat is (vs. what the code currently calls a "skill"), the wire
protocol we recovered, what the capture dumps do and don't contain, and the ordered next steps.

## TL;DR

- **`FeatIIR` (`0x28DA68A0`) is the feat record** — a train/unlock handshake carrying a `0x3F1`-framed list
  of feat ids. Decoded this session; added to `Opcodes.cs`.
- **The spawn scaffold is NOT feats.** `CharacterSkill` / `PlayerSpawnScaffoldRows` / `decode_feats.py` read the
  character's **ability/skill** presence list, a different id-space. It gates spell *usability* (see #298) but is
  unrelated to the feat tree. An earlier RE conflated the two; that has been corrected across code + memories.
- **No feat model exists** (no table, no tree, no training protocol). `CharacterSkill` is the skills scaffold, not feats.
- **The full unlocked-feat set at login is not in any capture** — the biggest blocker to building the subsystem.

## Feats vs. skills — the key distinction

AoC has two separate systems, and the codebase's naming predates the distinction:

| | what it is | where it lives on the wire | in our code |
|---|---|---|---|
| **Feats** (talent tree) | points spent to unlock/rank tree feats | `FeatIIR 0x28DA68A0` (train/unlock); full-set-at-login delivery unknown | **nothing yet** |
| **Skills / abilities** (spawn scaffold) | the flat id list a char carries; gates spell usability | the FA015807 player-spawn tail id-list | `CharacterSkill(SkillId, Rank)`, built by `PlayerSpawnScaffoldCodec` |

**Proof they're distinct:** unlocking the Hellfire feat (`Captures/faolan-account-login-hakotep-hox-lvl24-unlock-feat-hellfire.tshark.json`)
left the spawn scaffold byte-identical (26 → 26 entries); the feat id `112700` appears only in `FeatIIR`, never in
the scaffold; the scaffold's ids (e.g. `302303`, `1445000`) share zero overlap with learned spells and don't join
the RDB feat catalog id-space.

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

## What the dumps contain (and don't)

- **`FeatIIR` appears ONLY on train ACTIONS**, never on a plain login. Swept all captures for `FeatIIR` and for the
  candidate login-delivery records `ClonedFeatsIIR` (`0xF7BF33DC`) and `SkillIIR` (`0xD558FF16`): **zero occurrences.**
- Across all captures the only other feat traffic is a single untrain **confirm dialog** (`DispatchFeatUntrainMessageBox`
  → `FeatUntrainAnswer`), carrying no feat data.
- **So how the client learns its FULL unlocked-feat set at login is not captured.** It is delivered by the server (the
  client renders the feat window from its own RDB + the char's feat-presence set), but no capture we have shows that
  delivery. This is the #1 blocker.

The feat-window request/reply opcodes exist in `Opcodes.cs` but are unimplemented and unobserved: `GMRequestFeatData`
(`0x202C`), `GMRequestFeatClassData` (`0x202D`), `AdjustSkill` (`0x202E`, train), `UntrainFeat` (`0x2041`),
`FeatUntrainAnswer` (`0x203E`), `DoSkillSellback` (`0x205C`), s2c `DispatchUntrainSingleFeat` (`0x2011`).

## Current server state

- **No feat model.** No feat table, no tree, no feat points, no training. `DoSkillSellback` (`0x205C`) is a debug-log stub.
- The skills scaffold (`CharacterSkill`) is imported per captured char by `CaptureCharacterImporter` (via
  `PlayerSpawnScaffoldRows.ExtractTo`) and hand-transcribed per class in `PlayerSpawnScaffoldRows.FullSkillIdsFor`
  (from Necro/Demo/Assassin/Herald high-level captures). On level-up / a GM command, the class scaffold is granted so
  learned spells become castable (#298). **This is skills, not feats** — it just happens to share the `0x3F1`-list shape.

## RE assets & tools

- `Captures/scripts/decode_feats.py` — reads the **ability/skill scaffold** (id + tag) from any dump; self-IDs class
  from the profession id (`featId // 100 == Class` enum value). Modes: per-class / `discover` / `dump` / `json`. NB it
  reads skills, not feats (documented in its header).
- `Captures/scripts/find_missing_opcodes.py` — sweeps captures for IIR hashes/opcodes missing from `Opcodes.cs` and
  enumerates client IIR classes we've never named/seen. This is how `FeatIIR` was found.
- `Captures/ghidra/DumpVtableReaders.java` — recovers any IIR's `ReadSubClass` reader (vtable slot 8) → authoritative
  hash + grammar. Run headless via the `#`-free symlink (see the `ghidra_hash_path_symlink` memory).
- RDB feat catalog: type 1000020, ~20,417 plaintext feats (id + name + tree + rank; all tree names present); ~79% are
  TEA-encrypted with server-only keys (costs/prereqs unrecoverable from captures — see the `feat_trees_decode_251` memory).

## Open questions / blockers

1. **Login-time full feat-set delivery — not captured.** Needs a fresh capture: log in a feat-heavy char and/or open
   the Feats window (K). Prime suspect record: `ClonedFeatsIIR` (`0xF7BF33DC`). Until then the subsystem can't be built
   faithfully.
2. **id → name / tree mapping.** Scaffold and `FeatIIR` ids are GDB ids; they do not join the RDB 1000020 catalog
   id-space. The tree STRUCTURE (trees/tiers/prereqs/costs) is in the client RDB (partly plaintext) and on the wiki
   (https://aoc.fandom.com/wiki/Feat_Tree); the id↔name bridge is server-GDB-only and never transmitted.
3. **Is the `FeatIIR` list the full set or a delta?** Only train-action samples exist (1 and 3 ids). Unknown whether a
   train action echoes the whole trained set or just the delta. Resolved by the capture in (1).

## Next steps (ordered)

1. **Capture the missing delivery** (user action): a login of a feat-heavy char with the Feats window opened / a feat
   trained, on loopback. That single capture unblocks everything below.
2. **Decode the login feat record** from that capture (likely `ClonedFeatsIIR`): confirm whether the full unlocked set
   ships at login and its grammar. Add a Python dissector `Captures/scripts/decode_*.py` + a byte-exact golden test.
3. **Model.** Add a feat definition + per-character feat table (distinct from `CharacterSkill`). Seed the per-character
   trained set from the captured chars (the importer already parses their spawns).
4. **Tree structure.** Ingest the wiki tree (trees / tiers / prereqs / point costs / names) as reference data — the RDB
   can't give costs/prereqs (encrypted), the wiki can.
5. **Training protocol.** Implement `AdjustSkill` (`0x202E`) train / `UntrainFeat` (`0x2041`) / the feat-window
   request-reply, emitting `FeatIIR` on train (its builder is now grammar-complete).

## References

- Issues: **#251** (this tracker), **#298** (skills scaffold gates spell usability), **#313** (create-flow combos,
  same server-held-key class).
- Memories: `feat_trees_decode_251` (the correction + FeatIIR), `snapshot_known_spell_list`, `player_spawn_scaffold_generated`.
- Session commits (2026-07-14): `FeatIIR` added to `Opcodes.cs`; `decode_feats.py`; skills-vs-feats corrections across
  code + docs + memories.
