# Handoff — insert the per-character KNOWN-SPELL list into the FullChar snapshot (#74 / #71)

> **RESOLVED 2026-06-20.** Implemented + verified in-client. The learned-spell records are now spliced into the
> from-scratch tails' stat-block known-list (`KnownSpellList` + `TailsStatBlock.InjectKnownSpells`, wired through
> `BeachTailsBuilder.BuildFromScratch` ← `BurstlessWorldEntry`). Byte-exact against the captured Faolanx 2-record
> list and the empty beach template (`KnownSpellListTests`, 568 tests green). In-client: a necro with its 2 create-
> spells enters the world (chat connects, no crash) on both the band-aid build and the cleaned build, confirming the
> known-list — not the grant band-aid — delivers the spells. The interim band-aids were removed
> (`PendingSpellLearns` re-announce + session field + drain). **DIVERGENCE from step "Once it works":** the
> `EnsureStartingLoadout` pre-snapshot ability grant was KEPT — abilities are NOT in the known-list (Faolanx's list
> is spells only, type 2; no type-5 ability records), so they still need the pre-snapshot grant for bar placement.
> Original plan retained below for reference.

**Goal:** make a character's learned spells (and the abilities behind type-4 bar entries) *persist across
relog* and *place on the action bar*, the retail way — by carrying them in the `0x737A6DF9` FullChar
world-entry snapshot's **known-list**, instead of the grant-packet band-aids.

**Why it's needed:** our production snapshot reuses the captured **Strie (assassin) beach tails**, whose
known-list is empty (a fresh char has no spells). So the client never learns the character's own spells:
- A seeded spell appears only on the FIRST login (post-ready `DispatchLearnSpell` announce) and **vanishes on
  relog** — the snapshot says "nothing known", overriding the grant.
- The 3 Normal Attacks (type-4 bar entries) won't auto-place, because the client won't bind a bar slot to an
  ability the snapshot doesn't list.

Both symptoms are the **same root cause**: the known-list is missing from our generated snapshot.

This was committed as far as the infrastructure goes (`2ca009a` — `ClassStartingSpell` table + importer +
`EnsureStartingLoadout` seed; `c390d88` — the bar-restore endpoint fix). What remains is THIS snapshot work.

## What's decoded (ground truth — pinned 2026-06-20)

### Record format (DEFINITIVE)
The known-list is a **`0x3F1`-framed list** of **12-byte records**:
```
[u32 id  (big-endian)] [u32 0xFFFFFFFF] [u8 0x00 ×3] [u8 type]
```
- `type 2` = spell, `type 5` = feat/ability. The `0xFFFFFFFF` is presumed "no bound bar slot".
- Count word `c = (recordCount + 1) * 0x3F1` (the standard list-framing invariant; `0x3F1` = 1009).

Confirmed in two captures:
- **NecroL80** (developed necro, `Captures/NecroL80_mage-necromancer_login_longload.tshark.json`, the ~620KB
  `0x2000` burst pkt304 @ ~`0x97044`): **108 records** (103 spell + 5 feat) = her class's *available* set at
  level — NOT just learned. (A fully faithful regen of THIS is data-blocked: needs the RDB class/feat catalog,
  TEA-encrypted — see `feat_trees_decode_251`. We are NOT doing the full set.)
- **Faolanx** (clean fresh necro, `Captures/Faolanx_mage-necromancer_login.tshark.json`, snapshot pkt51,
  9771 B, list count @ `0x25F6`): **exactly 2 records** = its 2 create-spells, count `0x0BD3` (=`0x3F1*3`):
  ```
  0x0BD3                                         # count, 2 records
  00 42 CA 77  FF FF FF FF  00 00 00  02         # spell 0x42CA77, type 2
  00 12 5D 9B  FF FF FF FF  00 00 00  02         # spell 0x125D9B, type 2
  ```
  **This is the target shape**: a fresh char's known-list is just its N learned spells. So we insert exactly
  the character's `CharacterSpells` (type 2). (The 3 Normal-Attack abilities likely need type-5 records too —
  verify against Faolanx's full list whether the attack ids appear; the spell case is the proven part.)

### WHERE it goes in OUR beach tails (PINNED)
`Faolan/GameServer/Packets/FullCharSnapshot/BeachTailsTemplate.bin` (9282 B) has the **empty** known-list at
offset **`0x2425`**: count `0x000003F1` (0 records). It is identified by the **preceding 12-byte landmark**
`00 00 00 00  00 00 02 90  00 00 00 00` (i.e. `[u32 0][u32 0x0290][u32 0]`), which is the SAME landmark that
precedes Faolanx's populated `0xBD3` list. So the region is structurally the same slot; beach = empty,
Faolanx = 2 records.

The beach tails have other empty `0x3F1` slots (`@0x490`, `@0x1EED`, `@0x1EF1`) — **don't** use those; the
spellbook is the `@0x2425` one (the `[0566][0][0290][0]` landmark match is the discriminator).

## The implementation (the remaining work)

1. **Model the region in `SnapshotTails`** (`Faolan/GameServer/Packets/FullCharSnapshot/SnapshotTails.cs`):
   parse the `@0x2425` `0x3F1`-framed known-list as a typed list of `{ uint Id, byte Type }` records (codec +
   byte-exact round-trip test against `BeachTailsTemplate.bin`, where it's empty, AND against Faolanx's
   2-record list as a golden). Locate it by the `[0566][0][0290][0]` landmark, not a hardcoded offset (the
   offset shifts once the quest portion regenerates).
2. **Generate it** from the character: a `KnownSpellList.Build(IReadOnlyList<uint> spellIds)` →
   `[count=(N+1)*0x3F1] + N×[id, 0xFFFFFFFF, 000000, type=2]`. Source = `Database.GetCharacterSpells(id)`
   (already what `GrantStartingSpells` reads). Optionally add the starting abilities as type-5 records if the
   bar-placement check needs them (verify against Faolanx first).
3. **Splice it into the production snapshot** the same way the quest portion is spliced
   (`RetailLoginReplay` / `BeachTailsBuilder` — see how `BuildBeachQuestPortion` overwrites `@0..1292`):
   replace the empty `@0x2425` region with the generated one (variable length — everything after shifts; the
   tails are length-prefixed in the snapshot frame, so update the outer length too). Graceful fallback to the
   captured (empty) region on any failure, exactly like the quest splice.
4. **Check the spellbook flag.** The doc (`docs/fullplayer-snapshot-reader.md`, region 11) notes a gate
   `*(this+0xe9) != 0` for `SpellBook_t`; the root protobuf already sets `f8 has_spellbook = const true`
   (`fullchar_generation`). Confirm whether the `@0x2425` list is gated by anything that must be flipped when
   it goes from 0 → N records (it's non-empty for Faolanx with the same root, so probably nothing — but verify).

## Test / acceptance (MANDATORY — byte-changing snapshot steps are crash-prone)
- The acceptance gate is the **in-client chat-connect smoke test** (`chat_connect_is_load_confirmation`):
  AgentServer (chat) connecting = the client accepted the snapshot. Golden tests prove the wire, NOT
  client acceptance. Several past snapshot edits crashed on wrong assembly order — expect iteration.
- Concrete repro: server up, clear a fresh necro's loadout
  (`DELETE FROM CharacterShortcuts/CharacterSpells WHERE CharacterId=<necro>`), relog → `EnsureStartingLoadout`
  seeds the 2 necro spells (`0x125D9B`, `0x42CA77`) → they must now be in the spellbook + on the bar AND
  **survive a second relog with NO "learned a new spell" toast** (the current band-aid toasts every login).
- Use the live MCP `list_sessions` + watch `/tmp/faolan_server.log`; Testie (id 33554432) is the necro test char.

## Once it works — clean up the band-aids
Remove the interim grant-timing hacks added 2026-06-20 (they become redundant and the toast goes away):
- `GrantAbilitiesBuilder.GrantStartingSpells`: the `PendingSpellLearns.Add` post-ready re-announce.
- `GrantAbilitiesBuilder.EnsureStartingLoadout`: the pre-snapshot `SendAbilityGrant` loop (only needed because
  the bar couldn't resolve un-listed abilities).
- `PostReadyFollowUp`: the `PendingSpellLearns` drain.
- `NetworkClientSession.PendingSpellLearns`.
Keep the `EnsureStartingLoadout` DB seed (CharacterSpells + the default bar rows) — that part is correct.

## Pointers
- Grammar: `docs/fullplayer-snapshot-reader.md` (region list 1–12; the known-list is NOT region 11's
  stride-0x14 `SpellBook_t` — the learned ids appear only in this 12-byte typed list).
- Per-class snapshot context: `docs/handoff-2026-06-17-perclass-snapshot.md` (class/mana/stats are also
  Strie-inherited; same root issue, different regions).
- Memory: `snapshot_known_spell_list`, `protocol_spell_grant_learned`, `fullchar_generation`,
  `necro_snapshot_decode_state`.
- Decode scripts to extend: ad-hoc Python in this session's transcript; mirror `Captures/scripts/decode_*.py`.
