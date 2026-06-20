# Handoff 2026-06-20 — Death/release window (#214): packets correct, gate is the dead-state flag

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
