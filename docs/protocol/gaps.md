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

### RpcHeader trailing headerData
**Tracked: Gitea #319.** `PacketStream.WriteHeader` accepts an optional `headerData` blob appended after the u16
opcode, inside the header-length. Characterized across all captures (2026-07) — it is exactly **four** cases, not
a single mystery grammar:
- **1-byte `0x01`** — the overwhelming majority (7366 packets), and it is a **constant** `0x01`. Rides the latency
  ping/pong (`0x2085` c2s on GameCharAgent / `0x2082` `DispatchPong` s2c on GameCharInterface) and a handful of
  CSPlayerInterface dispatches (`0x20A5`/`0x20C5`/`0x20D0`). Emitted verbatim; its *meaning* (a flag? a fixed
  marker?) is the only real remaining unknown here, and it's a single constant byte.
- **4-byte** — the PlayerAgent/PlayerInterface **32-bit method id** (DECODED via #320; see
  `docs/re/aoc-rpc-method-opcodes.md`, `PlayerAgentMethodIds`). This interface uniquely carries it.
- **3-byte `0x94A760`** — PlayerInterface `SmallCharList` (0x20A5), a constant (88/88 captures).
- **13-byte** — PlayerInterface `Ox1A07` (OverrideGameTweaks), a per-session block on the ServiceConnection
  endpoint; still emitted verbatim (its internal structure is the residual undecoded part).

So `headerData` is NOT a generic per-opcode grammar: it is the PlayerInterface method-id (decoded) plus a constant
`0x01` latency marker plus two PlayerInterface specials. Residual: the semantics of the `0x01` marker and the
13-byte `Ox1A07` block.

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
