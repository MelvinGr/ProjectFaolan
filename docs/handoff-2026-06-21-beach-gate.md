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

## Related
Gitea #267 (this), #266 (Casilda done — position + hanging pose), #261 (the Casilda quest chain), #234 (create
cutscene Scry layer — the construct-the-Scry-burst precedent), #210 (burstless world entry).
Memories: [[casilda_184_not_spawn_swap]], [[createworld_cutscene_burst_234]], [[casilda_quest_chain_261]],
[[protocol_quest_state]].
