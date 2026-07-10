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
