# Handoff — Create-character flow reconstruct (#234)

> ## UPDATE — session 3b (2026-06-22): SPEECH ROOT CAUSE = missing BehaviorControlCenter cutscene packets (client log: cinematic-state BCC error → playstockphraseassubtitle times out 60s)
>
> Two ScryScript fixes landed this session (both verified byte-exact vs retail via a fresh loopback capture +
> `Captures/scripts/compare_create_cutscene.py`): (1) `StartGroup` now ECHOES the client's requested group id
> (call-and-response), and (2) `ThisIsKalanthes` now fires on `PlayResult` (after the player commits to the
> cutscene), not `ClientLoadingDone` — matching retail. Our whole ScryScript channel now matches retail exactly
> (steps, order, echoes, resource preload list all identical). **Kalanthes STILL doesn't speak.**
>
> **THE ACTUAL BLOCKER (from the client's `AgeOfConan.log`, decisive):**
> ```
> ERROR: Camera - cinematiccam failed to set cinematic state with BCC error
> PlayStockPhraseScriptCommand.cpp(103): ScriptGroup 'character_creation Tortage Beach (client-side)' (id=24063)
>   Script 'controlcamera' (id=3) Command 'playstockphraseassubtitle' (id=40/43/110): Timed out after 60.0 seconds
> ```
> Kalanthes's speech is a CLIENT-side `playstockphraseassubtitle` (3 lines) inside client script group 24063's
> `controlcamera` script. It times out after **60s** because the cinematic camera **can't set cinematic state — a
> BCC (BehaviorControlCenter) error**. That 60s timeout is the "takes way too long / things eventually happen"
> symptom. The speech is NOT a chat string and NOT a packet string (both correctly ruled out) — it's a stock
> phrase the client plays once it's in cinematic state, which it never reaches.
>
> **What's missing:** during the beach cutscene retail sends ~60 **BehaviorControlCenter (`0x642CD3D6`)** packets to
> the scene actors (decoded from Blablabla idx 668-1050): an opening burst (~668-710) puts every beach NPC into a
> type-7 idle/frozen state (`...0a08c5c302180128183000`), then per-actor behavior updates drive the performers —
> esp. one primary actor `0x2c4e` stepped through ~13 incrementing states (`18 01`→`18 0d`) and Kalanthes
> (`0x2c1c`, idx 889). **We send ZERO BehaviorCC.** This is the long-flagged "verbatim residue: BehaviorCC" (#223)
> / scene-actor gap (#185), now pinned as the exact speech blocker. Handle is at body[12:16] (re-addressable like
> MoveTask); grammar in `docs/behaviorcontrolcenter-grammar.md` + `Captures/scripts/decode_behaviorcc.py`.
>
> **NEXT:** replay the cutscene's BehaviorControlCenter sequence re-addressed to our live handles, timed to the
> callbacks. Open question: the BCCs target ~40 distinct captured scene-actor handles (`0x2c22`-`0x2c7c`); mapping
> each to our live entity is the hard part. Likely need at minimum (a) the freeze-all-NPCs opening burst and (b)
> Kalanthes's + the primary performer's per-state updates for the camera to enter cinematic state. Determine which
> actor `0x2c4e` is (its FA015807 name in the capture) and whether the camera's cinematic state keys on Kalanthes,
> the player, or a dedicated cinematic stand-in.
>
> ### Session 3c (2026-06-22) — BCC route ATTEMPTED + REVERTED; the BCC error is a RED HERRING
> Implemented + in-client tested (then reverted - kept only the committed ScryScript fixes):
> - **Player cinematic BCC** (replay of Blablabla f387/f389, behaviorId 42011, re-addressed to the live player) AND
>   the **scene freeze burst** (BehaviorControlCenter idle for all 32 KNOWN beach NPCs, `BuildBehaviorControlCenterIir(h,
>   slot 7, behaviorId 41413, phase 1, field5 24)`, gated on `player.Known`). Both went out byte-correct (verified in
>   our loopback: 32 freeze records in retail's f668-710 form + the player BCC).
> - **Result: NO speech.** And `playstockphraseassubtitle` STILL timed out 60s, AND `cinematiccam failed to set
>   cimatic state with BCC error` STILL fired. The player BCC made the char **translucent** (cinematic fade state -
>   so the BCC IS processed) but that's an unwanted visual, so the whole BCC route was reverted.
> - **KEY: the BCC error is BENIGN.** `...cam failed to set cimatic state with BCC error` ALSO fires at
>   **character selection** (`characterselectioncam`, no world packets involved) - so it is NOT caused by missing
>   server BCC and is not the speech blocker (matches Melvin: "many client-log errors also happen on retail").
> - **PROVEN: the speech has NO server trigger.** During the full 64s WelcomeTutorial speech window (Blablabla
>   f734->f889) Kalanthes receives ZERO packets; his only two are a BCC-idle (f889) + MoveTask (f890) AFTER the
>   speech. So the subtitle is 100% client-side (script group 24063 `controlcamera` / `playstockphraseassubtitle`).
>   Our `ThisIsKalanthes` binds the correct live handle (0x296D6, verified in our capture). Retail's cutscene is
>   ALSO ~2 min (WelcomeTutorial->Kneel 64.5s, Kneel->StartFixed3d 53s) - long duration is normal; retail's phrases
>   COMPLETE (~20s audio each), ours TIME OUT (never play).
>
> **REMAINING THEORIES (server side exhausted via the visible channels):** (1) a SECOND actor binding -
> `TagCharacterForCinematicScriptCommand` maps a cinematic *script-object id* to the live handle, and the
> `controlcamera` script may resolve its speaker that way rather than via `ThisIsKalanthes` (genuine cinematic-script
> RE - PATH A). (2) the phrase audio/data is genuinely absent from this client's RDB (but that class of RDB-miss
> also occurs on retail). Melvin notes "the tortage timeouts are NEW" - so a regression-window git-bisect of our
> create flow (when did our cutscene last NOT time out?) may be the fastest lead. See memory
> [[createworld_speech_needs_behaviorcc]].
>
> Tooling now in place for every test: a loopback capture runs to `/tmp/faolan_test.pcapng`; convert with
> `Captures/tshark_dump.sh` and diff with `Captures/scripts/compare_create_cutscene.py <ours.tshark.json>`.
> Code this session: `CreateFlow/CreateWorldCutsceneScripts.cs` + `Inbound/HandleCommandPacket.cs`.
> **Everything below predates session 3b.**

> ## UPDATE — session 3 (2026-06-22): the speech blocker is a call-and-RESPONSE bug — StartGroup must ECHO the client's requested id (FIX landed, needs in-client test)
>
> Root-caused the "Kalanthes doesn't speak" blocker from the capture. Three corrections to session 2's notes, then the fix:
>
> **Session-2 claims that were WRONG:**
> - *"NO server packets in the Kneel→IntroDone window."* FALSE — that window (Blablabla idx 734→1055) is full of
>   server traffic: MoveTask `0x96C46740` ×87, BehaviorControlCenter `0x642CD3D6` ×29, RegenIIR ×14,
>   GameActionList `0xF508F4C1` ×10, despawn ×10, StatIIR ×4, FeedbackIIR ×1. These drive Kalanthes's
>   movement + talk/kneel ANIMATIONS. But (confirmed) **none carries the speech string** — the speech text/voice
>   is client-side, run by RDB script group **7088774** (NOT a LuaArchive id; those are 1–2472). NPC chat does
>   NOT ride AgentServer (the chat conn in that window = keepalives only). So the speech depends entirely on the
>   client actually RUNNING group 7088774.
> - The `WelcomeTutorial` Scry step IS byte-exact vs f734 (`f1=5,f2=2,f3=0,f4="WelcomeTutorial",f5="",f6={0,0}`);
>   the `18 00` worry was just field-3 flag=0, which our default matches. NOT the bug.
> - `ThisIsKalanthes` in THIS capture has `f6={50000, 11292}` and **11292 = 0x2C1C = the live Kalanthes handle**
>   (== the `00002c1c` handle in our MoveTask bodies), confirming session-2 finding (A): f6Field2 IS the live
>   entity handle. All our Scry steps are byte-exact + correctly live-addressed. The packets weren't the problem.
>
> **THE BUG (decoded from Blablabla port-7020 c2s↔s2c interleave):** the beach cutscene is a call-and-RESPONSE.
> The client REQUESTS each script group by id and waits for the server to ECHO a `StartGroup` with the SAME id
> before it runs the group:
> ```
> C2S PlayResult                -> S2C ThisIsKalanthes
> C2S StartScript int=7088774   -> S2C StartGroup 7088774   (then ~2.7s later: WelcomeTutorial)
> C2S StartScript int=7124502   -> S2C StartGroup 7124502
> C2S Move                      -> S2C Kneel
> C2S IntroDone                 -> S2C StartFixed3d (+ StartGroup 7727158)
> ```
> Our counter-based `SendCreateCutsceneStep` pushed `StartGroup 7088774` PREMATURELY on `ClientLoadingDone`, then
> answered the client's `StartScript 7088774` request with `WelcomeTutorial` **instead of the `StartGroup 7088774`
> echo**. So the client never got the confirmation that runs group 7088774 → no speech.
>
> **FIX (landed, builds, 4/4 golden tests pass — NOT yet in-client tested):** made the handler token-driven and
> ECHO the requested group id. `SendCreateCutsceneStep(client, token, numericValue)` now:
> `ClientLoadingDone → ThisIsKalanthes + intro MoveTask`; `StartScript → StartGroup(numericValue)` and, when the
> id is 7088774, also `WelcomeTutorial`; `Move → walk MoveTask + Kneel`; `IntroDone → StartFixed3d + StartGroup
> 7727158`. Files: `CreateFlow/CreateWorldCutsceneScripts.cs` + the two call sites in `Inbound/HandleCommandPacket.cs`.
> The `CreateWorldChunkIndex` counter is no longer used by the live path (still used by the golden burst).
>
> **NEXT:** in-client test a create (verified class: Barbarian/Guardian/BearShaman/Necromancer) and watch whether
> Kalanthes now speaks the WelcomeTutorial. If still silent: the ~2.7s delay before WelcomeTutorial may matter
> (retail sends it well after the StartGroup, no callback) — try gating it on a later callback or a timer. Also
> consider whether the client's `StartScript` request must be answered on the SAME framing it arrived on.
> **Everything below this box predates session 3.**

> ## UPDATE — session 2 (2026-06-22 PM): cutscene now PLAYS + lands playable; Kalanthes appears, walks up, but does NOT speak
>
> Big progress over the original symptom. The create flow now: black screen GONE → beach renders → the **retail
> intro tableau** is correct (player prone on the sand, **Kalanthes seated right beside her** — verified in two
> screenshots) → char is playable. Two things landed this session, plus the remaining blocker:
>
> **(A) Cinematic actor-binding — SOLVED (and the original "(1)" RE concern below was WRONG).** `ThisIsKalanthes`'s
> `f6Field2` IS just the **live Kalanthes entity handle** — no separate `TagCharacterForCinematicScriptCommand` is
> needed. The captured `97795` was the hoxxling session's value; binding OUR live handle (`LiveNpcHandle(client,
> "Kalanthes", …)` — looks the NPC up in the player's zone) makes the cutscene camera focus the real Kalanthes.
> Confirmed in-client: camera now points at him.
>
> **(B) Kalanthes walk-up — DONE by MoveTask replay.** Two captured MoveTasks (`96C46740`, Blablabla_* f657 +
> f890), re-addressed to our live handle: f657 = intro TRANSFORM (snaps him to `333.2,146.3,706.0`, next to the
> washed-ashore player); f890 = WALK PATH (to `324.7,145.4,705.1`). `SendKalanthesMoveTask` in
> `CreateWorldCutsceneScripts.cs`, fired at case 0 (transform) + case 3 (walk, on the `Move` callback). In-client
> he now moves up to the player. Caveat: he **slides** (no walk leg-animation) — minor; the path/timing are right.
> Likely the path-form movement-mode byte or a missing anim flag; deferred (cosmetic).
>
> **(C) STILL BLOCKED: Kalanthes does not SPEAK (no WelcomeTutorial dialogue).** This is the remaining work.
> Decoded the full retail callback→response Scry order from Blablabla_* and matched our cases to it exactly:
> `cb0 → ThisIsKalanthes + StartScriptGroup 7088774`, `cb1 → WelcomeTutorial`, `cb2 → StartScriptGroup 7124502`,
> `cb3(Move) → Kneel`, `cb4(IntroDone) → StartFixed3d`. We previously bundled group 7088774 with WelcomeTutorial at
> case 1; reordered so the group loads at case 0 first (matching f665). **Still no dialogue.** So `WelcomeTutorial`
> as a bare `ScryScript.BuildStep` Scry step is NOT sufficient to start Kalanthes's speech.
>
> **NEXT (the dialogue blocker), in priority order:**
> 1. The ~53s gap between `Kneel` (f908) and `IntroDone` (f1054) in the capture is where the speech plays, yet there
>    are NO server packets in that window — so the speech is driven CLIENT-SIDE off the script groups (7088774 /
>    7124502 = RDB LuaArchive cutscene scripts), not off a server dialogue packet. Verify our `WelcomeTutorial`
>    Scry step is byte-exact vs f734 (`00000030a36d3b740000c35001316d8a000000001f080510021800220f57656c636f6d655475746f7269616c2a00320408001000`)
>    — esp. the `mode`/`flag` fields (f734 has `1002 1800` after the marker; our BuildStep mode:2 — confirm flag/the
>    `1800` byte match). A wrong mode/flag could make the client accept the step but not RUN the script.
> 2. Decode script groups **7088774 + 7124502** from the RDB LuaArchive (1992 scripts extracted, see `lua_vm`
>    memory) — they likely contain the `playspeech`/`playanimation`/dialogue-subtitle commands. If the client needs
>    the script BODY (not just the StartScriptGroup id) we may have to deliver it.
> 3. Confirm the client even reaches case 1: add a log line per `SendCreateCutsceneStep` case and watch the server
>    log during a run (the callbacks are `ClientLoadingDone → StartScript → StartScript → Move → IntroDone`).
>
> Code touched this session: `Faolan/GameServer/CreateFlow/CreateWorldCutsceneScripts.cs` only (actor-binding,
> `SendKalanthesMoveTask` + the two captured MoveTask bodies, case reorder). The verbatim-burst RE concerns in the
> ORIGINAL handover below (esp. the "(1) cinematic actor-tagging is genuine ScriptCommand RE" finding) are
> SUPERSEDED by (A) above. **Everything below this box predates session 2.**

---

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
