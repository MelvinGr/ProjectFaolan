# CharDCMoveIIR_t (0x4517AA81) wire grammar — dead-reckoning movement

The per-tick **dead-reckoning movement update**: every moving entity (players + NPCs) streams its
position, facing and (optionally) velocity/acceleration each tick. It is the single
highest-volume GameServer world record — **13,289 instances across 11 captures** — and the core of
the simulation/movement layer (Gitea #190 / #53).

Recovered from `AgeOfConan.exe` via the vtable-xref method (`Captures/ghidra/DumpVtableReaders.java`,
slot 8 → `CharDCMoveIIR_t::ReadSubClass = FUN_007adae0`, which delegates to the inner reader
**`FUN_007ad6b0`**). Dissector + validator: `Captures/scripts/decode_chardcmove.py`
(byte-exact to the declared length for all 13,289 samples).

## Carrier framing (the 0x2000 IIR envelope)

```
[u32 len][u32 0x4517AA81][u32 0xC350][u32 handle][u8 env][ inner reader ]
```

- `len` counts from after itself (i.e. `total = 4 + len`).
- `0xC350` (50000) is the standard IIR framing constant.
- `handle` is the session entity handle (e.g. `0x01316E71` = hoxxling).
- `env` is a single envelope byte (`0x00`/`0x01`) between the handle and the inner reader — **not**
  part of the reader. The reader starts at the byte after it.

## Inner reader (`FUN_007ad6b0`) — flag-gated dead reckoning

```
[u8 flag1]                       bitfield (see below)
[float x3]   position            ALWAYS — world (x, y=height, z), big-endian floats
 bit4 set  → [float facingX]        collapsed: ONE float; with param6 = a unit-length 2D facing
 bit4 clear→ [float x3]             full post-position move vector
[float]      param6              facing/secondary scalar
[u8 flag2]                       move mode; the block below is read ONLY when flag2 < 8:
   bit2 clear→ [float x3]        velocity      (param10..12)
   bit3 clear→ [float x3]        acceleration  (param13..15)
   [float]     param16           speed/scalar
   bit1 clear→ [float x3]        heading vector (param7..9)
   [float]     param19           scalar
   [u8][u8]    two bool flags
```

All multi-byte numbers are **big-endian** (`FUN_0048f900` = ReadU32-BE; raw bytes via
`FUN_00cba1b0`). Floats are the raw-body big-endian form (read with `struct '>f'`).

### flag1 bit map (`uVar5 = flag1`)

| bit | mask | name (decompile) | effect |
|---|---|---|---|
| 1 | 0x02 | `local_d` | clear ⇒ read the `param7..9` heading vector |
| 2 | 0x04 | `local_f` | clear ⇒ read the `param10..12` velocity vector |
| 3 | 0x08 | `local_e` | clear ⇒ read the `param13..15` acceleration vector |
| 4 | 0x10 | — | set ⇒ collapse the post-position move vector to a single float |
| 5 | 0x20 | — | stored as bool `param[0x12]` |
| 6 | 0x40 | — | stored as bool |

`bit0 (0x01)` toggles freely and does not affect the layout (the `0x1a`/`0x1b` twins are the same
length). When `flag2 >= 8` the velocity/accel/heading block is skipped entirely (the short forms).

## Semantic validation

- **Position** floats are real world coordinates and drift smoothly tick-to-tick as a character
  walks (e.g. hoxxling `322.50 → 322.22 → 320.94 → 320.14`, height `y ≈ 145`).
- The `bit4`-collapsed pair `(facingX, param6)` is a **unit-length 2D facing direction**
  (`facingX² + param6² ≈ 1.0` for 11,785 / 11,884 collapsed records) that rotates as the entity turns.

## Observed flag distribution

Most traffic is `flag1 ∈ {0x1a, 0x1e, 0x3a}` with `flag2 = 0` (moving, collapsed facing, no
separate velocity/accel vector). The `flag2 = 4` forms carry the longer velocity block.

## Sibling: StopIIR_c (0xD751EBB4) — movement stop

The "stopped here" record that closes a move (`StopIIR_c::ReadSubClass = FUN_00a34700`, 7 × ReadU32-BE).
Fixed length for all 2,181 captured samples:

```
[u32 len=41][u32 0xD751EBB4][u32 0xC350][u32 handle][u8 env=0x00][float x7]
```

The 7 floats are `position(x,y,z)` + a 4-float facing/orientation, e.g. hoxxling stops at
`(294.12, 145.14, 726.28)` facing `(0.0, -0.497, 0.0, 0.868)` — the last pair is again unit length
(`0.497² + 0.868² ≈ 1.0`), i.e. the final 2D heading. Total wire size = 45 bytes.

## Status

Decoded + dissected (this doc + `decode_chardcmove.py`). **Construct DONE:** `CharDCMoveCodec`,
`StopCodec` and `SetPosCodec` (one file each under `Faolan.Core.Network/Codecs/World/`) build/parse
byte-exact, golden-tested in `DeadReckoningCodecTests`. **Wired (#190):** the sim emits a `StopIIR` (`StopCodec`)
when an NPC arrives home and stops moving (`WorldTickEffectType.NpcStopped` → `BroadcastNpcStop`); the
per-tick move still uses `MoveTaskCodec` (swapping it for `CharDCMove` is optional polish). All
self-contained records are sent via `WorldBroadcaster.SendWorldRecord` (NOT `SendWorldIir`, which would
double-prefix the codec's own length). Needs in-client verification.
