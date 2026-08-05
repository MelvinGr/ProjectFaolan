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
| 7  | (0x28 obj)          | `FUN_00a46440` | — |
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
| 7 | FormationMoveTask  | `[float x6]`, no header (`FUN_00a4a9d0`) |

The PathMoveTask finalizer `FUN_00a3e400` = `[u8 bool][7 u32]` (a fixed 29 B; its sibling `FUN_00a3e510` only
validates the spline and reads nothing), so both PathMoveTask branches end with it.

## Decode coverage

- **Decode COMPLETE (2026-06-17)**: the full recursive grammar is pinned — `MoveTaskCodecTests`
  round-trips **9468/9468 captured MoveTask records (100%)** byte-exact. Scripts: `DumpMoveTaskList.java`,
  `DumpMoveTaskLeafReaders.java`, `DumpPathHelpers.java`, `DumpMoveTaskTail.java`.
- **Built byte-exact** (`MoveTaskCodec`, golden tests): mode 0 bare-position, mode 1 PathMoveTask
  (`BuildPathBody` — the multi-waypoint path; `BuildWalkBody` — the 2-point walk segment), mode 2 stationary
  (Rotate, the 61-B form), mode 8 approach-target. The full recursive form (incl. `ListMoveTask`) is covered by
  `MoveTaskCodec` (parse + re-emit any record byte-exact — `MoveTaskCodecCorpusTests`, all 9468 records).
- The grouped `PatrolPath` + `PatrolWaypoint` DB tables — ordered `Waypoints` by `Ordinal` — exist; the
  broadcaster emits mode-1 path moves via `BuildWalkBody` for chase steps and re-addresses the captured patrol
  body via `SendPatrol`.
