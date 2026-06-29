# Handoff (ARCHIVED) — Beach progression gate / invisible wall (Gitea #267) — RESOLVED

**Status: RESOLVED + verified in-client 2026-06-29. Gitea #267 closed.** Kept for the decode trail; the working
behaviour lives in the code + [[casilda_destiny_scry_steps]]. The long dead-end log (ShacklesOff, "client-content
scene blocker", "destiny collision unsolved") was wrong and has been pruned — see the bottom so it isn't re-tried.

## What the "invisible wall" actually was

The beach exit was gated by the **collision of the Casilda chain decor** (`004709_Global__decor_chain_hangingwoman`,
SimpleDynel IIR `0x66AEDD50`). It is permanent decor (never despawned), present while Casilda is chained, and reads
as an invisible barrier. It is **not** a Scry confinement, not a separate barrier object, not a despawn.

**Lift:** the instant the player frees Casilda (talk goal, stage 13480), retail sends a
`SimpleDynelVisualChangeIIR (0xE7F4F8F8, state 5)` to the chain dynel, dropping its collision (decoded from
`Hoxxling_..._login_gameplay` @2053). Wired:
- `SimpleDynelMessageCodec.BuildVisualChange(handle, state)` — the 21-byte record.
- `WorldBroadcaster.DropBeachWall` — looks up the beach chain world-object by name and sends state 5.
- `QuestProgressionService` fires it on the talk-to-Casilda free; `SendBeachGateOnEntry` drops it up-front for an
  already-freed relog.

## The chains themselves (the other half of #267)

The visible chains are **Casilda's slot-18 worn equipment** (`tpl402981`), NOT the `decor_chain_hangingwoman` decor
and NOT a client-content gap. Proven by capturing the live loopback bytes: our decor packet is byte-identical to
retail (only handle + playfield token differ) and renders fine. Casilda had been imported with her 3-slot
*following* loadout (no slot 18); the importer now keeps the richest per-NPC loadout (`equipByName` in
`BuildBurstTruthSpawns`) and her slot-18 row was added to `faolan.db` (commit feb236a). Her chained pose is the
captive BehaviorControlCenterIIR (slot 5, behaviorId 159428, commit 4d23682); facing pinned to her create-dump
rotation (commit 47ceea1).

Also confirmed while debugging: the **playfield token does not gate rendering** — our whole session uses
0x004FE428 while retail uses 0x01EA985C, and everything renders. Internal consistency is what matters.

## Pruned dead theories (do NOT re-attempt)
- `ShacklesOff` lifts the wall — WRONG. It is the PLAYER's manacle removal in Tortage CITY (a separate short city
  quest after the beach→city transition), appears only in city captures (maps 4020/4022). Reverted.
- The chains "can't render = client-content / beach-scene blocker" — WRONG. They are equipment (above).
- `ApproachCasilda`/`LeaveCasilda` Scry steps are the wall lift — WRONG, they are cycling tutorial-camera waypoints.
- A forced idle anim 156196/161594 makes her look chained — WRONG without the chains; the real look is the BCC pose
  + the slot-18 equipment.

## Related
Gitea #267 (closed), #184 (Casilda chained — closed, done via equipment+pose), #261 (the chain), #262 (escort to
city, open). Memories: [[casilda_destiny_scry_steps]], [[casilda_184_not_spawn_swap]], [[casilda_quest_chain_261]].
