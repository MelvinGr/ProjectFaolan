# CharDCMoveIIR_t (0x4517AA81) wire grammar — dead-reckoning movement

The per-tick **dead-reckoning movement update**: every moving entity (players + NPCs) streams its
position, facing and (optionally) velocity/acceleration each tick. It is the single
highest-volume GameServer world record — **13,289 instances across 11 captures** — and the core of
the simulation/movement layer (Gitea #190 / #53).

Recovered from `AgeOfConan.exe` via the vtable-xref method (`tools/ghidra/DumpVtableReaders.java`,
slot 8 → `CharDCMoveIIR_t::ReadSubClass = FUN_007adae0`, which delegates to the inner reader
**`FUN_007ad6b0`**). Dissector + validator: `CharDCMoveCodec`
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
 bit4 set  → [float qy]             collapsed rotation: the ONE non-zero vector component of a yaw-only facing
 bit4 clear→ [float x3]             the full rotation vector part (qx, qy, qz)
[float]      qw                  the rotation's scalar part (the decompile's `param6`)
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
| 4 | 0x10 | — | set ⇒ send the rotation as `(qy, qw)` instead of `(qx, qy, qz, qw)` |
| 5 | 0x20 | — | stored as bool `param[0x12]` |
| 6 | 0x40 | — | stored as bool — **AIRBORNE (a jump)** |

**Bit 6 (`0x40`) is a jump**, pinned by `xxx_2clients_jumping` — a session recorded for nothing else. It appears in
c2s moves ONLY there (20 records; none in a comparable non-jumping capture), and retail RELAYS it: both clients
received `0x40` moves naming the *other* subject, plus five further characters including a passer-by who happened to
jump nearby. Walking is `0x1E`; jumping is `0x56` — the same minus `NoAccelerationVector`, plus this bit, which fits
an acceleration vector arriving exactly as the character leaves the ground. So a viewer renders someone else's jump
from the ordinary move record, with no separate animation needed.
[V1: tools/capture/tshark_dump.sh]

It stayed unnamed until that capture for a good reason: the old invented enum paired `0x52`/`0x56` with a "jump" label
while those same values occur while SEATED, so the pair was not diagnostic. Isolating a jumping session is what makes
this bit diagnostic on its own — the bits either side of it (`0x20`, and `flag2`) are still only "stored as bool".

`bit0 (0x01)` toggles freely and does not affect the layout (the `0x1a`/`0x1b` twins are the same
length). When `flag2 >= 8` the velocity/accel/heading block is skipped entirely (the short forms).

## The relay is VERBATIM — the c2s record with its env byte cleared

A move is forwarded to the mover's own client (the echo) **and** to every other client that can see the mover as the
**same bytes**, changing only `env` (`0x01` → `0x00`, "I am the controlling client"). Measured over the whole corpus:
**9,438 of 9,932 c2s `CharDCMove` records have an s2c twin identical bar that byte** (the remainder sit in captures
with partial coverage, e.g. one that has 0/59). In `xxx_2clients_jumping` all 35 do, on both clients' streams — the
jump at packet idx 1339 reappears at idx 1340 (the jumper's echo) and idx 1342 (the watcher). [V2: xxx_2clients_jumping.tshark.json]

So the server has nothing to re-encode here: it re-frames the mover's own carrier per recipient. That is also what
makes the flag-gated layout a non-issue on the relay path — a rebuild has to know how many floats bit4 implies and
where the jump's acceleration vector sits; forwarding does not.
[V3: CrossPlayerMoveRelayTests.BroadcastPlayerMove_ForwardsTheJumpVerbatim_ToAViewerWhoKnowsTheMover]

## The post-position floats are a ROTATION QUATERNION

The decompile's names for this region — a "post-position move vector" plus the scalars `facingX` / `param6` — are
what the client's reader calls its locals, and they hide what the field is. The floats are the entity's **orientation
quaternion**, and bit4 does not "collapse a vector": it drops the two components that are zero for an upright
character.

| | bit4 clear | bit4 set |
|---|---|---|
| on the wire | `[qx][qy][qz]` then `[qw]` | `[qy]` then `[qw]` |
| records | 3,250 | 19,642 |
| `\|q\|² = 1` within 1e-4 | **100%** | **100%** |

Unit-length across every captured record in both forms is already decisive (a "move vector" has no reason to be),
and the yaw it implies confirms the reading: over sustained straight runs (three consecutive collapsed records with a
consistent travel direction, 1,634 samples), `2·atan2(qy, qw)` matches the direction of travel within **7.5° for 90%**
of them, and within 22.5° for 97%. The factor of two is the tell — a heading pair would be `(sin θ, cos θ)`, giving
the angle directly; a quaternion gives the HALF-angle, and reading it as a heading is off by θ/2, which is why an
earlier attempt at `atan2(facingX, param6)` scattered with a 57° median error and looked unpinnable.
[V2: Hoxxling_mage-heraldofxotli_login_gameplay.tshark.json]
[V3: DeadReckoningCodecTests.CharDCMove_RotationIsAUnitQuaternion]

The same four floats close **StopIIR** (below): unit-length for 100% of its 3,540 records too, so its "4-float
rotation" is this same quaternion, not a heading embedded in four floats.

This is what the move handler feeds the live entity (and, through it, the persisted facing and the spawn record other
players get). Before it was decoded, the handler read three floats blindly and passed them to a Euler→quaternion
conversion as DEGREES: in the collapsed form that mixed `qy` and `qw` with the `flag2` byte reinterpreted as a float
(the captured jump reads 0.479 / 0.878 / 1.5e-36), so every character's facing was pinned within about a degree of
identity.

## Semantic validation

- **Position** floats are real world coordinates and drift smoothly tick-to-tick as a character
  walks (e.g. hoxxling `322.50 → 322.22 → 320.94 → 320.14`, height `y ≈ 145`).

## Observed flag distribution — every value, measured

`flag1` is a **bitfield, not a movement type**, and there is no name to recover for it: the client has no
`MovingType` symbol, and its own reader calls the bits `local_d` / `local_e` / `local_f`. The record carries no
"walking / mounted / falling" mode at all — the client drives its animation from its own state machine (its warnings
name states like `sitonitem` / `WalkForwardStart`, which are animations, not wire values).

Counting `[u8 env][u8 flag1]` over every capture — **19,034 CharDCMove records, 19 distinct `flag1` values across
both env bytes (37 pairs)**:

| flag1 | bits | count (env 0x00 + 0x01) | reads |
|---|---|---|---|
| `0x1A` | 1,3,4 | 3368 + 2183 | collapsed rotation, velocity |
| `0x1E` | 1,2,3,4 | 3320 + 3036 | collapsed rotation only |
| `0x3A` | 1,3,4,5 | 1687 + 1530 | collapsed rotation, velocity, bool5 |
| `0x0A` | 1,3 | 684 + 653 | full rotation, velocity |
| `0x2A` | 1,3,5 | 638 + 671 | full rotation, velocity, bool5 |
| `0x72` | 1,4,5,6 | 244 + 190 | collapsed rotation, velocity, accel, bool5+6 |
| `0x52` | 1,4,6 | 171 + 116 | collapsed rotation, velocity, accel, bool6 |
| `0x0E` | 1,2,3 | 133 + 142 | full rotation |
| `0x56` | 1,2,4,6 | 40 + 35 | collapsed rotation, accel, bool6 |
| `0x42` | 1,6 | 29 + 29 | full rotation, velocity, accel, bool6 |
| `0x62` | 1,5,6 | 26 + 26 | full rotation, velocity, accel, bool5+6 |
| `0x12` | 1,4 | 19 + 17 | collapsed rotation, velocity, accel |
| `0x02` | 1 | 11 + 11 | full rotation, velocity, accel |
| `0x16`, `0x22`, `0x32`, `0x3E`, `0x46` | — | 1–4 each | rare combinations |
| `0x00` | none | 4 | reads everything, incl. the heading vector |

Bit 0 is **never set** — every observed value is even. Bit 1 is set in all but the all-zero form, so the
`param7..9` heading vector is almost never present.

⚠️ This is why treating the u16 as an enum does not work. A `MovingType` enum with 11 observed pairs matched
14,414 of the 19,034 records; the other **4,620 (24%)** fell through its `switch` and were logged and dropped -
that player's movement silently ignored. The handler now reads `[env][flag1]` and processes every record
(`MoveFlag` carries the bit names).

## Sibling: StopIIR_c (0xD751EBB4) — movement stop

The "stopped here" record that closes a move (`StopIIR_c::ReadSubClass = FUN_00a34700`, 7 × ReadU32-BE).
Fixed length for all 2,181 captured samples:

```
[u32 len=41][u32 0xD751EBB4][u32 0xC350][u32 handle][u8 env=0x00][float x7]
```

The 7 floats are `position(x,y,z)` + the same rotation QUATERNION the move carries, here always in full: hoxxling
stops at `(294.12, 145.14, 726.28)` facing `(0.0, -0.497, 0.0, 0.868)` — the `x = z = 0`, `qy² + qw² ≈ 1` shape of a
yaw-only facing, written out unmasked. Unit length holds for 100% of its 3,540 captured records. Total wire size =
45 bytes.

## Status

Decoded + dissected (this doc + `CharDCMoveCodec`). **Construct DONE:** `CharDCMoveCodec`,
`StopCodec` and `SetPosCodec` (one file each under `Faolan.Core.Network/Codecs/World/`) build/parse
byte-exact, golden-tested in `DeadReckoningCodecTests`. **Wired (#190):** the sim emits a `StopIIR` (`StopCodec`)
when an NPC arrives home and stops moving (`WorldTickEffectType.NpcStopped` → `BroadcastNpcStop`); the
per-tick move still uses `MoveTaskCodec` (swapping it for `CharDCMove` is optional polish). All
self-contained records are sent via `WorldIir.Send`, which writes the carrier's `[u32 len]` because that length
belongs to the sender (#530 rule 2); the codecs emit a bare record. (This used to read "via `SendWorldRecord`,
NOT `SendWorldIir`, which would double-prefix the codec's own length" — both senders and that hazard are gone:
#530 moved every self-prefixing codec onto the bare-record shape and left exactly one sender.) Needs in-client
verification.
