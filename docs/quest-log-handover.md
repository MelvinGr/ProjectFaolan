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

## STATUS (end of 2026-06-14 session) — journal works in-client

The quest journal renders correctly in-client. Two delivery paths, both working:

1. **Snapshot-delivered** — the beach world-entry FullChar snapshot (`0x737A6DF9`) embeds quest **10238
   "Shipwrecked"** (with its full multi-objective journal record) for every character. It is now also
   **persisted** to `CharacterQuests` so the DB matches what the client shows
   (`RetailLoginReplay.EnsureBeachStarterQuest`), and `RestorePlayerQuests` **skips** it so we don't
   clobber the snapshot's richer entry. In-client confirmed: login works, journal shows Shipwrecked with
   **both** objectives.
2. **Server-constructed** — quests the player accepts via NPC dialogue are built from DB rows and sent as
   QuestIIR/QuestFullUpdate/QuestJournal packets (see below). Single-objective; in-client check pending.

### Decode (DONE, byte-exact)
- `Captures/decode_quest.py` round-trips all 65 QuestJournal/QuestFullUpdate/QuestRewards bodies to the byte.
- Every text slot is `00 <u8 len> <ascii>` holding a `<remoteformat id=.. category=.. key=..>` / `<localized>`
  token. **`key` is a deterministic per-`(id,category)` content hash** (harvestable, not a nonce). Categories:
  `50300` area/journal title · `50301` quest title · `50302` summary · `50303` list sub-line · `50304`
  objective · `52000` map/region name · `51000/51200/100` localized.
- **QuestFullUpdate wire grammar fully decoded from the client** (Ghidra, `QuestFullUpdateIIR::ReadSubClass`):
  fixed header (u32×5, 5 strings, u32×4, a **u64**, u32) then **two count-prefixed lists of 18-u32 (0x48)
  objective records** (2nd big-endian-swapped). See the "wire grammar" section below.

### Build + DB + wiring (DONE)
- `WorldQuest.BuildQuestJournal`/`BuildQuestFullUpdate` (+ `RemoteFormat`/`Localized` helpers), byte-exact
  golden-tested vs Casilda 13479 (single objective).
- `QuestDefinition` + `QuestTextRef` tables (migration `AddQuestDefinitions`); `QuestDefinitionExtractor`
  harvests **17 quests** from captures; round-trip golden test feeds a harvested def back through the
  builders to frames 9947+687 byte-for-byte.
- `WorldBroadcaster.SendQuestFullUpdate`/`SendQuestJournal`; `RestorePlayerQuests` (login restore);
  `.quest journal [<id>]` GM command.

### Accept-from-dialogue (DONE, generic — #37)
- `ConversationNode.GrantQuestId` (migration `AddConversationGrantQuest`), harvested generically: a quest is
  granted at the dialogue node where its full-update first appears after an option pick (15 grant nodes
  across 9 NPCs). `ServeScreen` persists the quest active + pushes its journal content.
- `HasQuest(id)` Lua predicate (active OR completed = "accepted") so dialogue gates on acceptance, not just
  completion. Fixed Casilda's greeting state machine: OFFER = unconditional fallback, IN-PROGRESS =
  `HasQuest and not HasCompletedQuest`, DONE = `HasCompletedQuest`.

### REMAINING for #91
- **Multi-objective server-constructed quests** — grammar decoded (Ghidra), build is mechanical (map the 18
  record fields, model + emit the 2 lists). **No consumer yet** (self-delivered quests are single-objective;
  10238 is snapshot-owned) → unblocked but unscheduled.
- `BuildQuestRewards` (the destiny-step driver — decoded/round-tripped, not built; separate concern).
- Multi-quest **journal trailing block** (the "tracked quest" pointer — `SendQuestJournal` ships the captured
  single-quest no-track block).
- RDB-harvest of definitions for quests not in a capture (importer seeds only capture-observed quests).
- In-client verify the **server-constructed** accept→journal path end to end (snapshot path is verified).

### Known mismatch (cosmetic)
`QuestDefinitionExtractor` harvests only the FIRST objective (50304) per quest, so a self-delivered
multi-objective quest would show one objective. Doesn't affect 10238 (snapshot-owned) or 13479 (single).

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

## QuestFullUpdate wire grammar — decoded from the client (Ghidra, 2026-06-14)

The per-quest "stat" fields + objective records that the captures alone couldn't pin are now decoded from
`QuestFullUpdateIIR::ReadSubClass` in `AgeOfConan.exe` (`FUN_00917e20` → the field reader `FUN_0091df50`;
reproduce with `Captures/ghidra/DumpQuestIir.java` + `DumpFns.java`). Read primitives: `FUN_0048f900` =
read u32, `FUN_0048f720` = read u64, `FUN_0048f840` = read string (the `<remoteformat>`/`<localized>`
slots), `FUN_00cc0540` = read a 4-u32 group. Wire order (after the `[subop][ack][player]` framing):

```
u32  version (== 1)
u32 ×5    header scalars            (struct 0xc, 0x10, 0x114, 0x118, 0x14)
string×5  the 5 remoteformat refs   (title 50301, summary 50302, area 50300, region/map 52000 ×2)
u32 ×4    header scalars            (0xec, 0xcc, 0x2c, 0x11c)
u64       header                    (0x20 — the field I'd mistaken for a per-objective "floating timestamp")
u32       header scalar             (0x28)
u32       COUNT-1  (capped at 100)
  record ×COUNT-1 : 18 u32 (0x48 B) — read [0],[1], a u32 pair, a 4-u32 group, a u32 pair, a 4-u32 group, [0xe],[0xf]
u32       COUNT-2  (capped at 100)
  record ×COUNT-2 : 18 u32 (0x48 B) — same shape, read big-endian-swapped
```

So a quest carries **two** count-prefixed lists of fixed 18-u32 records (not one flat block) — which is why
my earlier flat-u32 alignment saw a "moving timestamp" and varying field counts. **Remaining (mechanical):**
map each of the 18 record fields to its meaning by correlating the offsets with the captured values, then
model it + rebuild (extractor harvests both lists; `BuildQuestFullUpdate` emits header + 2 lists). Still no
*self-delivered* multi-objective quest consumes this yet (Casilda's 13479 is single-objective; 10238 is
snapshot-owned), so it's unblocked but unscheduled.

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

## CORRECTED: the phantom journal quest IS in the snapshot (2026-06-14)

The earlier "client local cache, not a server replay" claim was **WRONG** — a misdiagnosis. The beach
(map 4010) world-entry FullChar snapshot (`0x737A6DF9`) **does** carry a quest: its TAILS embed
**quest 10238 (`0x27FE`)**'s journal records — `<remoteformat category="50301">` (title),
`50302` (summary), `50300` (area), `50304` (objective), `<localized category="51000">`. The earlier check
only looked for `34A7` (Casilda's id) and dismissed these journal remoteformats as "dialogue strings".

Source: the snapshot is replayed (largely verbatim) from character **Strie** (captured instance
`35020543` / `0x02165EFF`), who had that quest. `RetailLoginReplay.BuildBeachInventoryPacket` substitutes
the inventory containers but keeps `parts.Tails` verbatim (only Level/vitals patched), so Strie's journal
rides along. The "from-scratch" path is no better: `BeachTailsTemplate.bin` IS Strie's tails, and the
quest region is still TEMPLATE (`BeachTailsBuilder` only generates level/vitals; `UnknownBefore` +
`RemoteFormat50301/50302/50300` are template). So **every** beach character inherits Strie's quest 10238,
regardless of their own `CharacterQuests`.

**Why this matters (confirmed in-client 2026-06-14):** a quest accepted from dialogue now grants + renders
in the log AT ACCEPT TIME (the 0x504B7924 QuestLogList fix, commit `3c9ed1b`), but it VANISHES on relog —
because login delivers the log via the snapshot, not separate packets (retail's in-progress login
`xxx-login` sends ZERO quest packets), and our snapshot is the template (10238 only). Sending a QuestLogList
on login restore was tried and reverted: retail doesn't, so neither do we — the quest belongs in the tails.

### Snapshot quest-region grammar — DECODED (2026-06-14, `Captures/decode_snapshot_quests.py`)

Each quest in the tails is ONE block, structurally identical to the `QuestFullUpdate` (0x63268258) body's
content section (`WorldQuest.BuildQuestFullUpdate`). Per block:

```
[0x00000BD3 ChapterTag]          # grouping word; precedes the FIRST quest of a chapter run (not every quest)
u32 questId · u32 1 · u32 questId · u32 state · u32 0 · u32 N · u32 0x412
<title    00<len><remoteformat id=questId category=50301>>
<summary             remoteformat id=questId category=50302>
<area                remoteformat id=areaId  category=50300>
<objective(s)        ... category=50304 ...>
```

Verified across snapshots: Strie login = 1 block (10238, state=1, N=4); `xxx-login` (in-progress Casilda)
= 2 blocks (10238 state=1 N=4 **+** 13479 state=3 N=3). `state` here is the SAME per-quest field as the
QuestFullUpdate @36 (13479=3, the value the grant fix authors); `0x412` and `0x0BD3` are markers the
builder already emits. So **generating the region = emitting one QuestFullUpdate-content block per active
quest** — we already build that shape.

**Region location (decoded 2026-06-14).** The quest region is at the START of the tails. Template
(`BeachTailsTemplate.bin`, 1 quest): header `[0x18][0x7E2]` → detail block @8 (10238) → … 50304 objective +
51000 localized @1109 → **index `[1009][marker][questId…]` @~1168** → `PFAccessWindow_` @1307. The 2-quest
`xxx-login` is the same shape with a second detail block + the second id appended to the index. The detail
block is byte-identical to the `QuestFullUpdate` body from `questId` onward (verified: 577 bytes match
`BuildQuestFullUpdate`'s output, diverging only in the objective-record tail — acceptTime/markers).

**Build path (preferred — GENERATE, don't splice).** Because the detail block == `QuestFullUpdate` content,
generate it with the EXISTING `WorldQuest.BuildQuestFullUpdate(def, …)` and drop its 21-byte
`[subop][ack][player][0][01]` prefix — no captured-byte splice, re-addressing is free (built with the live
instance). Then: insert the generated block after the existing detail block(s) and append the quest id to
the index id-run. The QFU content already renders in-client (the grant path proved it), so an exact match of
retail's objective tail is not required for the quest to show. Model the region in `SnapshotTails`
(today: single `RemoteFormat50301/50302/50300` + opaque `UnknownBeforeAccessWindowTable`) as a parsed
detail-block list + index, regenerate from `CharacterQuests`×`QuestDefinition`.

**Index framing — fully decoded + self-validated** (`decode_snapshot_quests.py`): the index after the detail
blocks is `[0x000003F1][marker u32][questId u32 × N][N u32][0x000000F1][0x57365154]…`; `N` is the quest COUNT
and equals the id-run length (template=1 → `[27FE][1]`; `xxx-login`=2 → `[27FE][34A7][2]`). Adding a quest =
insert its detail block immediately before `0x000003F1`, append its id to the id-run, bump `N`.

**Status: blocked on in-client (the byte-accounting last mile).** The build path is clear and reuses tested
code, BUT the per-quest detail block must serialize to the EXACT length/framing the tails parser expects, and
a 1-byte error mis-frames the rest of the tails (access-window table) → broken FullChar → chat/world-load
regression (`protocol_chat_connection_regression`). Open accounting to pin (needs a live client to prove, so
build behind a default-off flag and bisect in-client): the tails 13479 block is **879 B** vs
`BuildQuestFullUpdate`'s content **880 B** — i.e. the block is QFU-content WITHOUT the trailing `0x00`
separator (line 189); confirm whether consecutive blocks carry a separator, and that the objective-record
section (which diverges from the live QFU at byte 577 — acceptTime/markers) parses identically in the tails
reader. The `marker` word (`0x7E2` vs `0x0BD3`) and 51000-localized id (`id=11561`) only matter for byte
fidelity, not for the quest to show. **Recommended:** implement `SnapshotQuestInjector.Inject(tails, blocks)`
(byte surgery: insert blocks + extend index) + a `WorldBroadcaster` block builder (QFU-content minus the
17-byte prefix), wire behind `FAOLAN_SNAPSHOT_QUESTS=1`, then bisect block length in-client.

## Related

`docs/archive/quests-and-combat.md` (first-pass dissection), `memory protocol_quest_state`,
`memory protocol_conversation_routing`, `docs/aoc-client-protos.proto`.
