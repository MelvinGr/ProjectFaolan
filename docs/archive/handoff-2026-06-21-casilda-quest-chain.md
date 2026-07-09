# Handoff — Casilda / Tortage intro quest chain (Gitea epic #261)

## STATUS 2026-06-29 (chain VERIFIED end-to-end in-client)

The full beach chain `13479 kill Tuut -> 13563 loot key -> 13480 talk/free Casilda -> 12407 escort` runs and is
verified in-client. Resolved since the 2026-06-21 status below:
- The "BLOCKED on spawns" caveat is gone — Casilda + Tuut spawn from the DB and the chain drives end-to-end.
- **Quest markers** correct through the whole chain: "!" fresh giver, **"?" on Casilda after looting Tuut's key**
  (the active talk-stage marker — `ComputeGiverMarkerState` only looked at the giver quest 13479, Completed by
  then; now it also returns "?" for an active TalkToNpc goal targeting the NPC, pushed via
  `RefreshQuestMarkerByName` on supersession — commit 161542b), cleared after talking.
- Casilda renders **chained** (captive BCC pose slot 5/159428 + her slot-18 chains item tpl402981) with correct
  facing, un-chains on the talk-free, and **follows** (#262 follow done; arrival completion still open). The beach
  "invisible wall" (#267) drops on the talk-free. Stage dialogue (#264), loot render (#263), corpse-bag mesh (#265)
  all closed.
- **Remaining:** stage-4 escort ARRIVAL completion (reach city, complete 12407) — Gitea #262.

See [[casilda_quest_chain_261]], [[casilda_destiny_scry_steps]], docs/archive/handoff-2026-06-21-beach-gate.md.

## STATUS 2026-06-21 (chain engine + stages 1-3 wired)

Build-plan steps 1-4 are DONE (server builds, 576 tests pass, faolan.db re-seeded):

- **Chain model** — `QuestDefinition.NextQuestId` (supersession link) + `QuestGoalDefinition.GoalType`
  (`QuestGoalType` Kill/LootFromNpc/TalkToNpc/Escort), migration `QuestChainSupersession`.
- **Supersession engine** — `Faolan/GameServer/World/QuestProgressionService.cs`: on a stage's goal completion it
  emits the captured sequence (QuestGoal + QuestIIR 2->4 + QuestFullUpdate(next)) and flips state (complete N,
  grant N+1 Active with its goal seeded). Reaches the three quest packets through the new narrow `IQuestOutbound`
  port (WorldBroadcaster implements it). Pure match predicate `QuestProgress.Advances` is unit-tested.
- **Triggers wired** — stage 1 KILL (`CombatService` -> `AdvanceKillGoals`, replaces the #47 turn-in for chained
  quests, kept for chainless ones), stage 2 LOOT (`LootService` -> `AdvanceLootGoals`, matched on the corpse bag's
  SOURCE NPC name since the key item is RDB content), stage 3 TALK (`ConversationService` -> `AdvanceTalkGoals`).
- **Importer** — `SeedCasildaChain` seeds the four stages' NextQuestId links + goal types/targets. (Also fixed a
  pre-existing `Extra6` typo 0x542D5354 -> 0x54375554 in `SeedCasildaQuestSnapshot` that a re-import surfaced.)

**NOT done / remaining:**
- **Stage 4 ESCORT (12407)** — follower AI + arrival completion. Own Gitea sub-ticket; needs a movement burst +
  a short follow-up capture of reaching the city (this capture has the escort GRANT but not its completion).
- **In-client verification is BLOCKED on spawns**: Casilda + Scavenger Tuut are NOT in the `NpcSpawns` table
  (they're scripted in the create-world Tortage intro, #234/#185), so the chain can't be driven end-to-end in the
  open world yet. The engine + wire are byte-exact/unit-tested and ready; flagging the epic `needs-testing` once
  the intro NPCs spawn. Step 6 (per-stage toasts) is client-rendered from QuestIIR(4) so likely already covered.

---


Decoded 2026-06-21 from `Captures/Blablatest_mage-necromancer-create_casilda_quest_first_stage.tshark.json`
(+ screenshots `/home/melvin/Pictures/Screenshots/{kiled_tuut,free_casilda}.png`, video
`Screencast_20260621_112521.webm`). This supersedes the single-stage assumptions in #47 (closed) and the
turn-in approach in #260 — quest **13479 is not a lone kill**, it is a multi-stage chain.

## The chain

Each stage is its **own quest id**; completing a stage *supersedes* it with the next:

| Stage | Goal / Task (in-client text) | Trigger | Quest id | → next |
|------:|------------------------------|---------|----------|--------|
| 1 | "Kill the Scavenger" / "The Scavenger" | kill **Scavenger Tuut** | `13479` | `13563` |
| 2 | "Get the Scavenger's Key" | **loot the key** from Tuut's loot bag | `13563` | `13480` |
| 3 | "Free Casilda" / "Freeing the Captured" | **talk to Casilda** | `13480` | `12407` |
| 4 | "Escort Casilda to the city" | Casilda **follows** the player | `12407` | (next) |

## Wire mechanism (supersession)

Grant: `QuestFullUpdate(13479)` at capture fr1088. Every stage completion is the **same sequence**
(verified at fr1354–1358 for 13479, fr1446 for 13563, fr1605 for 13480):

1. `QuestGoal (0x0774C8C1)` — the completing quest's objective counter hits its required count.
2. `QuestIIR (0x97E02314)` state **2** then state **4** — the journal completion (the existing
   `WorldQuest.BuildQuestIir` + `StateActive`/`StateCompleted`).
3. `QuestFullUpdate (0x63268258)` for the **NEXT** quest id — this is the supersession; the client swaps the
   displayed quest to the next stage and sets it active.
4. (+ any `FormatFeedback (0x8D5A8B6B)` for incidental events — e.g. killing Tuut dinged L2, so the
   AttributeChanged level-up toasts ride here; the "Goal Completed" / "Quest Task finished" toasts are
   client-rendered from the QuestIIR(4) + goal data.)

So the per-stage server action is: **complete stage N (QuestGoal + QuestIIR 2→4) → grant stage N+1
(QuestFullUpdate + set Active)**.

## What exists today (the foundation to build on)

- `WorldQuest` (`Faolan/GameServer/World/WorldQuest.cs`): `BuildQuestIir(handle, questId, state)` with
  `StateActive = 2` / `StateCompleted = 4`; `BuildQuestGoal`; `BuildQuestFullUpdate`. Senders on
  `WorldBroadcaster`: `SendQuestIir`, `SendQuestGoal`, `SendQuestFullUpdate`.
- `CombatService.AdvanceKillQuests` (`Faolan/GameServer/Combat/CombatService.cs`): the only goal-type today
  (kill). On a kill it advances `GoalProgress`, pushes `QuestGoal` + `QuestIIR(StateActive)` to refresh the
  HUD, and leaves the quest **Active** (objective-complete). **Interim** `ac811e8`.
- `ConversationService.TryTurnInQuests` (`Faolan/GameServer/Dialogue/ConversationService.cs`): on opening a
  quest GIVER's greeting with that quest's objective done, marks it Completed + QuestIIR(2→4). **This is the
  single-stage interim** — for the chain it must instead *supersede* (see below), and the talk itself is a
  goal type (stage 3), not a generic turn-in.
- `WorldBroadcaster.ComputeGiverMarkerState`: `!` when not held, `?` when objective done + still Active,
  none otherwise. Generalises fine to the chain (the active stage with goals-done shows `?`).
- `QuestDefinition` / `QuestGoalDefinition` (`Faolan.Core/Database/Tables/`): per-quest goal definitions
  (`RequiredCount`, server-side `TargetName`). `CharacterQuest` holds live state (State, GoalProgress,
  GoalRequired, GoalTargetName). Grant path: `ConversationService.PrepareQuestGrant`.

⚠️ The interim `ac811e8` (complete-on-talk-to-giver) is WRONG for chained quests — killing Tuut should
supersede 13479→13563, not leave 13479 for a turn-in. Rework it as part of stage 2 below.

## Build plan (staged — see #261)

1. **Multi-stage chain model.** Add a `NextQuestId` (supersession link) + a `GoalType`
   (kill / loot-item / talk-npc / escort) to `QuestDefinition` (or a new `QuestChain`/`QuestStage` table) +
   migration. Import the chain `13479→13563→13480→12407` and each stage's goal from the capture's
   `QuestFullUpdate` refs (quest text/goal names are client-RDB by id — no literal text needed). The
   importer already seeds 13479's kill goal (`SeedKillQuestTargets`); extend it for the chain.
2. **Supersession engine.** Generalise `AdvanceKillQuests` into a per-goal-type completion path: on a stage's
   goal completion, send `QuestGoal + QuestIIR(2→4)` for the completed stage, then `QuestFullUpdate(next)` +
   set the next `CharacterQuest` Active. Replace the `TryTurnInQuests` single-stage logic.
3. **Loot-item goal (stage 2).** Hook the loot path (`TryLootAll` / item-grant in the loot interaction,
   see [[protocol_loot_interaction]]) to advance/complete a loot goal when the target item (the scavenger's
   key) is looted.
4. **Talk-npc goal (stage 3).** Hook `ConversationService` (greeting open with the active talk-stage for that
   NPC) to advance/complete a talk goal (free Casilda) → supersede to the escort stage. This is where the
   #260 reward-dialog content folds in.
5. **Escort / follow (stage 4).** Largest piece — an NPC follow-the-player AI + arrival completion (Casilda
   escort). Likely its own sub-ticket; needs a movement/AI burst (cf. #190) for the follower.
6. **Per-stage notifications.** The "Goal Completed" / "Quest Task finished" toasts (decode the FormatFeedback
   form for quest events vs the AttributeChanged level-up form already in the capture).

## Open questions / capture gaps

- The "Goal Completed" / "Quest Task finished" toasts: are they distinct `FormatFeedback` records, or
  client-rendered from QuestIIR(4)? The capture's FormatFeedback at fr1357 is the L2 AttributeChanged ding —
  confirm whether a quest-specific feedback record also rides each completion.
- `QuestFullUpdate` field layout for the superseding quest (area/marker/goal text refs) — extend
  `WorldQuest.BuildQuestFullUpdate` if the chain stages need fields the current builder doesn't carry.
- Escort completion trigger (arrival at the city) — needs a destination/zone check; the committed capture has
  the escort GRANT (12407 active) but not its completion, so stage 4's *arrival* may need a short follow-up
  capture. Stages 1–3 are fully captured.

## References

- Capture (committed, both `.pcapng` + `.tshark.json`):
  `Captures/Blablatest_mage-necromancer-create_casilda_quest_first_stage.*` — covers stages 1–3 end to end
  (kill → loot key → free Casilda) plus the **grant** of stage 4 (escort, 12407 goes active at fr1605). The
  only piece NOT in this capture is the escort *arrival* completion (12407 → next); a short follow-up capture
  of reaching the city would close that. Stages 1–3 need no further capture.
- Screenshots: `/home/melvin/Pictures/Screenshots/{kiled_tuut,free_casilda}.png`
- Related: `docs/subsystems/knubot-conversation-packets.md`; memories [[casilda_quest_chain_261]],
  [[quest_kill_goal_47]], [[protocol_quest_state]], [[protocol_loot_interaction]].
