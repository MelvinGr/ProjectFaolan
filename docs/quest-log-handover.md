# Quest-log decode — handover

**Goal:** make the in-client **quest journal** (the J panel: a list of quests, each with a title, a
description, and its objectives) render from server-constructed packets, the way the backpack/AH now do.
Today the server drives quest *state* (a quest's active/complete flag + a kill counter) byte-for-byte, but
the journal *content* — the titles/descriptions/objective text the player reads — is not yet built; on the
beach we send **no quest at all**, so a fresh character's journal should be empty (any entry you see there
is the retail client's own local cache, see "Non-issue" below).

This is the **next priority** (Gitea **#91**). M4 (Casilda spawn swap, **#37 / new high-prio issue**) and
cross-player/shared-zone quest features (**#101**, `docs/archive/server-architecture-roadmap.md`) sit
behind it.

---

## Progress (2026-06-14): grammar decoded + journal builders byte-exact

**Decode DONE.** `Captures/decode_quest.py` pulls every QuestJournal / QuestFullUpdate / QuestRewards
body out of the three captures, tokenizes the framing, and **round-trips all 65 packets to the byte**
(parse → re-serialize → assert == original). The grammar below is closed.

Key finding: every text slot is a `00 <u8 len> <ascii>` record holding a
`<remoteformat id=.. category=.. key=..>` / `<localized ..>` token. **The `key` attribute is a
DETERMINISTIC content hash** — byte-identical for a given `(id, category)` across characters, sessions
and captures — so it is harvestable data, not a per-send nonce. category map: `50300` region/journal
title · `50301` quest title · `50302` quest summary · `50303` list sub-line · `50304` objective text ·
`52000` map/region name · `51000/51200/100` localized strings.

**Build DONE (framing).** `WorldQuest.BuildQuestJournal` + `BuildQuestFullUpdate` (+ `RemoteFormat` /
`Localized` record helpers), each **byte-exact golden-tested** against the Casilda quest 13479
(`QuestTests.BuildQuestJournal_MatchesCasildaCapture` vs frame 9947;
`BuildQuestFullUpdate_MatchesCasildaCapture` vs the fresh-offer frame 687). Builders take the text
records + scalar fields as parameters; the framing/structure is server-owned. The structured-header
constants (`0x50`/`0x64`/`0x7e2`/`0x0c`/`0x412`) are reproduced as a documented template and locked by
the golden — not yet semantically pinned.

**REMAINING for #91:** (a) a **quest-definition + content table** (questId → areaId, the
`(id, category, key)` text refs, goalId, map+x+y marker) harvested by the importer from these captures
+ RDB — *construct, don't replay*; (b) `BuildQuestRewards` (the destiny-step driver — decoded, not yet
built; separate concern, see below); (c) **wire** `SendQuestJournal` / `SendQuestFullUpdate` into
`WorldBroadcaster.RestorePlayerQuests` (login) + the accept/turn-in path; (d) in-client verify the
J-panel renders (the `key` hashes are the render-critical bit — wrong keys ⇒ blank text).

---

## What is already DONE (state layer)

`Faolan/GameServer/World/WorldQuest.cs` — byte-exact builders, golden-tested vs capture (`QuestTests`):

| Packet | Sub-op | Size | Meaning | Status |
|---|---|---|---|---|
| **QuestIIR** | `0x97E02314` | 29 B | add / update / remove a quest (state code; `2` = active) | ✅ built |
| **QuestGoal** | `0x0774C8C1` | 33 B | one objective's progress counter | ✅ built |

Plumbing on top of those:
- `CharacterQuest` table (`characterId, questId, State{Active,Completed}, GoalProgress/Required,
  GoalTargetName`); repo `Get/Set/RemoveCharacterQuest`.
- `WorldBroadcaster.RestorePlayerQuests` re-sends active quests on world entry (DB-driven; **0 rows for a
  fresh char → nothing sent**).
- Kill tracking: `QuestProgress.AdvancesOnKill` → `GoalProgress++` → `SendQuestGoal` → completes at
  `GoalRequired`.
- `.quest add|remove|list|complete` GM commands (the test bootstrap).

So: a quest *appears and tracks* once it's in the DB. What it **cannot** do yet is deliver the journal
*text/record* so the J-panel shows a real title/description/objective — that's QuestFullUpdate +
QuestJournal + QuestRewards below.

---

## What is MISSING (the journal-content layer to decode)

Three download (`s→c`) record packets carry the journal content. All share the GameServer `0x2000` framing
`[bodyLen4][subOp4][ack=0x0000C350][playerHandle4]…`; **text is never literal** — each text slot is a
`<remoteformat id="…" category="…" …>` token the client resolves against its RDB by `(id, category)`.

### QuestJournal `0x79F8C7A1`  (the journal entry list — title line per quest)
```
79f8c7a1 0000c350 01316d8a 01 000004 1e 00 64 <remoteformat id="1054" category="50300" …></remoteformat>
                                           ^^   00 64 <remoteformat id="4010" category="50300" …> …
```
- `category=50300` = journal-title text refs. Several remoteformats concatenated → the journal list.
- The `00 64` between records and the `01 000004 1e` prologue (count? total length?) are the framing to
  pin down.

### QuestFullUpdate `0x63268258`  (one quest's full detail — description + objectives)
```
63268258 0000c350 01316d8a 00000000 01 0000 27fe  01 0000 27fe  01 00000000 00000000 0400000412 00 65
   <remoteformat id="10238" category="50301" …></remoteformat> …
```
- `0x27fe` (10238) repeats and is also the remoteformat `id` → this is the quest's record id, carried as a
  raw field **and** as the text `id`.
- `category=50301` = quest description; expect objective-text refs too. The leading
  `01 0000 27fe 01 0000 27fe 01 …` block is the structured header (ids + flags/counts) to decode.

### QuestRewards `0xA36D3B74`  (mis-named — it's the destiny/tutorial STEP driver)
```
a36d3b74 0000c350 0131720b 00000000 1f 08 05 10 02 18 00 22 0f "ApproachCasilda" 2a 00 32 04 08 00 10 00
   …and: "WelcomeTutorial" / "CombatTutorial" / "StartFixed3d" / token="playorskip(72)|
    onclientconnected(1)|NEW_character_creation Slave Galley (server-side)" category="50023"
```
- Body is **protobuf-shaped** (`08/10/18/22/2a/32` field tags) wrapping a **named scripted-step token**, not
  an item/xp/money reward as the opcode-catalog name implies. The tokens observed —
  `WelcomeTutorial`, `CombatTutorial`, `ApproachCasilda`, `StartFixed3d`, `NEW_character_creation …` — are
  **Tortage destiny/tutorial step markers** (see #149 day/night destiny phasing). `ApproachCasilda` fires
  immediately before Casilda's quest grant. Decode the field tags against `docs/aoc-client-protos.proto`;
  this packet drives the scripted intro sequence, the reward side is a separate concern.

---

## Captures (decode source)

Primary, rich in all five packet types:
`Captures/account-stricken998-character-strickenbla-create-random-actions-combat-levelup-questcomplete.tshark.json`
(4903 pkts; QuestIIR×6, QuestGoal×3, QuestFullUpdate×5, QuestJournal×3, QuestRewards×50).

**Fresh-offer / grant flow** (the previously-missing capture):
`Captures/account-stricken997-character-randochar-login-get-casilda-quest.tshark.json` — a brand-new char
logs in and gets Casilda's quest. Shows the destiny-step driver in action:
`WelcomeTutorial`/`CombatTutorial` → `ApproachCasilda` (QuestRewards `0xA36D3B74` tokens) → talk to Casilda →
**QuestFullUpdate `0x63268258` with quest id 13479** (remoteformat id=13479 category=50301). This is the true
fresh-offer Casilda dialogue + first-quest grant.

Accept-side (one QuestFullUpdate at accept):
`Captures/account-faolan-character-necrol80-login-changemap-accept_quest.tshark.json`.

**Quest naming (corrected 2026-06-14):** quest **13479 (`0x34A7`) is the "Casilda" quest** — the one Casilda
gives — goal id `0x1670` (5744). It is *not* "Scavenger Lackey" (that name was wrong in earlier notes/issues;
**"Scavenger Lackey" is a beach *mob* name**, FA015807 spawns at frames 49/54/355/392 of this capture). The
"**Shipwrecked**" quest is given after the **Kalanthes** conversation; its quest id is not yet in a capture
(the `…kalantes_chat` capture only reaches the `WelcomeTutorial`/`StartFixed3d` tutorial-token stage).

**Quest-triggered spawn (→ M4, #184):** the named NPC **"Scavenger Tuut"** (FA015807, handle `0x00056DB0`)
spawns on the beach **in the same dialogue turn as the quest-13479 grant** — frame 687 QuestFullUpdate
(0x63268258) → frame 689 the Tuut spawn. So an active Casilda quest *adds* a per-player beach NPC. M4's
conditional-spawn model (chained↔unchained Casilda) must also cover "spawn Tuut while 13479 is active". The
simplest path is including it in the player's login world-burst gated on quest state (sidesteps the pinned
mid-session-spawn blocker, `memory protocol_midsession_spawn_blocked`).

The `.tshark.json` records expose `BodyHex` per packet; **`BodyHex` = `[len4][subOp4]…`** (sub-op at byte 4,
not byte 0 — that tripped up the first dump). Filter on the sub-op hex.

---

## Method (the house style — `CLAUDE.md` "capture workflow")

1. **Decode first.** Write `Captures/decode_quest.py`: pull every QuestJournal / QuestFullUpdate /
   QuestRewards body out of the two JSONs, parse the record framing to the packet end, and assert the
   grammar (record count, per-record length, the remoteformat `(id,category)` slots). No C# until the
   grammar closes to the byte.
2. **Build second.** Formalize `WorldQuest` into codecs with **Parse** (the existing `QuestTests` only
   covers Build) + new `BuildQuestJournal/BuildQuestFullUpdate/BuildQuestRewards`, each proven by a
   byte-exact golden test against the captured bytes in `Faolan.Tests`.
3. **Model the content.** Quest definitions (title/desc/objective remoteformat ids per quest id, goal ids,
   reward refs) belong in the **DB**, harvested from RDB (`memory rdb_extraction`) + these captures by the
   importer — *construct, don't replay* (`memory world_packets_construct_not_replay`). The client renders
   the text from its RDB by id; we only ship ids + state.
4. **Wire it.** `RestorePlayerQuests` (login) and the accept/turn-in path send the journal records alongside
   the existing QuestIIR/QuestGoal. Accept-from-dialogue is the pinned Lua/tag VM seam
   (`memory protocol_conversation_routing`, `protocol_quest_state`) — the dialogue itself already routes
   (M1–M3 of #37).

---

## Wiring points (where this lands in the server)

- Builders: `Faolan/GameServer/World/WorldQuest.cs` (extend).
- Send seam: `Faolan/GameServer/World/WorldBroadcaster.cs` (`RestorePlayerQuests`, `SendQuestIir`,
  `SendQuestGoal` — add `SendQuestJournal` etc.).
- State/model: `CharacterQuest` table + a new quest-definition table (importer-seeded).
- GM bootstrap: `.quest` in `Faolan/GameServer/GMCommands.cs`.
- Tests: `Faolan.Tests` (`QuestTests`).

## Non-issue to remember (don't chase it again)

A fresh beach character's journal showing "1 quest" is **not** a server replay. Verified 2026-06-14: the beach
(map 4010) world-entry FullChar snapshot (`0x737A6DF9`) carries **no** quest in any encoding (no `34A7`, no
QuestIIR markers — only the `PFAccessWindow_` table + a few dialogue-format XML strings), the beach burst has
no QuestIIR records, Casilda's conversation packets carry no quest grant, and `RestorePlayerQuests` reads 0
rows. Such an entry is the **retail client's local journal cache** for that character slot — confirm with a
brand-new character.

## Related

`docs/archive/quests-and-combat.md` (first-pass dissection), `memory protocol_quest_state`,
`memory protocol_conversation_routing`, `docs/aoc-client-protos.proto`.
