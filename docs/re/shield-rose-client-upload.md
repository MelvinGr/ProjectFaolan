# The shield rose is uploaded BY THE CLIENT (#620)

Recovered from `AgeOfConan.exe` (Ghidra, `tools/ghidra/ConstScan.java` on the stat id + `tools/ghidra/Callers.java`
+ `tools/ghidra/DecompAddrs.java`). It answers the one question #20 left open: who owns a *player's*
directional-combat guard. The answer is the client, and the `CombatShieldIIR` travels **both** ways.

The capture corpus cannot show this — all 110 captured `CombatShieldIIR` records are downloads naming an NPC,
because no captured session ever pressed a shield key — which is exactly why it had to come off the binary.

## The stat

`0xA5` (165) = *Shield Allocation*, decimal-packed hundreds/tens/units = Left/Mid/Right. Six functions in the
image touch it as a stat id; three of them are the shield rose.

| function | role |
|---|---|
| `FUN_0113de90` | `CombatShieldIIR_c::Activate` — stores the received u32 as the entity's stat `0xA5`, then re-reads the three digits and writes `0x6F` (111) back if they sum above 3 |
| `FUN_01117a20` | the shield **keybind** handler — moves the player's own guard and **sends** the result |
| `FUN_01117d60` | writes one arc's digit back into stat `0xA5` (`SetStat(0xa5, right + (mid + left*10)*10)`) |
| `FUN_01117e20` | reads one arc's digit out of stat `0xA5` |
| `FUN_011215e0` | the `SimpleCharFullUpdate` spawn apply — copies the scaffold's word 13 (`+0x1e0`) to stat `0xA5` (see `fa015807-spawn-reader.md`) |

## The upload

`FUN_01117a20(module, arc, stackAll)` is reached from five one-line command stubs — `FUN_00456160` (arc 1,
Left), `FUN_00456240` (arc 2, Mid), `FUN_004561d0` (arc 3, Right), `FUN_004562b0` (arc 5, reset to 1/1/1) and
the generic `FUN_00455cb0` — each of which resolves the local player and forwards. It has the two modes the
live game documents:

* `stackAll == 0`: a two-step **move**. The first press parks the source arc in `this+0x354`; the second
  decrements it and increments the destination (`FUN_01117d60` twice).
* `stackAll != 0`: zero the stat, then put all three shields on the pressed arc (arc 5 spreads 1/1/1 instead).

It then reads stat `0xA5` back, constructs a `CombatShieldIIR_c` on the stack (the image's ONLY construction of
that class) and hands it to `FUN_00595f90`, whose own failure string names it:

```
n3EngineClient::SendIIRToServer(): Unable to write the IIR to the stream; the IIR will not be sent to the server!
```

The send is gated on a per-entity flag at `+0x378` and rate-limited by the token bucket `FUN_00a6db40(3)`.

## What that settles

* A player's rose is **not** server-rolled. The server receives it, clamps it the way `FUN_0113de90` does,
  stores it and relays it — `GameServerListener.HandlePlayerShieldRose`.
* The mitigation stays server-side either way: all eight `Combat_{PvE,PvP}{0..3}ShieldBonus` tweaks are
  registered and never read by the client (`tools/ghidra/TweakGlobals.java`, reads=0), so the numbers are ours
  to apply. See `docs/rdb/client-tweak-table.md`.
* Retail does not seed a viewer's rose from the spawn: 310 of the 317 captured player scaffolds carry 0 in
  word 13, the slot `FUN_011215e0` copies to stat `0xA5`.
* …and it does not seed one later either. Timing all 110 captured `CombatShieldIIR` against their own
  defender's spawn (`--report shield-rose`): of the 71 first-roses, **none** arrives within 2 s of that handle's
  spawn and the earliest is 2.4 s — an engage, not a catch-up. So a viewer who arrives mid-fight sees no rose
  until the next change, in retail as in ours, and nothing is sent to plug it (#622).
* The mitigation the server applies with those numbers is keyed on the **direction the attacker SWINGS**, not on
  where they stand — see [`../subsystems/combat.md`](../subsystems/combat.md). An NPC carries no direction
  ability, so the server picks one (#621).
