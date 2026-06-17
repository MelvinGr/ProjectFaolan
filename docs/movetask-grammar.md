# MoveTaskIIR_c (0x96C46740) wire grammar — RECURSIVE move tasks

Recovered from `AgeOfConan.exe` (Ghidra `/tmp/ghidra-aoc/proj`; scripts `Captures/ghidra/DumpMoveTask.java`
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

The `else` branch is the **patrol waypoint list** (the count-prefixed 3-float positions). The exact per-waypoint
trailer (whether `FUN_00a31ec0` consumes bytes past the 3 floats) and the `flags & 1` packed branch are the
remaining byte-alignment to pin against the captured bodies.

Mode-1/5/6/7 leaf **type names** recovered from their ctors (`DumpMoveTaskLeafReaders.java`): 1 = PathMoveTask,
5 = BallisticMoveTask, 6 = AnimatedMoveTask, 7 = FormationMoveTask (5/6/7 readers not yet field-mapped).

## Status (#53)

- **Built byte-exact** (`MoveTaskCodec`, golden tests): mode 0 bare-position, mode 2 stationary (Rotate
  to-pos+quat, the 61-B form), mode 8 approach-target.
- **Decoded (2026-06-17), not yet built**: the recursive structure is fully pinned — the `ListMoveTask` header
  (`FUN_00a48820` Wait-base + the min byte + `u8 count`) makes the recursive parse consume every mode-4 body to
  its leaves; `PathMoveTask` is decoded down to its `[u32 count][3-float waypoints]` list. Scripts:
  `Captures/ghidra/DumpMoveTaskList.java` + `DumpMoveTaskLeafReaders.java`.
- **Remaining**: pin PathMoveTask's per-waypoint trailer + its `flags&1` packed branch, field-map the
  Ballistic/Animated/Formation leaves, then a recursive `MoveTaskCodec` over the leaf builders + a `PatrolPath`
  DB table that groups ordered waypoints per path (today's `PatrolPaths` table is flat, ungrouped).
