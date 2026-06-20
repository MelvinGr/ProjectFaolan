# Handoff 2026-06-20 — Death/release window (#214): packets correct, gate is the dead-state flag

## UPDATE 2026-06-20 (session 3, in-client) — death flow WORKS; the one remaining blocker is the WAVE-respawn path on the resurrect click

**Read this section first — it's the current state. The older sections below are the (correct) trail that led here.**

### What works in-client now (verified by Melvin)
- **Death → release window + movement lock + camera pan + dead pose.** The full Dead substate is entered.
- The window's **location title** resolves, and the **server-side respawn** (revive + move) is correct.

### The fixes that got us here (all committed)
1. **`CharacterStats.bcc_state = 29`** in the player FA015807 self-spawn = THE dead-state gate for a
   login-while-dead char (commit eec6c33). Health=0 and the forced death anim (`anim_forced_id=41877`) are
   COSMETIC (HP bar / lying pose) — they were red herrings. (PlayerSpawnMessage.ApplyCharacter.)
2. **In-session death BccIIR** (`WorldDeath.BuildDeathBcc`, behaviorId 41877) sent at the end of the death
   sequence (`SendDeathScreen`) — this is what makes an IN-SESSION death (not just login-while-dead) enter the
   Dead substate; **it restored the death CAMERA PAN in-client** (commit 767e76a). bcc_state toggles via the
   BccIIR channel (`0x642CD3D6`), NOT the FA015807 (the spawn only carries the *initial* state).
3. **Lightweight in-place revive** (`WorldDeath.BuildRevive` BccIIR bcc_state 29→1 + vitals) replaces the heavy
   `ReenterWorld` in BOTH `RespawnAtChosenPoint` and `RespawnPlayer` (commits 767e76a). Decoded from the retail
   beach death+resurrect capture (`Stihetfv_soldier-conqueror_login-die-resurect-beach`, #1382 revive / #1292
   death / #1387 IntendedTeleport). Retail does NOT world-re-enter for a near revive.
4. **`Character.IsDead` cleared before the revive** (commit 76c0800) — `RespawnAtChosenPoint` forgot to, so the
   re-entry rebuilt the dead spawn. (Now moot since we don't re-enter, but correct.)
5. **`RespawnPoint.Id` = the RDB `Guid`** (commit 4110fec) so the `0x2024` `spawnPointId` is the client id the
   release screen needs for the title + `RespawnMe` echo (beach AlwaysKnown = 784236/0xBF76C). Importer
   `RdbRespawnImporter` captures `<resurrectionpoint Guid=…>`; migration `RespawnPointClientId`; db reseeded.

### THE BLOCKER — the client takes the WAVE-respawn path on the resurrect click
On the resurrect click the client sends **`SignUpForWaveRespawn (0x2025)` + `RequestWaveRespawnTimerUpdate
(0x2028)`** (a repeating ~15s countdown) instead of instant **`RespawnMe (0x2024)`**. We don't handle the wave
opcodes → countdown + nothing happens. **Retail's beach capture uses pure instant `RespawnMe`, zero wave
packets** — so this is wrong, not a retail behavior. It is NOT stale session state (happens on a fresh client
restart AND for in-session death, not just login-while-dead).

**Client decision fully traced (Ghidra):**
- Button handler **`FUN_01268d20`**: goes WAVE (`FUN_012696c0`) if resurrect-window flag **`+0x477 != 0`**
  (primary); else if `+0x476` then stat `0x546`>0 else stat `0x545`>0 → WAVE; else INSTANT (`FUN_006ede20`,
  which calls the RespawnMe proxy `0x4fa9f0`).
- Window setup **`FUN_012673b0`** (ResurrectView_c ctor, loads `ResurrectView.xml`, has a `WaveRespawnBar`
  widget) sets the flags:
  - `+0x477 = 1` if `GetStat(0x360) & 0x8000` **OR** a resurrection-destination object `window[0x70]` (via
    `FUN_00591c00`) has field `+0x74 == 0xC7A3`.
  - `+0x476 = 1` if `SimpleChar[0x81] == 50000 (0xC350, = the entity class, ~always true)` AND current-playfield
    checks (`FUN_00592aa0`).
- Stat leaf `FUN_006f9700` is a per-character stat-map lookup → **returns 0 for an absent stat**. Stats
  `0x360`/`0x545`/`0x546` are **absent in our snapshot AND the FA015807 generic_stats** (checked) → 0.
- Our `0x2024` release-screen body is **byte-identical to retail's instant one** (spot const `0xC7A0`, not the
  `0xC7A3` the wave gate wants); the wave stats are absent. **By this static read it should be INSTANT — yet
  in-client it's WAVE.** So either one offset above is misread, or the trigger is live playfield/destination
  state not in any packet we send differently from retail. Leading suspect: the **playfield** (the `+0x476`
  playfield checks / how the client models our beach playfield instance vs a real Tortage one).

**To resolve A (true instant):** read the live window `+0x476`/`+0x477` (and `window[0x70]+0x74`) on a dead
client when the button is clicked — breakpoint `0x1268e53` (CMP `[ESI+0x477]`) in `FUN_01268d20`, ESI = the
window. ⚠️ gdb on this Wine client is FLAKY: it stops on Wine **SIGUSR1/SIGUSR2** and freezes the client even
with `handle … nostop noprint pass`; kill gdb + `kill -CONT` the client to recover. Client addrs (no ASLR):
button handler `0x1268d20`, window setup `0x12673b0`, wave path `0x12696c0`, instant path `0x6ede20`, stat leaf
`0x6f9700`, playfield getters `0x591c00`/`0x592aa0`, SimpleChar getter via `DAT_0297d374->[0x10]`.

**Fallback B (pragmatic, not yet done):** handle the wave opcodes server-side — route `SignUpForWaveRespawn
(0x2025)` (body `[u32 spawnPointId][u32 0]`) to the same lightweight revive, and answer
`RequestWaveRespawnTimerUpdate (0x2028)` (reply `DispatchReplyWaveRespawnTimerUpdate 0x207C`, format
undecoded — no wave capture) with timer 0 to kill the countdown. Makes the button work regardless of path.
(Melvin wants A — true no-wave — so B is a last resort.)

### Retail revive sequence (decoded, for completing the in-place revive properly later)
After `RespawnMe`, retail (beach #1382-1387) sends, on the player handle, with sender GameAgent 0x59:
`0x642CD3D6` revive BccIIR (bcc_state 29→1) → `0x96B8DC59` vitals (full HP) → `0x10D27BC0` IntendedTeleport to
the res point → NPC interest re-stream. Distance-based: a FAR res point gives a loading screen (world re-entry);
a NEAR one (beach) is this lightweight in-place form. We send the BccIIR + vitals; the IntendedTeleport
(`0x10D27BC0`, body has the playfield token field we'd need) is NOT yet built — we revive in place at the death
spot (fine for the adjacent beach point).

---

## UPDATE 2026-06-20 (in-client, end-to-end) — window + respawn round-trip WORK; revive-completion is the last gap

Live in-client progress (Melvin, on the local server):
- **Window + movement lock: WORKING** (bcc_state=29, commit eec6c33).
- **Location title: WORKING** — `RespawnPoint.Id` is now the RDB `Guid` (commit 4110fec); beach AlwaysKnown
  = 784236 (0xBF76C), byte-matching retail's `0x2024`.
- **Resurrect click → server revive: WORKING** — on a CLEAN relog the client sends instant
  `RespawnMe (0x2024)` `[u32 spawnPointId]`; the server resolves the spot, revives, moves to the res
  point, logs "respawned." Verified by gdb (the resurrect button at ResurrectView `FUN_01268380+0xebd`
  calls the RespawnMe proxy `0x4fa9f0`).

**The "wave respawn" (0x2025/0x2028 + repeating 15s countdown) was STALE SESSION STATE, not a packet
flag.** After an unclean relog the client was stuck in a leftover wave-respawn UI; a fresh client login
makes it use instant `RespawnMe (0x2024)` like retail. So there is nothing to fix on the wave path — just
don't carry over a dirty session. (We still don't handle 0x2025/0x2028; we don't need to for normal death.)

**THE REMAINING GAP — revive completion.** After `RespawnMe` our server calls the full
`ReenterWorld` (54-packet burst-less world entry). The client — already in-world showing the release
window — does NOT apply that as a Dead→Alive transition: the window stays open, the avatar isn't cleanly
revived (HP ticking from mobs, stray XP). **Retail does NOT re-enter the world.** Decoded from
`AssassinL23_...resurect` right after the `RespawnMe` (pkt #4807), the retail revive is a lightweight
in-place sequence on the EXISTING entity:
- **#4840 `0x642CD3D6` BehaviorControlCenterIIR (BccIIR)** on the player handle — the "resurrect confirm"
  behavior action; almost certainly the bcc_state 29→1 transition that CLOSES the window (grammar in
  `docs/behaviorcontrolcenter-grammar.md`).
- **#4845 `0x96B8DC59` vitals** — restore current health to full.
- entity despawns (`0x04B82B2D`) for the old area, then **#4904 `0x10D27BC0` IntendedTeleportIIR** —
  teleport to the res point, then re-stream nearby NPCs (interest).

**Next step (deferred — Melvin 2026-06-20: "we'll figure that out later"):** the choice is DISTANCE-based.
On retail a res point FAR from the death spot gives a loading screen (= a real world re-entry, our current
path); a NEARBY res point (our beach case: death ~<331,146,707>, res point <336,148,660>, ~47 units) does
the lightweight IN-PLACE revive (no loading screen) — BccIIR alive + vitals-to-full + IntendedTeleport +
interest refresh — which is what closes the window cleanly. So `RespawnAtChosenPoint` should pick: near ⇒
lightweight in-place revive (decode #4840 `0x642CD3D6` for the exact bcc_state 29→1 alive form, the inverse
of the dead spawn we already build); far ⇒ keep `ReenterWorld` (with the loading screen). The bcc_state
mechanism we already own is the key to the in-place form.


## UPDATE 2026-06-20 (SOLVED: the window shows) — the gate was `CharacterStats.bcc_state`, NOT health

**The release/resurrect window now paints in-client and the player is movement-locked** (verified by
Melvin). The gate was NEVER health. Walking the same-character (Stihetfv) dead-vs-alive player
FA015807 self-spawn (`SimpleCharFullUpdateIIR`) field-by-field, three things differ that matter, in
ascending order of importance:

1. `CharacterStats.health`(f7) = 0 (life/max full) — **cosmetic**: blanks the HP bar only.
2. `CharacterBcc.anim_forced_id` = 41877 (timer 1.0, blend 1) — **cosmetic**: the lying-down death
   POSE. With only 1+2 the avatar lay on the floor but **could still walk** and **no window**.
3. **`CharacterStats.bcc_state`(f22) = 29** (alive = 1) — **THE gate**. The behavior/state-controller
   state enum. Setting it locks the player into the Dead substate → movement locked → resurrect window
   paints. Across all 15 captured ALIVE player spawns `bcc_state==1`; the ONLY 29 is the dead capture.

All three are now set for a dead character in `PlayerSpawnMessage.ApplyCharacter` (commits 443213b /
4ae5b34 / eec6c33; alive path byte-unchanged; `PlayerSpawnGenerationTests` locks it). So the whole
multi-session "construction-latch from health" thread was a red herring — the dead STATE is an explicit
enum field in the spawn, not an inferred consequence of health=0.

**Two follow-ups remain (the window works, but):**
- **Empty location title.** The `0x2024` spot body carries no name string; the client resolves the
  title from the **`spawnPointId`** (a client-RDB respawn-point id). Retail sends real ids — beach
  (map 4010) = **0xBF76C (784236)**, AssassinL23's map 2010 = 0xF5C46 (1006150). Our `RespawnPoint.Id`
  must BE that client id or the title is blank (and `RespawnMe` can't resolve it).
- **Resurrect button does nothing.** The client echoes the chosen `spawnPointId` back in `RespawnMe`
  (c2s 0x2024); our handler looks it up in `RespawnPoint`. If our id ≠ the client id (above), the
  lookup fails → no revive. Fix = seed the real client respawn-point ids; then verify the
  `WorldSimulation.RespawnPlayer` revive path. (Retail 0x2024 bodies decoded above; per-spot grammar
  in `ResurrectionSpotsBuilder`.)


## UPDATE 2026-06-20 (step 3 IMPLEMENTED) — step 2 zeroed health in the WRONG packet; fixed the FA015807 self-spawn

**The construction-latch theory was right; step 2 just patched the wrong object.** The local
**SimpleChar** (whose dead-flag gates the window, `DAT_0297d374->[0x10]`) is built by
**`SimpleCharFullUpdateIIR` = the player's own FA015807 self-spawn**, NOT the `0x737A6DF9`
FullPlayerChar snapshot. Step 2 zeroed `HealthCurrent` only in the snapshot (a different object) →
the SimpleChar was still constructed full-health → flag false → avatar stood, no window. That fully
explains the step-2 in-client failure WITHOUT abandoning the latch theory.

**Decoded proof (same character alive vs dead).** The retail `login-while-dead` capture's player
self-spawn is **Stihetfv** (handle `0x021663dd`, FA015807 pkt **#50**), and we also have Stihetfv's
**alive** login (`Stihetfv_soldier-conqueror_login.tshark.json` #44) — same char, clean diff. Both
get a self-FA015807 (the local player gets BOTH a self-FA015807 and the `0x737A6DF9` snapshot). The
embedded `ProtoCoreCharacterIIR.CharacterStats` differs in exactly:
- **`health` (f7) = 0 dead / 16800 alive** ← the latch field
- **`life` (f6) stays full** (dead 19600) — only CURRENT health drops, max is untouched
- **`can_fall` (f20) = 0 dead / 1 alive** (a corpse is ragdoll, not gravity-stepped)
- (f7 bcc differs + f12/f13 offensive/defensive targets present when dead — combat residue, ignored)

**Fix (commit 443213b, needs in-client test).** `PlayerSpawnMessage.ApplyCharacter` now sets
`Stats.Health = 0` and `CanFall = false` for a dead character (life/max unchanged; alive path
byte-unchanged). This is the SOLE world-entry player-spawn path (`SendBurstlessWorldEntry` →
`BuildBurstlessPlayerSpawn`). Test `ApplyCharacter_DeadCharacter_ZeroesCurrentHealthKeepsLifeFull`
(PlayerSpawnGenerationTests) locks it. Step 2's snapshot `HealthCurrent=0` STAYS (retail-dead carries
both); they reinforce, not conflict.

**TEST (needs Melvin — visual, the launcher can't see a painted window):** rebuild + restart the
server, flag a char dead (`Character.IsDead=true`, e.g. die in-session or set it in the DB while the
server is stopped), log in → the release/resurrect window should paint at world entry. If it shows,
the latch theory is confirmed at the correct packet and the in-session death (which already sends the
death sequence) should re-latch via the same health=0 the kill record carries. If it STILL doesn't
show, the next lever is the gdb session #2 lead below (the C++ Dead substate transition) — but health
is now zeroed on the constructing packet, so the construction-latch is finally being exercised
correctly.

---

## UPDATE 2026-06-20 (live gdb session #2) — the feed path is COMPLETE; the handoff's gate theory is WRONG

A second live gdb session (client on our LOCAL server, char `Testie` flagged dead) overturned the central
claim of this handoff. **Do not chase the `0x2024`/snapshot/feed path further — it already works end to end.**

**1. `+0xA4` / `method_0x10` is NOT a dead-flag.** `DAT_0297d374` is the local-player **controller**; its
`vtable[0x10]` method (`0x596150`, just `mov eax,[ecx+0xA4]; ret`) returns the controlled local **`SimpleChar_t`**
(RTTI `.?AVSimpleChar_t@@`, vtable `0x203e93c`). That pointer is **non-null whether the player is alive or
dead** (verified: alive-and-walking, `+0xA4` still populated). So the gate `FUN_01395d20`'s
`if (method_0x10() != 0)` is just "is there a local character", not a dead-check. The earlier "the gate returns
false on local" reading was wrong.

**2. Our server drives the ENTIRE native resurrection path to completion** — proven with execution breakpoints
on our local server (fired twice, identical each login):

```
[0x4c16a0  0x2024 DispatchValidResurrectionSpots handler]  ✓ fires
[0x4c1829  vtable+0x94 window-feed call]                    ✓ reached
[0x1395d20 FUN_01395d20 gate]                               ✓ passes (local SimpleChar present)
[0x6edf40  ShowResurrectionView]                            ✓ called (stores deathPos into SimpleChar+0x2b8/0x2bc)
[0x6edf9c  window-open call 0x443c70 on DAT_0297d3d8+0x120] ✓ called  (DAT_0297d3d8 = resurrection-UI mgr, non-null)
[0x6edfa1  window-open returns]                             ✓ completes; observer list non-empty
```

⇒ **There is no missing packet or snapshot field in the feed path.** The whole open sequence executes; the
window simply doesn't paint.

**3. The real gate is the client-side player DEAD STATE.** The window-open completes but manifests nothing
because the local player is never in the **Dead substate of `CharacterStateController_c`** — so the
resurrection-view GUI isn't created/registered (or its handler declines). This is the handoff's _other_
hypothesis (pure-C++ Dead substate), now proven to be THE gate. **The step-2 snapshot/health approach below
aimed at the wrong layer** (it's kept as inert scaffolding; `Character.IsDead` + `CurrentHp=0` make the HP bar
read 0 but do NOT enter the Dead state).

**Next target** find what
transitions the client into the Dead substate. Two leads: (a) Ghidra the `CharacterStateController_c` Dead
transition + what polls the SimpleChar's state/health to trigger it; (b) diff the retail dead player's
`FA015807` spawn / `CharacterIIR` (capture `retail_login-while-dead...` pkt #50, handle `0x021663dd`) against
our generated player spawn for a state field. Key client addresses (no ASLR, runtime==Ghidra): controller
global `DAT_0297d374`; SimpleChar getter `0x596150`; gate `0x1395d20`; `ShowResurrectionView` `0x6edf40`;
window-open `0x443c70`; resurrection-UI mgr global `DAT_0297d3d8`; `0x2024` handler `0x4c16a0`.

---

## UPDATE 2026-06-20 (in-client test) — health=0 is NOT the gate; the construction-latch theory is DISPROVEN

Step 2 was tested in-client (char flagged dead in the DB, relogged). Result: **the 0 HP bar shows correctly
but the release window does NOT, and the avatar stands** — even though the server ALSO fired the full death
sequence (death-pose MoveTask + SimpleCharKilledBy + `0x2024` spots + death effect, confirmed in the log) AND
the snapshot carried `HealthCurrent=0` AND the in-session vitals were 0. So **health=0 at SimpleChar
construction does NOT latch the client dead-flag** — the earlier "construction-latch from health" theory is
wrong. The window is gated on a field we are STILL not setting (confirms the original `vtable+0x10` gdb
finding; neither the snapshot health nor any death packet flips it).

New clue from the retail `login-while-dead` capture: the dead player (handle `0x021663dd`) has its FA015807
spawn (#50) but **NO MoveTask at all** (every `96C46740` in the capture is an NPC's). So the dead pose/state is
carried **inside the player's own FA015807 spawn or its `CharacterIIR`**, not a separate packet — that (or a
non-health state field in the snapshot) is the real gate. Pinning it needs the **step-3 gdb trace** of what
`method_0x10` reads on the live retail client.

Step-2 code is KEPT as scaffolding (Melvin, 2026-06-20: "keep it, set chars alive") but is INERT until the real
field is found: `Character.IsDead` persistence, the snapshot `CurrentHp=0` patch, `BuildVitals`/`EntityVitals`
dead handling, and the `WorldSimulation` set/clear all remain. NB while a char is `IsDead` the world model
treats it dead (NPCs won't attack, no regen) — so only flag a char dead for a death test.

## UPDATE 2026-06-20 (later) — STEP 1 found the (insufficient) field: current-health = 0

The diff (handoff step 1) is **done**. The dead-state field in the `0x737A6DF9` world-entry snapshot is the
**tails stat block's current-health stat**: `statId 0x001B` (`LegacyCharacterStatId.HealthCurrent`) **= 0**
(the `HealthMax`-side slots stay full). Proof + refined mechanism:

- `Captures/scripts/decode_death_snapshot.py`: across **37 alive** login snapshots `HealthCurrent` is always
  full HP; **only** the `login-while-dead` capture has it `= 0`. Locked by `Faolan.Tests/DeathSnapshotTests`.
- Slot map (positional `TailsPostAccessBlock.Values`, the stat block read as `[statId,value]` pairs): the
  current-health VALUE lands at `[23]` (`CurrentHpExtra`), with repeated-block copies at `[54]`/`[202]`; the
  base/max side is `[21]`/`[56]`/`[204]`. Dead capture: `[23]=[54]=[202]=0`, `[21]=[56]=[204]=19600` (max 196).
- This maps 1:1 onto `TailsPostAccessBlock.CurrentHp` (which already writes `[23]`/`[54]`+`[202]`). So a dead
  login snapshot = the normal one with `CurrentHp` patched to **0** (leave the base/max slots full).
- **Refined mechanism (the crux for step 3):** the client's local-player dead-flag (`DAT_0297d374->[0x10]()`)
  is **latched when the SimpleChar is CONSTRUCTED from the snapshot** (health 0 at birth ⇒ flag true). It is
  **NOT** recomputed from a mid-session `HealthCurrent=0` StatUpdate — we already push exactly that in-session
  (`WorldSimulation.ApplyNpcAttack`→`PushVitals`) and the flag stays false (matches `WorldDeath.cs:8`). So the
  login path works via the construction-time latch; the **in-session path needs the state-transition packet
  that re-latches it** (still the gdb-trace work below — find the setter behind `vtable+0x10`).

**Step 2 IMPLEMENTED (commit 1623285) — needs in-client retail test.** A dead character now logs in dead:

- `Character.IsDead` (+ migration `CharacterIsDead`) + `IDatabaseRepository.SaveCharacterDeadState` persist it
  independently of the full-graph save (like the bind point / XP progress).
- `WorldSimulation` sets it on death (`TickPlayerDeaths`, alongside the release screen) and clears it on
  respawn (`RespawnPlayer`, before the revive re-enter so that snapshot is alive).
- Both login tails paths zero `CurrentHp` for a dead char: `RetailLoginReplay` (captured tails) and
  `BeachTailsBuilder.BuildFromScratch` (beach). Max/base HP stays full. Alive path byte-identical (IsDead
  defaults false — the beach goldens still pass); `BuildFromScratch_DeadCharacter_ZeroesCurrentHealthKeepsMax`
  proves the dead shape. 566/566 tests pass; migration auto-applies at server startup.
- **TEST:** rebuild + restart the server (the migration applies on startup), then in-client: die, log out, log
  back in → the release/resurrect window should show at world entry. If it does, the construction-latch theory
  is confirmed and step 3 = replicate that latch mid-session.

Remaining: **step 3** (the in-session re-latch packet — gdb-trace the setter behind `vtable+0x10`).

## TL;DR

The in-client **release/resurrect window still doesn't show on our server**, but a full live-debugging +
capture session proved **our death-packet layer is byte-correct** and pinpointed the real gate. The window is
gated on a **client-side local-player dead-flag** that our world-entry never sets. Retail carries that
dead-state in the **world-entry `FullPlayerChar` snapshot** — not in any combat packet. The fix is to set that
dead-state in the snapshot we already generate (and/or feed it in-session), NOT more death-packet plumbing.

Everything below was verified with gdb attached to the live retail client (no ASLR → Ghidra addrs == runtime;
see agent memory `gdb_live_client_debugging`). Full chain is on Gitea **#214** (comments through 2026-06-20).

## What is already done + committed

- **00982a9** `feat(combat)`: byte-exact player-death sequence (death-pose MoveTask type `0x06`, death-effect
  ApplySpell spell `0x0009F000`, `SimpleCharKilledBy` with self-killer, sender corrected to GameAgent `0x59`)
  - **removed the 10s auto-respawn** (respawn is now purely click-driven via inbound `RespawnMe` c2s 0x2024).
    Golden-tested (`WorldDeathTests` 3/3). All of this is correct — verified on the wire vs retail.
- **dabd2aa** `chore(captures)`: the retail **login-while-dead** capture
  (`Captures/retail_login-while-dead_resurrect-window.{pcapng,tshark.json}`) + Ghidra helpers
  (`Captures/ghidra/{VtableSlots,XrefsRW,WhatClass,CallsWithConst}.java`).

## Proven by live gdb (retail death vs local death, identical results)

The client's death path fires the **same** on our server as on retail:

- `0x148b8f0` `SimpleCharKilledByUpdateIIR::ReadSubClass` — parses our record (version byte `0x01` OK).
- `0x148b880` the apply — records our killer onto the victim `SimpleChar` (`killer=0xC350/0x02000000`).
- `0x4c16a0` the `0x2024 DispatchValidResurrectionSpots` handler → reaches its SUCCESS path `0x4c1829`
  (the `vtable+0x94` window-feed call), no "stream bad" error path.

So **the packets are correct.** Identity is also correct: the victim entity is `SimpleChar_t` (RTTI), class
`0xC350`, handle `0x02000000`; our `EnterWorld`/spawn/kill-record all agree; retail uses the same
char-id-as-handle model (its player handle `0x08006f31`, and `0x021663dd` in the new capture).

## The exact gate (this is the crux)

The `0x2024` window-feed (`vtable+0x94`) resolves to `FUN_0139c990` → **`FUN_01395d20`**:

```
FUN_01395d20(deathPos):
    if (DAT_0297d374 != 0 && DAT_0297d374->vtable[0x10]() != 0):   // is-local-player-dead?
        ShowResurrectionView(deathPos)
```

The window shows **iff `DAT_0297d374->method_0x10()` returns non-zero** (the local-player dead-flag). On our
server the spots are fed but the flag is false → the view stays hidden. `DAT_0297d374` is the local-player /
playfield global; `+0x10` is the dead-flag accessor.

**Refuted:** the Lua `e_OnPlayerDeath` (enum `0x14`, `LuaCallbacks.cpp`) theory — its dispatcher
`FUN_00a1e4f0` fired **0×** during a real retail death. The death UI (corpse + camera-pan + `ResurrectView_c`)
is pure C++ (the Dead substate of `CharacterStateController_c`), not a Lua callback.

## The constructive lead: retail login-while-dead capture

`Captures/retail_login-while-dead_resurrect-window.tshark.json` (518 pkts) — the retail char relogged WHILE
DEAD and came back in the dead state with the window. Decoded:

- **NO `SimpleCharKilledBy`, NO `ApplySpell`** at world-entry.
- The dead-state is carried by the **`FullPlayerChar` snapshot `0x737A6DF9`** (idx 55, ~11.6 KB, receiver =
  player handle `0x021663dd`).
- A single `0x2024` (idx 313) just feeds the window. Its body is **byte-identical to our builder**
  (deathPos + `0x7E2` + spot + `0xC7A0` + mapId `0xFAA`=4010 + zeros + spawnId + `0x0A` + `00`).

⇒ **The dead-state is a field in the world-entry snapshot we already generate**, not a combat replay.

## Next steps (in priority order)

1. **Diff the dead snapshot vs an alive snapshot.** Take `idx 55` of the login-while-dead capture and the
   FullPlayerChar snapshot from a normal (alive) login of the _same/similar_ char, and diff the typed-root +
   tails (grammar in `docs/fullplayer-snapshot-reader.md`). Find the dead-state field (a flag and/or a
   vitals/state value). Caveat: the `0x0000001b` health-stat search hits false positives in string regions
   (`PFAccess`…) — use the snapshot grammar, not a raw byte search.
2. **Set that field in our snapshot builder** for a dead player (and clear it on respawn), so
   `DAT_0297d374->[0x10]()` reads true → the window shows at login-while-dead.
3. **For the in-session death (#214 proper):** trace `DAT_0297d374` (the local-player global) + its method at
   vtable `+0x10` to the underlying flag and its **setter** — then have our in-session death set it the same
   way retail's combat death does (this is the part that does NOT come through the snapshot).
4. Re-test in-client: a dead player at login should show corpse + window; an in-session death should too.

## How to reproduce the debugging setup

- Live gdb on the client: see agent memory `gdb_live_client_debugging` (attach `gdb -p <pid>`, Wine signal
  passthrough, breakpoints at the raw Ghidra addresses, MCP `kill` to trigger a local death). Ghidra project
  at `/tmp/ghidra-aoc/proj`; helper scripts in `Captures/ghidra/`.
- Retail testing: throwaway account, **no anti-cheat** (per Melvin) — connect the client to Funcom, attach
  gdb, die / relog-while-dead. Capture retail traffic with `dumpcap -i wlo1 -f "net 37.18.0.0/16"` (works
  without root) then `Captures/tshark_dump.sh` to decode (GameServer traffic is zlib/plaintext; login is
  TEA-encrypted but irrelevant here).

Related: agent memories `protocol_death_resurrect`, `gdb_live_client_debugging`,
`captures_and_ghidra_always_available`; docs `combat.md`, `fullplayer-snapshot-reader.md`.
