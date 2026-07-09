# The Age of Conan wire protocol — verified reference

One authoritative reference for the protocol, in which **every stated fact carries a verification tag and a
citation** that a test resolves against the repo. The machine-derivable parts are *generated*, not hand-written.

Read [`PLAN.md`](PLAN.md) for the method (the verification ladder, the anti-drift tests, the migration order).

## Layer pages (hand-written, every fact tagged)

Read top-down; each assumes the one above it.

| | Page | Covers |
|---|---|---|
| 00 | [`00-transport.md`](00-transport.md) | TCP framing, length, CRC32, the compression marker + zlib |
| 01 | [`01-crypto.md`](01-crypto.md) | login: Diffie–Hellman (g=5) + TEA-CBC; why "any password" works |
| 02 | [`02-rpc.md`](02-rpc.md) | `RpcHeader`, `ProtoEndpoint`, class-pair multiplexing, method-index vs hash dispatch |
| 03 | [`03-iir-carrier.md`](03-iir-carrier.md) | the `0x2000` carrier + BKDR-31 sub-opcodes |
| 04 | [`04-encoding.md`](04-encoding.md) | endianness (BE ints, LE protobuf fixed32, BE raw floats), varint, zigzag, framing |
| 05 | [`05-identity.md`](05-identity.md) | entity classes, Sex/Race/Class wire codes, session-volatile vs stable keys |

## Generated pages (never hand-edit)

Regenerated from the compiled types; a test fails if they drift. See [`gen/README.md`](gen/README.md).

- [`gen/opcodes.md`](gen/opcodes.md) — 819 opcodes across 16 interfaces, direction + class id
- [`gen/iir-types.md`](gen/iir-types.md) — the 32-bit IIR sub-opcodes, each with its BKDR-31 hash recomputed as proof
- [`gen/protobuf.md`](gen/protobuf.md) — every protobuf message, per-field wire encoding (zigzag/fixed surfaced)
- [`gen/coverage.md`](gen/coverage.md) — how much of the protocol is actually decoded (V3/V2/V1 burndown)
- [`gen/messages.md`](gen/messages.md) — per-message index: each IIR type → its codec (the layout spec) + the golden test that proves it

## What we do NOT know

- [`gaps.md`](gaps.md) — the register of observed-but-unproven facts (`[V0: …]`). A gap here can never go silent.

## The guarantee

Two tests keep this honest:

- **`ProtocolDocsTests`** — the `gen/` pages must match what the code generates. Change `Opcodes.cs`, the page
  changes in the same commit.
- **`ProtocolCitationTests`** — every `[Vn: …]` tag must resolve: `[V3]` → a real `[Fact]`, `[V2]` → a real
  capture, `[V1]` → a real repo path, `[V0]` → an entry in `gaps.md`.

So a claim in these pages is not "someone wrote it down" — it is "an artifact in this repo backs it, and CI checks
that the artifact still exists."
