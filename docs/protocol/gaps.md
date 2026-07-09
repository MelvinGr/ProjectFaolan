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

### RpcHeader trailing headerData
**Tracked: Gitea #319.** `PacketStream.WriteHeader` accepts an optional `headerData` blob appended after the u16 opcode, inside the
header-length. Several captured packets carry it; we reproduce it verbatim per call-site. Its grammar is not
decoded, and it is not obviously per-opcode.

---

## RPC

### PlayerAgent 32-bit hash dispatch
**Tracked: Gitea #320.** Every RPC interface dispatches on a 16-bit method index (`0x2000 + registration index`) **except**
PlayerAgent/PlayerInterface, which uses a 32-bit hash. The login flow works because we hard-code the handful of
values we observed; the hashing function has not been recovered, so we cannot enumerate the interface the way
`gen/opcodes.md` enumerates the other 16.

---

## IIR bodies

### ChatTextIIR trailer word 0x40000001
**Tracked: Gitea #321.** `ChatTextCodec` writes a constant 5-byte trailer (`0x00` then u32 `0x40000001`) after the XML. It is constant in
the single capture we have, so we emit it verbatim. A second sample with a different value would let the two
halves be field-named.

### FeedbackIIR and RegenIIR are still verbatim blobs
Both are replayed from captured bytes rather than constructed (they show **V1** in the coverage matrix, correctly).
Tracked as the create-burst verbatim residue in Gitea #223.

---

## How to close a gap

Decode it, add a golden test, then promote the fact to `[V3: …]` in the relevant layer page and delete its entry
here. If it turns out to need a capture we don't have, say so here rather than dropping it.
