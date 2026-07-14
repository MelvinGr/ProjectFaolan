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

### RpcHeader trailing headerData — the `0x01` marker's meaning
**Tracked: Gitea #319.** The blob's grammar is decoded — four per-opcode/constant cases (see [`02-rpc.md`](02-rpc.md)
and `Captures/scripts/decode_rpc_headerdata.py`). The 13-byte `OverrideGameTweaks` case, previously called an opaque
block, is now decoded too: it is a protobuf endpoint reference to PlayerInterface (class `0x7D155738`) + a per-session
instance id. The **only** thing left is the *semantic* of the constant 1-byte `0x01` marker (a flag? a version?
present on ping/pong `0x2085`/`0x2082` and a few CSPlayerInterface dispatches, always exactly `0x01`). We emit it
correctly (`KeepAliveService`); its label needs the client's generic RPC-header parser (Ghidra) — low ROI, on no
send path that depends on the meaning.

---

## RPC

### PlayerAgent 32-bit hash dispatch
**Tracked: Gitea #320.** Every RPC interface dispatches on a 16-bit method index (`0x2000 + registration index`); PlayerAgent/
PlayerInterface **additionally** carries a 32-bit method hash on the wire. This is a dump task, not a hash to reverse: the
login-critical method set is recovered and verified end-to-end (a Frida-under-Wine hook on the client send path correlated
with a loopback capture) and carried as real associated ids (`PlayerAgentMethodIds`, appended by `WriteHeader`; tests lock
the bytes). The interface is only PARTIALLY enumerated in `gen/opcodes.md` — the methods the server does not implement
(character create/delete/rename, Facebook token, hardcore, offline-levels, …) still lack their wire (opcode, id) pairs,
harvestable via the same Frida technique. Remove this V0 once all 17 interfaces are fully covered.

---

## IIR bodies

### FeedbackIIR and RegenIIR are still verbatim blobs
Both are replayed from captured bytes rather than constructed (they show **V1** in the coverage matrix, correctly).
Tracked as the create-burst verbatim residue in Gitea #223.

---

## How to close a gap

Decode it, add a golden test, then promote the fact to `[V3: …]` in the relevant layer page and delete its entry
here. If it turns out to need a capture we don't have, say so here rather than dropping it.
