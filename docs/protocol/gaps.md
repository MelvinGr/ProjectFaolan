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

---

## Teams and player trade

### The team member record's 9-byte per-character opaque
**Tracked: Gitea #101.** [V0: the team member record's 9-byte per-character opaque] The 102-byte team member
descriptor ([`../subsystems/teams-trade-friends.md`](../subsystems/teams-trade-friends.md)) ends with nine bytes
that DIFFER per character — `0x00000003 0x0159EA5C 0x54` and `0x00000181 0x813BC035 0xEC` for the capture's two
captured players — while every field around them is identical in both. The value appears **nowhere else in the capture**:
not in login, not in the character snapshot, not in the chat tier, so it is CS-tier state we cannot derive from
anything we send, and there is no basis for computing it. We carry it (a captured member round-trips byte-exact) and
send zero for our own members until an in-client check says what breaks. Read as `[u32][u32][u8]` because only that
last byte varies and the three after it are zero in both records; a second capture with more characters — ideally
one where the same character appears in two sessions — would settle both the split and whether it is per-character
or per-session.

### The two unnamed trade replies
**Tracked: Gitea #101.** [V0: the two unnamed trade replies 0x206B and 0x206D] Both are EMPTY-bodied s2c replies in
the player-trade flow and both are `Reserved0x…` in our opcode catalog, i.e. the client's string table gave no name.
Their ROLE is unambiguous from the capture — `0x206B` acks your own accept, `0x206D` lands on both sides as the
trade commits (immediately followed by the `TradeCompletedSuccessfully` overlay) — so the flow is implementable; it
is the client-side NAMES that are missing, which needs the CsPlayerInterface/GameServer dispatch tables in Ghidra.

### Which team-member field carries the LEVEL
**Tracked: Gitea #101.** [V0: the team member record's level] The 102-byte member descriptor's leading trailing-u32 is
the CLASS (`0x29` Necromancer / `0x2C` Demonologist, corroborated by the chat server's friend-add reply). No field in
it is identifiable as the level, because the two captured members share every remaining value: the candidates are the
per-character word that reads 2 and 3, and the constants 6 / 354 / 51100 / 40016. A capture with two members of
DIFFERENT levels settles it in one look; until then we send the level nowhere, so the client's team window may show a
wrong or blank level even though grouping works.

### The friend/guild state stamp's ONLINE value
**Tracked: Gitea #426.** [V0: the friend state's online stamp] The chat-tier friend state
(`[u32 id][u8 online][u8 level][u32 stamp][u8 class]`) carries a u32 whose OFFLINE meaning is solved — a unix last-seen,
identified because the captured offline friend's `0x6A64AD51` is 2h06 before that capture was taken — while the ONLINE
value is not decoded. It is not a timestamp (`0x01EDA8B2` reads as 1971-01-10) and not a constant (five captured friends
carry `0x01EDA8B2`, one carries `0x01EDAF93`, 1761 higher). Its magnitude and high word sit right beside the character
ids in the same capture (`0x0131xxxx` vs `0x01EDxxxx`), so an id or handle is a better guess than a time, and the
`0x0025` presence push carries a third value of the same shape. We send the captured constant for every online friend.
A capture with online friends in DIFFERENT zones would test the location hypothesis in one look.

