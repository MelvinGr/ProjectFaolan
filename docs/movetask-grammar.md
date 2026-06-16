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

**ListMoveTask** (`FUN_00a3fcd0`, mode 4) reads a header (`FUN_00a48820` + bytes), a `u8 count`, then loops
`count` times calling the factory (`FUN_00a46800`) — so each list element is itself a `[mode][fields]` task.
This is the multi-segment "path": a sequence of Rotate/SetPos/… leaf moves, not a flat waypoint list (a flat
model fit only 184/5094 mode-4 bodies — the rest are longer sequences).

## Status (#53)

- **Built byte-exact** (`MoveTaskCodec`, golden tests): mode 0 bare-position, mode 2 stationary (Rotate
  to-pos+quat, the 61-B form), mode 8 approach-target.
- **Remaining**: a recursive `ListMoveTask` codec (mode 4) over the leaf builders above — needs the exact
  ListMoveTask header framing + the per-leaf flag-byte alignment pinned by a round-trip against the captured
  96–330 B bodies — then a `PatrolPath` DB table sourced from the leaf sequence.
