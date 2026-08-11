# MoveTaskIIR_c (0x96C46740) wire grammar — RECURSIVE move tasks

Recovered from `AgeOfConan.exe` (Ghidra `tools/ghidra/output`; scripts `tools/ghidra/DumpMoveTask.java`
+ `DumpMoveTaskTypeReaders.java`, the vtable-slot method). The MoveTask is a **recursive composite**: a move
can be a *list* of nested moves, which is why the captured bodies span 49–370 B. Gitea #53.

## Top-level reader — `MoveTaskIIR_c::ReadSubClass` (`FUN_00a340e0`)

```
[u16 0]                 # framing (the builders emit 00 00 here)
[float x3]  from-position
[factory]               # FUN_00a46800 — reads the typed sub-task
```

## The move-task factory — `FUN_00a46800`

```
[u8 mode]               # selects the type, then constructs it + calls its slot-14 reader (offset 0x38) and
                        # slot-10 validity check (0x28)
```

| mode | type | reader (slot 14) | wire fields (after the mode byte) |
|---|---|---|---|
| 0  | WaitMoveTask        | — | `[float x2][u8 flag]` |
| 1  | PathMoveTask        | `FUN_00a479f0`(ctor) | (big 0x68 object — path) |
| 2  | RotateMoveTask      | `FUN_00a48b00` | `[float x7][u8 flag]` = to-pos(3) + quaternion(4) + flag |
| 3  | JumpMoveTask        | — | `[u8 flag]` |
| 4  | **ListMoveTask**    | `FUN_00a3fcd0` | `[…hdr…][u8 count][ factory × count ]` — **recursive** |
| 5  | (0x30 obj)          | `FUN_00a461d0` | — |
| 6  | (0x34 obj)          | `FUN_00a46340` | — |
| 7  | **FormationMoveTask** | `FUN_00a46440` | `[u32 0xC350][u32 leaderHandle][f32 x][f32 y][f32 z][u32 slot]` |
| 8  | approach-target     | `FUN_00a46590` | `[u32 0xC350][u32 targetHandle][u8 1][9 zero bytes]` |
| 9  | (0x1c obj)          | `FUN_00a465f0` | — |
| 10 | SetPosMoveTask      | `FUN_00a47…`   | `[float x7]` = pos(3) + quaternion(4) |
| 11 | GroundAlignMoveTask | — | `[u8 flag]` |

`ReadU32` = `FUN_0048f900` (big-endian); raw 1-byte reads via `FUN_00cba1b0`.

### The WaitMoveTask base header — `FUN_00a48820` (2026-06-17)

Both `WaitMoveTask` (mode 0) and the front of `ListMoveTask`/`PathMoveTask` read this base:

```
[u8 flags]
flags & 1 == 0  -> [float duration][float remaining]   # present ONLY when bit0 is clear
flags & 2       -> (sets a bool, no wire bytes)
```

### ListMoveTask — `FUN_00a3fcd0` (mode 4)

```
[WaitMoveTask base header]
[u8]                       # FUN_004e0390 ReadByte (a min/limit byte)
[u8 count]
[ factory × count ]        # each element is a full [u8 mode][fields] sub-task (recursive)
```

With this header pinned, the recursive parser consumes **every** mode-4 body cleanly down to its leaves — the
only un-pinned pieces left are the leaf grammars below (no list-framing ambiguity remains).

### PathMoveTask — reader `FUN_00a483b0` (mode 1, the dominant leaf, ~5037 in lists)

```
[WaitMoveTask base header]
[u8 flags]
flags & 0x10 -> [float]                 # -> +0x5c
flags & 0x20 -> [float]                 # -> +0x60
flags & 1    -> [FUN_0048f550 ×2] ...   # alt branch (packed; size not yet pinned)
else         -> [u32 count][ count × (3 floats = a waypoint) (+ FUN_00a31ec0) ]
```

The `else` branch is the **patrol waypoint list** (the count-prefixed 3-float positions).

All mode-1/5/6/7 leaves are now field-mapped (`DumpMoveTaskLeafReaders.java` + `DumpMoveTaskTail.java`):

| mode | type | wire fields (after the mode byte) |
|---|---|---|
| 5 | BallisticMoveTask | base header + `[float x7][u8 flag]` (`FUN_00a49540`) |
| 6 | AnimatedMoveTask   | nothing — `FUN_00a4a2b0` returns 1 |
| 7 | FormationMoveTask  | `[float x6]`, no header (`FUN_00a4a9d0`) — see below |

### FormationMoveTask (mode 7) — the six words, from the wire (#741)

The client reader gives six words with no header; the corpus names them. 170 records over 21 captures:

```
[u32 0x0000C350]   the leader's entity class
[u32 leaderHandle] the entity this one walks with
[f32 offsetX][f32 offsetY][f32 offsetZ]   where it holds, relative to the leader
[u32 slot]         its index in the formation
```

**The formation is what proves the split.** The four `Black Dragon Guard Patroller`s that follow one
`Black Dragon Guard Captain Patroller` sit on a 3×3 grid — x ∈ {−1, 0, +1}, z ∈ {0, −1, −3} — each carrying its
own slot index, and the eight combinations run **0..7** exactly. `offsetY` is 0 in all 170 records.

That retired an earlier reading of word 4 as a single scalar "offset": it is right for `Scruffy`, whose
`(0, 0, −4.5)` puts it 4.5 units directly behind `Ginger`, and wrong for the 55 records whose displacement is
on X instead.

This is the wire form of AMBIENT NPC-vs-NPC movement. It is **not a chase and not mutual**: one creature keeps
an ordinary multi-waypoint path and the other is bound to it. Harvested per creature-type pair by the
`npc-follow-pairs` module (`--report follow-pairs`); see `docs/subsystems/npc-three-tier-architecture.md`.

The PathMoveTask finalizer `FUN_00a3e400` = `[u8 bool][7 u32]` (a fixed 29 B; its sibling `FUN_00a3e510` only
validates the spline and reads nothing), so both PathMoveTask branches end with it.

## Decode coverage

- **Decode COMPLETE (2026-06-17)**: the full recursive grammar is pinned — `MoveTaskCodecTests`
  round-trips **18,455/18,455 captured MoveTask records (100%)** byte-exact. Scripts: `DumpMoveTaskList.java`,
  `DumpMoveTaskLeafReaders.java`, `DumpPathHelpers.java`, `DumpMoveTaskTail.java`.
- **Built byte-exact** (`MoveTaskCodec`, golden tests): mode 0 bare-position, mode 1 PathMoveTask
  (`BuildPathBody` — the multi-waypoint path; `BuildWalkBody` — the 2-point walk segment), mode 2 stationary
  (Rotate, the 61-B form), mode 8 approach-target. The full recursive form (incl. `ListMoveTask`) is covered by
  `MoveTaskCodec` (parse + re-emit any record byte-exact — `MoveTaskCodecCorpusTests`, all 18,455 records).
- The grouped `PatrolPath` + `PatrolWaypoint` DB tables — ordered `Waypoints` by `Ordinal` — exist; the
  broadcaster emits mode-1 path moves via `BuildWalkBody` for chase steps and re-addresses the captured patrol
  body via `SendPatrol`.

## What EXECUTES a path — and where the speed comes from

The grammar above is the READER. The executor is its vtable sibling: `FUN_00a483b0` sits at slot 14 of the
vtable at `01ff536c`, and slot 11 `FUN_00a47eb0` is the per-frame update (its assert string names
`MoveTasks\PathMoveTask.cpp`). `tools/ghidra/DumpVtableAround.java` prints a class's whole vtable from any one
known slot, which is how the two were connected.

The movement line is:

```c
if (*(float *)(this + 0x60) < speed)
    speed = *(float *)(this + 0x60);      // clamp
FUN_00a3f0d0(spline, speed * dt);         // advance along the spline
```

`+0x60` is the record's optional `PathFlags & 0x20` float and **defaults to `0x7f7fffff` (FLT_MAX)** when the
flag is absent. So it is a speed **cap**, not a speed — and it is absent on all 759 captured multi-point patrol
paths (`PathFlags = 0` on every one), so retail never caps a beat.

The speed itself comes from the ENTITY, via `FUN_00a2f160` -> `FUN_00a2e670`:

- `FUN_00a2f160` returns the entity's **movement mode** from `+0x15c`, folding in the control mode
  (`vehicle.cpp`): under one control mode it maps 1→2, 2→3, 4→5 — a run/sprint modifier — and returns 6
  under another.
- `FUN_00a2e670` switches on that mode (a six-arm jump table) and reads a **different per-entity speed field for
  each**, out of one contiguous float block at `+0x128`…`+0x144`:

| mode | forward | backward | strafe |
|---|---|---|---|
| 1 or 4 | `+0x128` | `+0x134` | `+0x13c` |
| 2 or 5 | `+0x12c` | `+0x138` | `+0x140` |
| 3 | `+0x130` | `+0x138` | `+0x140` |
| 6 | `+0x144` × a factor | | |

### ⭐ The modes are WALK / RUN / SPRINT, and the `<movementset>` XML names them (#606)

That block is the client's own `<movementset>` record (RDB **1010205**) laid out in its PARSER's field order.
`FUN_00606300` reads the XML attributes into a float array, and the array's layout is exactly the block's:

| offset in the movementset | attribute | entity offset |
|---|---|---|
| `+0x00` | `basewalkspeed` | `+0x128` |
| `+0x04` | `baserunspeed` | `+0x12c` |
| `+0x08` | `basesprintspeed` | `+0x130` |
| `+0x0c` | `basereversewalkspeed` | `+0x134` |
| `+0x10` | `basereverserunspeed` | `+0x138` |
| `+0x14` | `strafewalkspeed` | `+0x13c` |
| `+0x18` | `straferunspeed` | `+0x140` |
| `+0x1c` | `chargespeed` | `+0x144` |

(the array continues `accelspeed`, `idleturnspeed`, `movingturnspeed`, `jumpspeed`, `jumpheight`, which the
speed switch does not read.) Substituting gives each mode's triple:

| mode | forward | backward | strafe | ⇒ |
|---|---|---|---|---|
| 1 or 4 | `basewalkspeed` | `basereversewalkspeed` | `strafewalkspeed` | **WALK** |
| 2 or 5 | `baserunspeed` | `basereverserunspeed` | `straferunspeed` | **RUN** |
| 3 | `basesprintspeed` | `basereverserunspeed` | `straferunspeed` | **SPRINT** |
| 6 | `chargespeed` × a factor | | | **CHARGE** (the `vehicle.cpp` control mode) |

⭐ **Mode 3 reusing RUN's reverse and strafe is what pins it**, and it is forced rather than chosen: the
movementset ships no sprint-reverse and no sprint-strafe attribute for it to read. The control-mode fold is the
same story read from the other end — 1→2→3 is walk→run→sprint.

The **empirical anchor**: retail's own patrollers move at 2.2 u/s (9,944 anchor pairs — `WorldState.RouteWalkSpeed`)
and the Default Movementset's `basewalkspeed` is exactly `2.2`. Those NPCs are the ones we send as mode 1.

4 and 5 are an unnamed parallel pair that shares the walk/run speeds exactly (so they differ in something other
than pace), and neither they nor 6 has ever appeared in the spawn field — the corpus only ever carries 1, 2 and 3.
`SpawnPostIirs` therefore keeps them as named `uint` constants rather than an enum: a `switch` over a
three-member enum would silently discard the other three arms, which is the `CharDCMove.MovingType` mistake.

So **the movement mode is the client's walk / run / sprint selector**, and the MoveTask record does not set the
pace at all. [V1: tools/ghidra/DumpVtableAround.java]

⚠️ Two readings of this record that look right and are NOT:

- The path trailer scalar is **not a ground speed**. `MoveTaskNodes.Path` describes the slot as "the patrol SPEED
  inside a patrol list", and it does occupy that slot — but read as u/s it runs median 6.75 (max 159) against an
  observed ~2.2 u/s for the same NPCs.
- `[duration]` is **not the time to walk the path**. Retail's own duration/length ratios come out at ~30 u/s.

Neither changes how fast the client moves an NPC; only the entity's mode does. See Gitea #714.
