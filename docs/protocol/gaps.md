# Gaps — what we do NOT know

Every `[V0: …]` tag in the protocol reference must appear here, verbatim, or `ProtocolCitationTests` fails.
This is the register of things we have **observed but not proven**: a byte we reproduce because the capture says
so, without knowing what it means.

The point of this file is that a gap can never go silent. A doc that quietly omits the unknown reads as complete.

> **Not the same as the coverage matrix.** [`gen/coverage.md`](gen/coverage.md) counts *message types* with no
> builder. This file lists *facts* inside messages we DO build — the fields we copy verbatim without understanding.

Every gap below is a tracked Gitea issue (`melvin/projectfaolan`), so it lives on the backlog, not just here.

---

## Transport

### compression marker bytes 4..7
**Tracked: Gitea #318.** The stream-compression control frame is `[80 00 00 05][4 bytes][flag]` — 9 bytes at a packet boundary. The tag and
the trailing flag are understood (`flag == 0` ⇒ the rest of the direction is one zlib deflate stream). The four
bytes between them are copied/skipped without interpretation. Our server never opens a compression stream, so
nothing depends on them; a client-side reader (`Captures/ghidra/DumpFns.java` territory) would settle it.
**Ghidra note (2026-07):** the `0x80000005` tag is NOT a literal constant in the client (a `FindConst` scan hit
only signed-division magic numbers) — it is detected by the **high bit of the u32 length field** (`length &
0x80000000` ⇒ control frame; low bits `= 5` ⇒ compression-stream-open). So the 4 bytes are the stream's
parameter (a length/id for the inflate setup). Full decode needs the high-bit dispatch path in the receive loop
or a capture carrying a live stream (the login captures on hand don't) — deferred as low-ROI since we skip it.
**Decompressor confirmed (2026-07):** the client statically links **zlib 1.2.5** — inflate core `FUN_01c2c440`
(carries "incorrect header check"/"invalid window size"/"unknown compression method"), wrappers `FUN_00cbc880`/
`FUN_00cbc3c0`. So the 4 bytes feed a zlib inflate setup; identifying which field (uncompressed length? window?)
means tracing the receive loop → those wrappers, and can only be *validated* against a live stream we don't have.

### RpcHeader trailing headerData — residual sub-fields
**Tracked: Gitea #319.** The blob's GRAMMAR is decoded — it is four per-opcode/constant cases, not a generic
grammar (see [`02-rpc.md`](02-rpc.md) and `Captures/scripts/decode_rpc_headerdata.py`, which tallies them across
every capture). Two sub-fields remain emitted verbatim, and only these:
- the **meaning** of the constant 1-byte `0x01` latency marker (a flag? a fixed sentinel?), and
- the **internal structure** of the 13-byte PlayerInterface `OverrideGameTweaks` (`0x1A07`) per-session block.

Both are client-reader-decodable (Ghidra) if the fidelity ever matters; neither is on any path we send.

---

## RPC

### PlayerAgent 32-bit hash dispatch
**Tracked: Gitea #320.** Every RPC interface dispatches on a 16-bit method index (`0x2000 + registration index`) **except**
PlayerAgent/PlayerInterface, which uses a 32-bit hash. The login flow works because we hard-code the handful of
values we observed; the hashing function has not been recovered, so we cannot enumerate the interface the way
`gen/opcodes.md` enumerates the other 16.

---

## IIR bodies

### FeedbackIIR and RegenIIR are still verbatim blobs
Both are replayed from captured bytes rather than constructed (they show **V1** in the coverage matrix, correctly).
Tracked as the create-burst verbatim residue in Gitea #223.

---

## How to close a gap

Decode it, add a golden test, then promote the fact to `[V3: …]` in the relevant layer page and delete its entry
here. If it turns out to need a capture we don't have, say so here rather than dropping it.
