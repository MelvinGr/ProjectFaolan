# 03 — The IIR carrier

The world is not sent as bespoke packets. It is sent as a stream of **IIR records** (Incremental Information
Replication) wrapped in one carrier opcode. This page is that wrapper and its 32-bit sub-opcode space.

---

## The carrier

`GameCharInterface` opcode **`0x2000`** (`DispatchPassBlob`) carries an opaque blob. Inside that blob is one IIR
record, tagged with a 32-bit type id:

```
[u32 BE length][u32 sub-opcode][u32 typeTag = 0x0000C350][u32 handle][record body …]
```

- **sub-opcode** — the 32-bit IIR type (the subject of this page).
- **typeTag** — the entity class of the subject, usually `0x0000C350` (Character). See
  [`05-identity.md`](05-identity.md#entity-classes).
- **handle** — the live entity the record is about (`0` for global records like the map change).

A worked example — the world/system chat line is a length-framed carrier around this exact shape.
[V3: ChatTextCodecTests.Build_FromParsedFields_MatchesCapture]

## Sub-opcodes are BKDR-31 hashes

The 32-bit sub-opcode is the **BKDR-31 hash of the client's IIR type name** — the same hash family as the RPC
class ids in [`02-rpc.md`](02-rpc.md). BKDR-31 is `h = h*31 + c` over the ASCII name (Java's `String.hashCode`).

Names carry a `_c` / `_t` suffix and lowercase the `n3` prefix before hashing (`n3ToClientQuit_c`, etc). The
generated catalog recomputes the hash from the recovered name and shows the match, so the mapping is
**self-verifying** rather than asserted: [`gen/iir-types.md`](gen/iir-types.md).

## Where the sub-opcode lives in code

Every codec that emits an IIR **declares** the type(s) it emits, so the sub-opcode is never a bare literal:

```csharp
public const uint Opcode = (uint)GameServer0X2000Opcodes.ChatTextIIR;          // single type
public static readonly GameServer0X2000Opcodes[] HandledTypes = [ … ];         // more than one
```

A test forbids a bare IIR hex literal anywhere in `Codecs/`, which is what lets `grep GameServer0X2000Opcodes.<Type>`
find every emitter. [V3: CodecConventionTests.NoCodecSourceContainsAMagicIirOpcodeLiteral]

That declaration is also what [`gen/coverage.md`](gen/coverage.md) reads to attribute each type to its builder, so
coverage is exact, not name-guessed.

## Multi-type records

Some record shapes are shared by several sub-opcodes. The dynel-family full-update (door, destructible,
tombstone, climbing) is one codec reading the type from the record; it declares all four via `HandledTypes` and
round-trips each byte-exact. [V3: WorldObjectCodecTests.DynelFullUpdate_RoundTripsByteExact]

## Construct, don't replay

The direction of travel is to build each record from typed DB columns, not replay a captured blob. Two records
that already build wholly from typed inputs and are pinned byte-exact:

- the death sequence's kill record [V3: WorldDeathTests.BuildKilledBy_ReproducesCapturedRecord_ByteExact]
- the death-pose MoveTask [V3: WorldDeathTests.BuildDeathPose_ReproducesCapturedRecord_ByteExact]

Records still replayed verbatim are tracked in [`gaps.md`](gaps.md) and [`gen/coverage.md`](gen/coverage.md) (the
V1 rows).

## See also

- [`gen/iir-types.md`](gen/iir-types.md) — the 74 sub-opcodes with self-verified hashes.
- [`gen/coverage.md`](gen/coverage.md) — which are decoded, and to what verification level.
- [`04-encoding.md`](04-encoding.md) — how the fields inside a record body are encoded.
