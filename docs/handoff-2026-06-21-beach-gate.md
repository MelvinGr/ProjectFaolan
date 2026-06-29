# Handoff — Beach progression gate / invisible wall (Gitea #267)

**Goal:** gate the Tortage beach (map 4010) so a fresh character can't run past Casilda / leave the starting area
until the intro quest is done — the retail behaviour. Today nothing stops the player; they walk straight through.

This is a from-scratch feature: **there is no decoded server lever for it yet.** Decode how retail blocks the
beach exit, build + send the barrier at world entry, gate it on quest state, and drop it on completion.

## What gates it (quest state)

- Beach starter quest = **`WorldQuest.BeachStarterQuestId = 10238`** (seeded on first beach entry,
  `RetailLoginReplay`/`BurstlessWorldEntry.EnsureBeachStarterQuest`).
- The Casilda intro CHAIN (the thing you actually complete to "free" the beach) is
  **13479 (kill Tuut) → 13563 (loot bag) → 13480 (talk Casilda) → 12407 (escort)** —
  `QuestProgressionService` (the supersession chain, #261). The gate should lift when the chain reaches its end
  (escort complete) — confirm the exact quest/step that opens the path against a capture.
- Per-character quest state: `IDatabaseRepository.GetCharacterQuests(id)` / `CharacterQuest` (State, GoalProgress).

## Most likely mechanism — the Scry destiny flow (NOT a physical wall)

The Tortage intro is driven by **`ScryScriptIIR` (0xA36D3B74)** — server→client named scripted STEPS the client's
destiny/tutorial flow runs. Already built byte-exact in `Faolan/GameServer/World/ScryScript.cs`
(`ScryScript.BuildStep(handle, token, mode, …)`); steps seen in captures + code: `PlayOrSkip`, `StartFixed3d`,
`WelcomeTutorial`, `ThisIsKalanthes`, the big `character_creation Tortage Beach (server-side)` step
(`RetailCreateWorldEntryBurst.cs`). The intro flow steers the player (e.g. an `ApproachCasilda`-style step) — the
"can't leave yet" confinement is very likely **part of this scripted destiny state**, lifted as steps advance,
rather than a separate collision volume. So **investigate Scry first.**

Alternative if Scry isn't it: a **client-enforced collision volume** (a Scry-adjacent barrier object, or a
playfield trigger that teleports/blocks). Less likely given the intro is already step-driven, but keep it open.

## How to decode it (the RE entry point)

1. In a CREATE capture (the intro walkthrough — e.g. `Captures/Ranochar_mage-necromancer_create.tshark.json`,
   `Blablatest_…_casilda_quest_first_stage`, or any `*create*`), find the window where the player is on the beach
   PRE-quest and walks toward the exit / past Casilda. Look for:
   - a server packet sent when they hit the boundary (a Scry step, a feedback/`FormatFeedbackIIR` "can't go that
     way", a `n3Teleport` snap-back, or a collision/barrier object spawn), and
   - the DIFFERENCE vs after the quest completes (the gate should disappear — diff the Scry step sequence /
     world-object set pre- vs post-completion).
2. The Scry steps decode via `Captures/decode_quest.py` + the `ScryScriptIIR` reader (see `ScryScript.cs` header
   for the protobuf grammar: f1=version 5, f2=mode, f4=stepToken, f5=resources, f6=pair).
3. If it's a barrier OBJECT: it'd be a `WorldObject`/SimpleDynel-class record at the beach edge — decode like the
   other world objects (`WorldObjectBuilder`, `--dump-worldobj`), and note its playfield-instance handling.

## Where it wires in

- **Send:** `Faolan/GameServer/Login/BurstlessWorldEntry.cs` is the sole world-entry path (#210) — add the gate
  packet(s) there (after the world build), conditional on the intro quest being incomplete. Mirror how
  `PostReadyBurst.cs` already sends `StartFixed3d`/`WelcomeTutorial` Scry steps.
- **Lift:** when the chain completes (`QuestProgressionService` — the escort/turn-in step), send the step that
  opens the path (and/or despawn the barrier). Removing it mid-session is the harder half — verify the
  client-removal packet exists in a capture (a "gate cleared" Scry step is the clean form).
- **Quest gate helper:** the marker logic in `WorldBroadcaster` (`ComputeGiverMarkerState`, #241) already reads
  `GetCharacterQuests` for state — same pattern for "is the intro done?".

## Caveats / gotchas

- The destiny flow is **client-scripted from quest state** (same root as Casilda's chained pose, #184/#266) — the
  server's job is to send the right Scry step at the right time, not to simulate a wall.
- Don't confuse with combat/leash (`WorldState.Tick` `NpcLeashRange`) — that's NPC movement, unrelated.
- In-client verification needs the real client (the launcher can't see render/movement gating); use a fresh char.

## Session findings 2026-06-21 (retail-dump comparison — CONCLUSIVE)

Two distinct issues confirmed (not one): (1) Casilda's chains don't render (she hangs from thin air);
(2) the invisible wall confines correctly while chained but doesn't lift when she's freed.

**The wall is NOT a despawnable object — proven against every casilda/create/quest capture.**
- The chain (`004709_Global__decor_chain_hangingwoman`, IIR `0x66AEDD50`) and the **Acheronian Gate** are
  sent as a **consecutive handle pair (N, N+1)**, every session, with **session-volatile handles** — our DB's
  `0x15DD8/0x15DD9` is just one import snapshot, and those exact values appear in NO capture.
- **Neither is ever despawned** — not at quest start, not at first-quest-complete (`…compeltefirstquest`),
  not post-quest (`…after_first_quest_kalanthes`, where they're *re-sent* in the 5-dynel login burst). They're
  permanent decor. (Verified with the correct despawn-handle offset: `n3ToClientQuit` handle = body[12:16],
  not [8:12]=the 0xC350 ack.)
- The ONLY despawns in the whole flow are NPCs (`0xFA015807` — captor mobs killed + interest churn) and loot
  containers (`0xF096251E`). Zero `0x66AEDD50` despawns anywhere.
- ⟹ The wall is **client-side destiny/playfield collision**, lifted by destiny/quest STATE (Scry-driven:
  `StartFixed3d → ApproachCasilda → LeaveCasilda → …`). `LeaveCasilda` is the prime "you may now leave" step.
  Removing the chain/gate is the WRONG lever. The `.despawnobj <handle>` GM command added this session
  (WorldBroadcaster.DespawnWorldObjectForPlayer + GMCommands) is a useful generic tool but is NOT the wall fix.

**Issue 1 (chains missing) — server side fully CLEARED, so it's client mesh:**
- The chain row (20) is sent (passes the `Data != null` filter in GetWorldObjects), built byte-exact (lifted
  columns, WorldObjectBuilderTests), stamped with `BeachPlayfieldId` (the same token the player spawn uses, and
  the player renders), and has NO handle collision (Casilda `0x29735`, chain `0x15DD8`, no NPC↔object overlap).
- ⟹ Remaining cause = the `hangingwoman` mesh (resource `347243` / localized `347573`) not instantiating
  client-side (also why `.despawnobj 0x15DD8` was a no-op — the dynel was never live). NEXT: in-client, check
  whether the OTHER beach world objects render (chests/`Gloves of the Oxmal` `0xF096251E`, Acheronian Gate). If
  none render → the whole burstless world-object path isn't instantiating; if only the chain is missing → it's
  the specific decor mesh (RDB/ContentMirror resource `347243`).

**Still blocked for issue 2 precision:** no capture shows a player crossing the boundary as the wall drops, so
the exact Scry step (likely `LeaveCasilda`) that lifts it is inferred, not proven. A retail capture of walking
out past the freed Casilda would pin it.

## Session 2 (2026-06-21 eve): chains + pose — exhausted server side; CLIENT-CONTENT blocked

Two symptoms chased in-client with a real client (Testietwo, beach):
1. **Casilda's chains don't render** (she floats / stands with no manacles).
2. Her **pose** was wrong (an old hack forced Bcc idle 156196).

**Chains = client-side render issue; every server lever exhausted (all verified in-client, all negative):**
- The chain decor record (`004709_Global__decor_chain_hangingwoman`, IIR `0x66AEDD50`) is **byte-identical** to
  retail: identity, position (362.4,155.4,718.0), rotation all match a capture AND match the `MapWorldBurstEntities`
  entity (handle 0x24124) that **rendered under the old replay path** — only the stored playfield-token bytes differ,
  and those are rewritten to the live token (0x004FE428) at runtime anyway.
- It IS sent (logged: `63 sent, 0 skipped`), with the correct live playfield token (other world objects render with
  it — the lootable `0xF096251E` crate does), the mesh `347243` IS in the local RDB (types 1000028+1010034, LOCAL),
  and there's no handle collision.
- Tried, all no effect in-client: sending the `StartScriptGroupOnClient(385910)` destiny step on login; the
  `SimpleDynelVisualChangeIIR (0xE7F4F8F8)` retail sends to the decor handle; and sending the chain under the exact
  burst handle `0x24124` that rendered under replay.
- KEY CLUE (Melvin): under the OLD verbatim-replay world entry the chains + pose were correct; the replay→construct
  migration lost them. NPCs were also dropped from the burst into `NpcSpawns` ([[npc_model_streaming]]) and Casilda's
  `NpcSpawns` capture is the STANDING one (idle 42024). ⟹ Whatever made the chain render lived in the broader scene
  context the replay provided, not in the chain packet itself — not reconstructable piecemeal from here. The chains
  are a client-content / beach-scene problem (the decor likely only instantiates as part of the full create-world
  scene load), NOT a protocol gap. **Don't re-chase the chain packet — it's byte-perfect.**

**Pose (decided): leave her STANDING, no override.** Dumps show retail spawns her standing (idle 42024 everywhere);
the chained look is the destiny scene (decor + a captive BehaviorCC `161594`, decoded byte-exact: 0x642CD3D6 slot 1,
phase 1, field5 24, bound 0x50/0x01). Both 161594 and the old 156196 only look right WITH the chains, so with the
chains absent she's left standing (retail-exact spawn, no forced anim). Restore 161594 once the chains render.

**Kept this session (retail-accurate):** removed the non-retail 156196 idle override (NpcSpawnBuilder); added the
beach-gated `StartScriptGroup(385910)` + `ApproachCasilda` login Scry steps (PostReadyBurst — genuinely sent on retail
beach login, harmless even though they didn't fix the chains); added a generic `.despawnobj <handle>` GM command
(WorldBroadcaster.DespawnWorldObjectForPlayer). Dropped the dead experiments (BehaviorCC pose send, visual-change,
burst-handle).

## Session 2026-06-29 (REVISED): ShacklesOff is the CITY player-unshackle, NOT the beach wall

In-client result: the `ShacklesOff` wiring below did NOT lift the beach wall (it stayed up), so it was reverted.
Proof it's a city event: `ShacklesOff` appears in only one capture (`Hoxxling_..._login_gameplay2` @5579), which
is **entirely in Tortage city** (maps 4020/4022 from idx 13/308, never beach 4010); the prior grant is the city
quest 10286. The player washes ashore in Slave Manacles (#220) and is unshackled by a SHORT CITY QUEST **after**
the beach→city transition - that's what `ShacklesOff` is. A city packet can't lift a beach wall.

⟹ The beach "invisible wall" is STILL unsolved. Best current theory: it is the destiny confinement that releases
at the beach→city TRANSITION (taking the exit/portal to the city), i.e. entangled with #262 (escort to city), not
a standalone beach "wall down" packet. Need a capture of the moment the player actually leaves the beach toward
the city (the changemap trigger) to pin it.

The reverted (wrong) attempt, kept for the record:

## Session 2026-06-29: invisible wall (issue 2) RESOLVED in code [REVERTED - see above]

The lift is **not** an animation or a despawn (Melvin: "it just happens when you unchain her"). Decoded the full
destiny Scry step sequence from the **Hoxxling gameplay dumps** (`Hoxxling_..._login_gameplay{,2}.tshark.json`):

```
gameplay  (beach intro):  StartFixed3d, StartScriptGroup(385910), WelcomeTutorial,
                          ApproachCasilda(555), LeaveCasilda(1076), CombatTutorial, Level, ApproachCasilda(1828), QuestRewards
gameplay2 (free+escort):  ShacklesOff(5579), StartScriptGroup(2012264)(10375), GoingInside(10460),
                          StartScriptGroup(2012264)(11828), GoingOutside(11901)
```

KEY: `ApproachCasilda`/`LeaveCasilda` are **cycling tutorial-camera waypoints** ("walk up to her" / "walk away to
go kill Tuut") - LeaveCasilda fires at 1076, long BEFORE she's freed, and Approach fires AGAIN at 1828. They are
NOT the wall lift. The unchain/lift is **`ShacklesOff`** - a single mode-2 step (`ScryScript.BuildStep(instance,
"ShacklesOff")`) that fires ONCE immediately after the freeing quest stage completes (gameplay2 idx 5565 quest
supersede -> 5579 ShacklesOff). (My first pass wrongly used LeaveCasilda; corrected to ShacklesOff.)

Wired:
- `WorldBroadcaster.LiftBeachGate` sends `ShacklesOff` on the standard world IIR conversation.
- `QuestProgressionService` sends it the moment the freeing talk-to-Casilda goal completes (the same branch that
  makes her follow, #262) - the unchain moment.
- `WorldBroadcaster.SendBeachGateOnEntry` lifts it up-front at beach entry for a character who already freed her
  (`IsCasildaFreed`: a completed `TalkToNpc`/Casilda goal - data-driven, no hard-coded quest ids), called from
  `PostReadyBurst` so a relog isn't re-confined.

Still open for #267: the **chains don't render** (issue 1, client-content/scene). Needs in-client verification of
the wall drop.

### #262 escort -> city (decoded from the same dumps, for the next session)
The escort to Tortage city is in `gameplay2`: after `ShacklesOff` the player + Casilda head to the city gate; the
`GoingInside`/`GoingOutside` steps + `StartScriptGroup(2012264)` bracket a building (door) transition, and there
are several `IntendedTeleport (0x10D27BC0)` + `n3Teleport (0x5A32F0D7)` map/door moves (idx 10377/11330/11830).

**Quest-id note (corrected):** the **beach** Casilda chain `13479 -> 13563 -> 13480 -> 12407` is the SAME for
every class (verified guardian/necro/herald/assassin/conqueror - see `docs/tortage-intro-quest-ids.md`), NOT
per-class. The `12371/10286/10287/10915/14012/14014/14020/13041` ids are the *post-beach Tortage CITY* destiny
quests (a separate id band), which I briefly mis-read as a per-class chain. Still wire escort arrival by trigger
(reaching the city / door transition), not a hard-coded id - the chain is data-driven (name-keyed) already.

## Related
Gitea #267 (this), #266 (Casilda done — position + hanging pose), #261 (the Casilda quest chain), #234 (create
cutscene Scry layer — the construct-the-Scry-burst precedent), #210 (burstless world entry).
Memories: [[casilda_184_not_spawn_swap]], [[createworld_cutscene_burst_234]], [[casilda_quest_chain_261]],
[[protocol_quest_state]].
