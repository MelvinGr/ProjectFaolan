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

---

## RPC

---

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
The client's own **ARMOR_MITIGATION table is RDB type 1000514, and it is EMPTY on this build** - 16 bytes,
a valid header declaring zero entries (`docs/rdb/client-data-tables.md`). So the table lookup the client
prefers can never match and the closed form is always taken, which closes the one route that would have
avoided the coefficients. They really do need a live dump.

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
the player-trade flow and both are `Reserved0x…` (methods `0x6B` / `0x6D` on GameCharInterface) in our catalog, i.e.
the client's string table gave no name.
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
value's id SPACE is not.

What the client does with it is no longer a guess. `FriendListNode_c`'s buddy-apply (`FUN_006ca170`, anchored on its
own `m_nCharacterId == cBuddy.nUserId` assert in `FriendListNode.cpp:118`) copies three of the `ppj::Buddy_t` dwords
straight through and treats this one differently: it uses it as a **key into a global id→string map**
(`FUN_0043ac00`, an rbtree find on the singleton at `DAT_03c03100`) and renders the hit's string — falling back to the
literal `"unknown"` on a miss. So it is an **id whose NAME the client displays**, not a time (which also explains why
`0x01EDA8B2` reading as 1971-01-10 never made sense), and five friends sharing one value with a sixth 1761 apart is
five people in one named place and one elsewhere.

What is left is naming the map: identify what fills `DAT_03c03100` (populated via `FUN_01389630` / `FUN_0138b7c0`)
and the id space follows. ⚠️ Note the magnitude while doing it — `0x01EDA8B2` is ~32M, the same order as a
**playfield token** (`0x004FE428`, `0x0802ABCD`) and nothing like a `Maps.Id` (4000, 4010, 11040); if it is the
playfield table, the key is the token rather than the map id. We send the captured constant for every online friend,
so today every online friend renders as whatever that id happens to name.


### What `npc_type` 1 and 2 mean
**Tracked: Gitea #508.** [V0: what npc_type 1 and 2 mean] `NpcStats.npc_type` (field 14) is the NPC kind that
drives the nameplate rank, and **3 is the boss/elite rank the client draws skulls next to** — identified from the
annotation `talked to npc keira (skulls next to name. like king conan)`, whose two NPCs both carry 3, and
corroborated by the 56-row roster that value selects. **4** is equally clear from behaviour: 534 of its 897 rows
are shop vendors and 513 have a conversation, i.e. ordinary interactive NPCs.

The two small groups are not separated. **1** (55 rows) is cleanly non-interactive — zero vendors, zero
conversations, mostly hostile trash — so "plain creature" fits, but nothing rules out a narrower meaning.
**2** (10 rows) is the genuinely unexplained one: it mixes Tortage destiny NPCs (Delia, Sakumbe, Valerius) with
named enemies (Eerika of the Maidens, Pict Chief, Torgrim Blackfrost, Arnulf Iceblade) and two Wharf Rat
Scoundrels, which is not one obvious category. Note Eerika is #502's encounter boss yet is 2 while Saddur, a
smaller boss, is 3 — so 2 and 3 are not simply two tiers of the same "named" ladder.

⚠️ The obvious RE route is closed: the client stores its protobuf field names only inside the serialized
`FileDescriptorProto` blobs, so `npc_type` is **not** a defined string in `AgeOfConan.exe` and cannot be anchored
on (`tools/ghidra/DumpNpcTypeRank.java` establishes this — PART A finds no hit). Naming 1 and 2 means finding the
NpcStats field-14 reader through the generated parser and following it to the nameplate decoration, or finding a
client data table that enumerates the kinds.

### The skill-point curve above level 1, 5, 23, 24 and 80
**Tracked: Gitea #507.** [V0: the skill-point pool for the 75 uncaptured levels] `CharacterStatId.SkillPoints`
(stat `0x0F3`) is the pool the client reports as "gained N skillpoints", and it is a pure function of LEVEL —
identical for every character at a level regardless of class (two level-23 characters of different classes both
carry 402; two level-24 characters both carry 642). So it is not an award to grant on a trigger.

What is missing is 75 of the 80 rows. The five measured are 0, 154, 402, 642, 1892 at levels 1, 5, 23, 24, 80,
and that is **not** a smooth curve — 23→24 jumps 240 in one level while 5→23 averages under 14 — so it is a
per-level table with no formula to interpolate. `SkillPointCurve` therefore returns null for an unmeasured level
rather than inventing a value the client would display as authoritative.

⚠️ It is not in the client data tables: 1000505–1000508, 1000511, 1000513, 1000514 and 1000516 were dumped and
none contains 154, 402, 642 or 1892 in either endianness. The cheap source is captures, not RE — one login
snapshot per level fills a row with no decoding at all.

### The harvest health decrement per swing
**Tracked: Gitea #494.** [V0: the harvest health decrement per swing] A harvest node's health is measured only at
the ENDS: it starts at 1000 and bottoms out at **1**, after which the node despawns. What happens in between is
not recoverable from the corpus, because each node is streamed **once** with whatever health the shared playfield
has left it at — the partial values (901, 701, 601, 501, 382, 169, 137, 31, 21, 11) are other players harvesting
the same nodes, not a per-swing curve. So the server empties a node in one harvest rather than inventing a
decrement.

The retail swing sequence is undecoded for the same reason: the client selects the node with `LookAtIIR` carrying
class `0xC879` (`EntityClass.Destructible`) and then sends `BehaviorControlCenterIIR` swings, but how many swings
and what cast time are not established. A capture of one player emptying a single node from full settles both this
and the decrement in one go.

---

## Quest sharing

### The quest-share message bodies
**Tracked: Gitea #101.** [V0: the quest-share message bodies] No capture performs a quest share — teams, trade and
the friend list all came off `login_2-clients_group-trade-friend`, but neither client in it ever shares a quest — so
the five `0x2047`-`0x2054` bodies ([`../subsystems/quest-sharing.md`](../subsystems/quest-sharing.md)) are recovered
from the client's own readers rather than observed, and nothing gates them byte-exactly. Two of the recovered facts
would have been guessed wrong: `ShareQuestAccepted`'s `InstanceID_t` is **one** u32 (its proxy `FUN_00500960`
byte-swaps a single dword and streams 4 bytes, where the ID32 writer streams 8), and
`DispatchQuestShareIndividualFeedback`'s `i_cFeedbackValue` is a **string**, not the ID32 its `i_c` prefix implies
(`FUN_004c5a50` calls the string primitive). Closing this needs a retail two-client share on tape, or an in-client
check that the window opens and the accept grants.

### The quest-share feedback codes
**Tracked: Gitea #101.** [V0: the quest-share feedback codes] The twelve codes and which of the two feedback methods
each rides are read off the client's two jump tables (`FUN_01396ef0` accepts 0,1,2,5,6,7; `FUN_013970e0` accepts
3,4,8,9,10,11), and each case's LDB message key names it. That makes the SET exhaustive and the names certain. What
it does not settle is when retail SENDS each one, and two are not implementable from it at all:
`ShareFeedbackLevelTooLow` / `…TooHigh` need the level band a share is checked against, which no field of the quest
catalog states, and `ShareFeedbackDoesNotHaveExpansion` needs a per-account expansion entitlement we do not model.

### The quest-share range and timeout
**Tracked: Gitea #101.** [V0: the quest-share range and timeout] The share window's own tick (`FUN_01220bb0`) drops
it with `QuestSharePlayerMovedOutOfRange` once the sharer is further than **60.0** units, which is where the server's
range test comes from — but that is the CLIENT's leash, and whether retail's server applies the same distance before
offering (rather than only while the window is open) is not established. The window also times out on a countdown
whose tick units are not pinned; nothing server-side depends on it, since the client closes its own window.

---

## Guilds

### The guild management fan-out
**Tracked: Gitea #104.** [V0: the guild management fan-out] Leave, kick, promote, demote and the invite reply are
the guild actions **no capture performs**, so the nine bodies in
[`../subsystems/guilds.md`](../subsystems/guilds.md) come from the client's `CSPlayerInterfaceDispatch_t` readers
rather than from retail bytes, and nothing gates them byte-exactly. The FIELD widths are certain (each reader names
its fields in its own assert strings, and the primitives are unambiguous - `FUN_0048f900` u32, `FUN_0048f840`
string, `FUN_00cba1b0(dst,1)` one byte). What is NOT settled is who retail sends each one to, and the CONTENT of
`DispatchGuildInviteReplyToInviter`'s `i_cMessage` - a free string we fill with the invitee's name because that is
the only thing the inviter's toast can need. A two-client retail guild-management session settles all of it.

### Which guild ranks may kick, promote and demote
**Tracked: Gitea #104.** [V0: which guild ranks may kick, promote and demote] The same gap the bank withdrawal has,
one rung along: the client ships a per-rung permission BITMASK in RDB 1000518 (strictly nested, `0` subset `30`
subset `286` subset `6430` subset `7518` subset `0x7FFFFFFF`), which is presumably what gates these - but which BIT
is kick, which is promote, and whether promote is even permission-gated rather than leader-only, is not recoverable
from the table alone. We allow the LEADER only, the narrowest rule that cannot be abused, rather than guessing a
threshold that would quietly let the wrong members remove people.

### The guild bank's two message bodies
**Tracked: Gitea #132.** [V0: the guild withdrawal body] [V0: the guild cash update body] `GuildWithdrawMoney`
(c2s `0x2035`) and `DispatchGuildUpdateCash` (s2c `0x2052`) are the only guild-bank messages that exist —
there is no deposit method, because guild money is fed by the guild TAX (`0x2029`). Neither body is captured, and
that is not an accident of coverage: a sweep of all 215 captures finds **zero** guild-bank traffic, because no
captured session ever opened the bank UI.

Both are recovered from the client instead. Its method table declares `GuildWithdrawMoney(uint64 amount)` and
`DispatchGuildUpdateCash(i_nCash)`, and every sibling scalar-parameter method on these interfaces writes its
argument as a bare big-endian value with no header — so a one-parameter method's body IS its parameter. The u64
width matches `Guild.Cash`. What that does NOT settle is whether the update carries the balance or a delta; we
send the balance, because `i_nCash` names a total and the request already carried the amount.

Closing this needs an in-client check (open the guild bank, withdraw, see whether the window updates), not a
capture we could go and find.

### Which guild ranks may withdraw from the bank
**Tracked: Gitea #132.** [V0: which guild ranks may withdraw from the bank] Retail has per-rank bank permissions;
nothing in the corpus, the client's method table or RDB 1000518 (the rank-name ladder) says which rung they hang
off. We allow the LEADER only — the narrowest rule that cannot be abused — and refuse everyone else, rather than
guessing a threshold that would quietly let the wrong members drain the bank.
