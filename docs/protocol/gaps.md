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
and `tools/capture/decode_rpc_headerdata.py`). The 13-byte `OverrideGameTweaks` case, previously called an opaque
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
the bytes). `PlayerAgentMethodIds` maps 9 c2s methods (auth, startup, login, character create/delete, …) plus the
observed s2c responses. The interface is only PARTIALLY enumerated in `gen/opcodes.md` — the methods never yet observed
on the wire (character rename, Facebook-token save, hardcore, offline-levels, …) still lack their (opcode, id) pairs,
harvestable via the same Frida technique. Remove this V0 once all 17 interfaces are fully covered.

---

## IIR bodies

### FeedbackIIR and RegenIIR are still verbatim blobs
Both are replayed from captured bytes rather than constructed (they show **V1** in the coverage matrix, correctly).
Tracked as the create-burst verbatim residue in Gitea #223.

---

## Combat

### Weapon damage scale / the melee damage formula
The weapon min/max damage FIELDS are decoded (RDB item-template 286/285, `docs/rdb/item-template-stats.md`, #395),
but the exact SCALE those raw values display/combat at, and the real per-hit melee formula, are not: retail computes
damage server-side (spell-power × weapon × mitigation × gear, a GDB formula not in the client exe or a plain wire
field). `CombatResolver` uses a provisional ÷100 scale and a weapon-avg-plus-level figure. Close it by correlating
captured DamageResult numbers against a known equipped weapon, or tune in-client. Tracked in Gitea #395.
`[V0: exact combat scale/formula — the real per-hit damage is a server-GDB formula, undecoded]`

### Armor mitigation formula
The defensive analogue (#401). The client mitigation function `FUN_0113ba00` (log tag `GameCode/ArmorMitigation`)
is RE'd in SHAPE — physical branch: `TotalArmorValue = clamp0((C3·0.01+1)·armor(0x1c0) + (Strength(0x324)−10)·0.1·C4)`
(+ invulnerability `0x1c6` when flagged), then a level-normalized percent
`((2.0 − level(0x36)/stat(0x3c5))·TotalArmorValue) / (level·C1) · 100`. The static consts (0.01/1.0/0.1/2.0/100) read
straight from the exe, but the coefficients **C1/C3/C4** (globals `0x02d24704`/`24`/`34`) are 0.0 statically — they
load from runtime BALANCE CONFIG (server-GDB, same class as the weapon scale above), so the exact numbers need a live
Frida dump of those globals or in-client tuning. `CombatResolver.ApplyPhysicalMitigation` uses a provisional
`raw·K/(armor+K)` curve (`K=2000`) as a stand-in. The magical branch (damage types 4/5/10) uses `0x14e`+`0x1c3`/`0x1ca`
scaled by Int/Wis. Tracked in Gitea #401.
`[V0: exact armor mitigation coefficients — C1/C3/C4 are runtime GDB config, not static in the exe]`

---

## How to close a gap

Decode it, add a golden test, then promote the fact to `[V3: …]` in the relevant layer page and delete its entry
here. If it turns out to need a capture we don't have, say so here rather than dropping it.
