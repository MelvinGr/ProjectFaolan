# Handoff 2026-06-20 — Death/release window (#214): packets correct, gate is the dead-state flag

## UPDATE 2026-06-20 (later) — STEP 1 SOLVED: the dead-state field is current-health = 0
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

Remaining: **step 2** (emit `CurrentHp=0` at login for a dead char — needs a persisted dead-state, a schema
decision) and **step 3** (the in-session re-latch packet). Step 2 is now a small wiring change in
`RetailLoginReplay` (where it already patches vitals); both still gate on an in-client retail test.

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
  + **removed the 10s auto-respawn** (respawn is now purely click-driven via inbound `RespawnMe` c2s 0x2024).
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
   FullPlayerChar snapshot from a normal (alive) login of the *same/similar* char, and diff the typed-root +
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
