# Handoff — Create-character flow reconstruct (#234)

**Symptom (in-client, 2026-06-22):** create a new char → the destiny cutscene plays as a **black screen with
correct audio** → after the cutscene the client **disconnects all connections**, then **reconnects and hangs**.

## Root cause (capture + log confirmed, this session)

Loopback capture of a full create run (`Teststet`) + server log. Three GameServer (:7014) connections:

| Port | Role | Notes |
|---|---|---|
| editor | character editor | customize + name; ends with `0x2002` enter-world handoff |
| world (cutscene) | `SendCreateWorldSceneBurst` | the 5-chunk verbatim burst; cutscene plays; `IntroDone` fires |
| reconnect | burst-less entry | client reconnects on its own → `SendWorldEntry` → **hangs** |

The create-world burst **is** a near-complete world entry — it contains `1×0x737A6DF9` snapshot, `59×FA015807`
spawns, `186×MoveTask (0x96C46740)`, `5×WorldObject (0x66AEDD50, incl. the hangingwoman chains)`, `6×loot`,
`9×vitals`, `1×MapChange`, plus the cutscene Scry scripts (built via `ScryScript.BuildStep/BuildStartScriptGroup`,
not byte arrays). **But every world-content record is VERBATIM for the captured `hoxxling` session, not the live
created char.** So after the cutscene the client holds the wrong character's avatar/inventory/world → it drops
all connections → reconnects to load its real char → the burst-less entry runs but **hangs** (a second, separate
post-create reconnect bug).

Sequence in the log: `SCENE burst → ClientLoadingDone → StartScript 7088774 → StartScript 7124502 → IntroDone`
(cutscene done) → **all 3 connections disconnect** → reconnect → `Burst-less world entry … sent 68 packets` → hang.

This is the long-standing `create_burst_construction` gating: *"chars can't DB-source (un-lifted pre-IIR
#185/#54)"* — the burst's characters are verbatim. Confirmed.

## The plan — integrate, don't re-RE

**Key leverage:** `BurstlessWorldEntry.SendBurstlessWorldEntry` ALREADY constructs the full live-char world
(MapChange → beach, world objects, player spawn, `0x737A6DF9` snapshot, vitals, NPC interest-streaming) and is
the PROVEN working path for existing-char logins. The create-world flow should = **that constructed live-char
world + the cutscene Scry-script overlay**, replacing the verbatim burst.

Steps (each in-client testable):
1. **Route the create-world entry through the constructed world.** Make `SendCreateWorldSceneBurst` build the
   live char's world via the burst-less construction (extract the construction out of `BurstlessWorldEntry` into a
   shared method both paths call, or have the create path call it). Drop the verbatim world-content packets (the
   59 spawns / snapshot / 186 MoveTasks / vitals / MapChange) — they're superseded. Keep the editor→world handoff.
   *Goal: created char lands on the beach as ITSELF, no avatar mismatch, no disconnect.*
2. **Overlay the cutscene.** Re-add the destiny Scry sequence (the `character_creation Tortage Beach` playorskip,
   `ThisIsKalanthes`, `Kneel`, `ApproachCasilda`) on top of the constructed world, paced by the client callbacks
   (`ClientLoadingDone → StartScript → … → IntroDone`) as today. These are already `ScryScript.BuildStep`-built
   and take the live `id`, so they re-address cleanly.
3. **Kalanthes / scene actors** come from the constructed NPC set (interest-streamed beach NPCs), not verbatim —
   so they're the live session's. Cinematic actor-tagging (`TagCharacterForCinematicScriptCommand`, see
   `docs/create-cutscene-camera-paths.md`) binds the cutscene to the live Kalanthes handle.
4. **Delete the self-reconnect.** Once the world is established on the cutscene connection, the client shouldn't
   need to drop+reconnect. If it still does, the post-create reconnect must arm the post-`0x205F` follow-up
   (`0x2024 ReadyForPlayScreen`) like the teleport-reentry path (`GameServerListener` ~line 507) so it doesn't hang.

## Gotchas
- Don't double-send MapChange/snapshot (the constructed entry replaces the verbatim ones — remove, don't append).
- The chains (`0x66AEDD50` hangingwoman) ARE in the create scene burst (chunk 0) — the create flow is the scene
  context where they render in retail, so fixing this also informs the existing-char chains issue (#267).
- Editor (drowning, playfield 4000) cutscene is client-autonomous + already correct; this work is the WORLD
  (beach 4010 / Kalanthes) connection.
- Camera-path/playfield theory is RESOLVED (`docs/create-cutscene-camera-paths.md`); not a blocker.

## Status (updated 2026-06-22, in-client tested)

**Step 1 done + tested** (route create-world entry through burst-less, at the GameServerListener call site;
verbatim burst + chunks kept static for the golden):
- **Crash fixed for verified classes.** A created Bear Shaman no longer crashes — the burst-less spawn is valid
  (server-side: the spawn parses; the earlier crash was the per-class closer, below).
- **Per-class closer crash CONFIRMED** (#250): created chars of the 7 BORROWED-closer classes crash the client
  (`CoreCharacterFullUpdateIIR.cpp(1086) !a_cBlob.HasFailed()`): Assassin, Ranger, Conqueror, Dark Templar,
  Priest of Mitra, Tempest of Set, Herald of Xotli. Only the 4 with REAL captured closers work: Barbarian,
  Guardian, Bear Shaman, Necromancer. The 7 need their real closer (per-class, race-independent) — from each
  class's create capture, or by RE'ing what the closer tag indexes so they can be derived. SEPARATE follow-up.
- **New blocker (verified classes): black-screen hang, no loading screen.** The beach/Kalanthes cutscene is
  SERVER-DRIVEN: the client fires `ClientLoadingDone → StartScript → StartScript → Move → IntroDone` and the
  server must answer each (`SendNextCreateWorldChunk`) to advance it. Step 1 gated those chunks off (burst-less
  set `WorldEntered`), so the client sits in the destiny-cutscene state with nothing driving it → black hang.

**Step 2 DONE + tested** (`CreateWorldCutsceneScripts.cs` — `SendCreateWorldWithCutscene` + `SendCreateCutsceneStep`,
wired at the GameServerListener call site + HandleCommandPacket callbacks; `SendNextCreateWorldChunk` retired from
the live path, kept for the golden):
- The create-world entry = handshake + burst-less live-char world + the destiny cutscene's Scry-step overlay
  (scripts only: begin marker, `character_creation Tortage Beach` mode-6 scene-load, script groups 48126/385910,
  then per-callback `ThisIsKalanthes` / `7088774`+`WelcomeTutorial` / `7124502` / `Kneel` / `StartFixed3d`+`7727158`).
- **In-client (verified-class Bear Shaman):** black screen GONE, audio correct, the cutscene PLAYS (slave-galley
  interior visible, then underwater), the full callback sequence fires `ClientLoadingDone→StartScript→StartScript→
  Move→IntroDone`, and the char is in-world (sends Move). HUGE step up from crash/hang/black.

**Remaining blocker — post-cutscene CONTROL RELEASE (un-referenced):** the char ends **underwater at the drowning
cutscene's end position (~260, 4.65, 290)** and is **NOT player-controllable** — the destiny flow never releases to
gameplay (wash ashore → stand on the beach → regain control). Our only create capture is the verbatim `hoxxling`
one, which **disconnects right after the cutscene** (wrong-char world), so it contains NO packets for the
post-`IntroDone` control-release / wash-ashore. The replayed sequence ends at `StartFixed3d` (fixed-camera lock) +
`7727158` with nothing after. So this final handoff is GUESSWORK without new data.

**Wash-ashore DONE + tested** (the key retail mechanism, decoded from `Blablabla_soldier-guardian_create_*`):
retail's EDITOR connection sends a changemap-prep (`IntendedTeleport 0x10D27BC0` + `n3Teleport 0x5A32F0D7`) to the
Tortage Beach spawn (`322.5,150,704.9`, map `0x0FAA`, descriptor `0x32`) RIGHT BEFORE the `0x2002` handoff - the
same `SendChangeMapPrep` GMCommands.DoTeleport uses. Added to the create `0x206E` handler (GameServerListener)
before `SendWorldHandoff`. **In-client (verified class):** loading screen now appears, the drowning/underwater
scene plays, and the char **washes ashore to the beach** - the create flow now runs editor → drowning → wash
ashore → beach, no crash/hang/black/disconnect. HUGE.

**Remaining: post-cutscene CAMERA / destiny-tutorial tail.** On the shore the camera ends in the wrong (fixed
cinematic) position. `StartFixed3d` (sent at `IntroDone`) is byte-IDENTICAL to retail - not the bug. Retail
CONTINUES past it: `CombatTutorial` (~19s later) → `ApproachCasilda` → `LeaveCasilda` → `Level` → `QuestRewards` →
... - a long scripted destiny-TUTORIAL sequence (minutes of gameplay, in `Blablabla_*` from f1174 onward) that
manages/releases the cinematic camera and drives the first quest. My overlay stops at `StartFixed3d`. Finishing
the camera needs replaying that post-cutscene tutorial chain (each step triggered by client callbacks / player
progress) - decode it from `Blablabla_soldier-guardian_create_*` (port 7020, after f1055) and extend
`SendCreateCutsceneStep`. This is the remaining #234 work; the core create→beach flow now works.

NB the full retail order is: editor drowning (autonomous) → editor changemap-prep to beach → handoff → world
MapChange + beach cutscene (Kalanthes, callback-driven) → `IntroDone`/`StartFixed3d` → world entry (0x201c, AFTER
the cutscene in retail; we do burst-less up-front, which works) → CombatTutorial/destiny tutorial → gameplay.

## Camera diagnosis (2026-06-22, decoded from Blablabla create f1054-1071) — THE next step

The stuck camera is the cinematic letterbox NOT releasing. Decoded retail's post-`IntroDone` sequence on the world
connection (port 7020): `IntroDone`(1054) → `StartFixed3d`(1055, byte-identical to ours) → **`0x201c` world-entry
handshake ×3 (1056-58) → `0x2020` (1059) → MoveTasks(1062-66) → FA015807(1069) → `0x2020`/`0x2008`(1070-71)**. So
retail does the playable **world establishment (the `0x201c` 5-entry handshake + spawns) AFTER the cutscene** -
that is what exits the cinematic and hands control to the player (then `CombatTutorial` etc. are player-progress
driven, no Scry callbacks between 1055-1174). Our burst-less does the `0x201c`/spawn establishment UP FRONT (before
the cutscene), so post-`IntroDone` the client never receives the "now playable" `0x201c` and stays letterboxed.

## 2026-06-22 (cont.) — loopback-dump diff vs retail: the camera/cutscene tail is TWO issues

Decoded retail's world connection (`Blablabla_*` port 7020) AND our flow (`/tmp/ourcreate` loopback):
- **Retail establishes the FULL world entry BEFORE the cutscene** (MapChange + ~40 spawns + player SNAPSHOT f474 +
  `0x2024` ReadyForPlay f619 + abilities), THEN starts the cutscene (f631). So my ORIGINAL order (burst-less up
  front) is correct - the reorder (defer player) was WRONG and broke the loading-screen dismiss; reverted. The
  `0x201c` handshake comes AFTER `IntroDone` (f1056) but a bare `Send0X201C` bolt-on there did NOT release the
  cinematic.
- **Our flow has a DUPLICATE `StartFixed3d`** (loopback f649) BEFORE `ClientLoadingDone` that retail lacks:
  `PostReadyBurst` (the existing-char post-`0x205F` beach destiny burst - `StartFixed3d`/`ApproachCasilda`/...) is
  ALSO flushing on the create-world connection (server log "Flushing pending post-0x205F follow-up") and CONFLICTS
  with my cutscene overlay. That's the in-client "GUI for a bit, then cutscene mode starts again."
- **Kalanthes the cutscene actor never renders** (in-client: "didn't see Kalanthes"), so the "Kalanthes walks up"
  step sticks - the #185 scene-actor gap.

**Remaining create-flow work (2 items):**
1. Deconflict: on the create-world connection (`IsCreateWorldSession`), DON'T run `PostReadyBurst`'s destiny steps
   (StartFixed3d / ApproachCasilda / the caging tableau) - my `SendCreateCutsceneStep` overlay owns the destiny
   flow. Keep any genuinely-needed post-ready bits (abilities). Gate in `FlushPendingPost205FFollowUp`/PostReadyBurst.
2. Render Kalanthes (and the scene actors) for the cutscene (#185) + cinematic actor-tagging
   (`TagCharacterForCinematicScriptCommand`, docs/create-cutscene-camera-paths.md) so the walk-up plays.

In-client now: create → drowning → loading screen → wash ashore → BRIEF gameplay/GUI control → then the (conflicted,
actorless) Kalanthes cutscene re-engages and sticks. Big progress; these two items finish it.

## PATH A chosen (full faithful continuous intro) — concrete plan + foundation (2026-06-22)

Confirmed it's a SINGLE continuous cinematic in retail (no control until the very end), and that the StartFixed3d
de-dup (commit b6eb9d7) reduced our mid-cinematic control flicker. The two pieces, both now scoped:

**(1) Cinematic actor-tagging (Kalanthes) — TRACTABLE, not a content block.** Kalanthes IS a renderable beach NPC
(NpcSpawns Id 10641 @ 293.78,145.2,727.26) and is only 36u from the wash-ashore spawn (well inside the 150u
interest range), so he's already streamed - the gap is the CINEMATIC ACTOR BINDING. `TagCharacterForCinematicScriptCommand`
(`FUN_014514c0`) parses an `"object"` reference telling the cutscene WHICH entity it controls (the walk-up). We
send `ThisIsKalanthes` with the CAPTURED refs (`f6Field1=50000, f6Field2=97795`) - the live flow needs the LIVE
Kalanthes handle bound. NEXT: decode how the cutscene references the actor (is `ThisIsKalanthes` f6 the actor
id/handle? or is there a separate `TagCharacterForCinematic` packet?). FINDING: `ThisIsKalanthes`'s `f6 =
{50000=0xC350 type, 97795=0x17E03}` is a ProtoID, but `97795` is NOT a spawn handle (no `FA015807` carries it; our
live Kalanthes handle is `0x296D6`) - it's a CINEMATIC SCRIPT-OBJECT id. So the binding is INDIRECT: the cutscene
refers to script-object `97795`, and a separate `TagCharacterForCinematicScriptCommand` (inside a ScriptGroup) maps
that script-object -> the live Kalanthes entity handle. NEXT: find/decode that tag command (search Blablabla_* for
the Kalanthes handle `0x296D6`-equivalent near f658, and the ScriptGroup that carries the tag), then emit it bound
to OUR live Kalanthes handle. This is genuine cinematic-script RE (the ScriptCommand system), not a quick re-address.
Same pattern for the Casilda actor. The walk-up is then a server MoveTask for Kalanthes during the cinematic.

**(2) The full continuous destiny tutorial.** Replay retail's post-IntroDone sequence (Blablabla_* port 7020 from
f1055): `StartFixed3d → 0x201c → CombatTutorial → ApproachCasilda → LeaveCasilda → Level → QuestRewards → ...`,
each step triggered by client callbacks / player progress (combat done, reached Casilda, quest turn-in). Extend
`SendCreateCutsceneStep` past case 4 with these, driven by the corresponding callbacks. Control is handed over only
at the END. This overlaps the existing-char beach destiny flow (PostReadyBurst's ApproachCasilda, #267) and the
Casilda chain (#261) - unify them.

This is a multi-step epic (cinematic-actor RE + the full tutorial replay), best continued as a focused effort with
the Ghidra/decode tooling. Foundation + first step (the Kalanthes actor binding) are the entry point.

---
(superseded note below) **Fix (reorder to match retail) — REORDER REQUIRED, not a bolt-on (tested):** I tried bolting `Send0X201C` on
AFTER `IntroDone`/`StartFixed3d` (case 4) - it did NOT release the cinematic. Reason: my create flow calls
`SendBurstlessWorldEntry` directly (the existing-char path is `SendWorldEntry` = `Send0X201C` + burst-less, so my
create flow was even missing the `0x201c` entirely), and burst-less establishes the PLAYER (FA015807 + snapshot +
0x2024 + interest) UP FRONT, before the cutscene. So the client is established cinematically before the cutscene
and a later `0x201c` can't re-transition it. The real fix is the REORDER: before the cutscene send ONLY the
playfield scene (MapChange + world objects); defer the PLAYER world-establishment (`Send0X201C` + player FA015807 +
snapshot + `0x2024` + NPC interest) to AFTER `IntroDone`/`StartFixed3d` (case 4), matching retail's f1056-1071. I.e.
SPLIT `SendBurstlessWorldEntry` into `scene-setup` (before) + `player-entry` (after the cutscene). This is the
remaining camera/control work; everything up to the shore is committed + working (commit 28c2a77).

NB the editor "drowning" cutscene (playfield 4000) is client-autonomous; the per-class CLOSER fix (the 7
borrowed-closer classes crash — Assassin/Ranger/Conqueror/DarkTemplar/Priest/Tempest/Herald) is a SEPARATE
prerequisite for those classes (only Barbarian/Guardian/BearShaman/Necromancer have real closers today).
