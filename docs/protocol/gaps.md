# Gaps — what we do NOT know

Every `[V0: …]` tag in the protocol reference must appear here, verbatim, or `ProtocolCitationTests` fails.
This is the register of things we have **observed but not proven**: a byte we reproduce because the capture says
so, without knowing what it means.

The point of this file is that a gap can never go silent. A doc that quietly omits the unknown reads as complete.

> **Not the same as the coverage matrix.** [`gen/coverage.md`](gen/coverage.md) counts *message types* with no
> builder. This file lists *facts* inside messages we DO build — the fields we copy verbatim without understanding.

Every gap below is a tracked Gitea issue, so it lives on the backlog, not just here.

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

## Minigames / Massive PvP

### The Massive-PvP board's trailing string + tail columns
`DispatchMassivePvpInfoRequestReturn` (0x20A5) is fully framed and round-trips every captured body
(`MassivePvpInfoCodec`, `docs/re/minigame-massivepvp-messages.md`), but two of an entry's columns have no meaning
attached. `otherName` is a second latin1 string after the guild name, non-empty in only 10 of the 1000 captured
rows (`"Fury (Dead)"`, `"Yellow Lotus (RP)"`), and the `[u32 tail][u8 tailFlag]` pair after it is non-zero in
exactly those same 10 rows — so the two vary together, and nothing else in the corpus moves with them. Close it
by capturing a board with more variety, or by finding the client's consumer of the two columns. Tracked in
Gitea #146.
`[V0: the Massive-PvP board's otherName / tail columns]`

### `PvPMatchRules_c` column layout
The element the matchmaking messages carry a whole match in (`DispatchPvPMatchMakingYouJoinedMatch` /
`MatchInfo` / `SearchResult`, and the c2s `PvPMatchMakingCreateMatch`). Its reader is recovered
(`FUN_0055bdb0` → `FUN_00558b70`, three u32 plus a string group, then `FUN_0053b020`) but the field split is not
pinned: **no capture contains a single instance**, because no captured session ever queued for a minigame, so
there is nothing to check a split against. Close it by decompiling the element reader field-by-field the way the
scalar dispatch bodies were done. Tracked in Gitea #146.
`[V0: the PvPMatchRules_c element layout]`

### The battlekeep info record's columns
`DispatchBattleKeepInfoRequestReturnPhase1And2` / `Phase3And4` (0x20A9 / 0x20AA) are framed exactly — a
0x3F1-list of a 7-word keep record, with a nested 4-word sub-list in the phase-1/2 form
(`docs/re/minigame-massivepvp-messages.md`). What the seven words MEAN is open. Unlike the scalar dispatch
bodies on the same interface, these readers take the words straight into the struct with no per-field bail-out
string, so the binary does not name them, and **no capture contains a single instance** — no session opened the
battlekeep UI. Close it by capturing the battlekeep window, or by finding the client's UI consumer of the
struct. Tracked in Gitea #147.
`[V0: the battlekeep info record's seven columns]`

---

## Player stats

### What else makes retail re-send the combat rating quad
The four ids — `AttackRating` 0x398, `DefenseRating` 0x39A, `MagicRating` 0x39B, `MagicalResist` 0x39D — carry one
level-derived number (`400 x level - 300`, exact on all 2,100 captured records, #685), and retail restates them in
bursts of four with a corpus-wide MEDIAN gap of 10.4 s (278 bursts over 132 dumps). Two of the occasions are now
measured and the server sends the quad on both: a LEVEL-UP (15 of 18 captured `NewLevelIIR` events draw a burst, 11
within 0.1 s) and the COMBAT TOGGLE (13 of 13 captured `PerformSpecialAction` 0x4E2 presses, 0.31-1.61 s later).
Most bursts follow neither, so what triggers the rest is open — a stat-sheet recompute is the obvious candidate, and
gear changes are the obvious thing to measure next. The VALUE is settled; the full cadence is not.
Tracked in Gitea #505.
`[V0: what else makes retail re-send the quad]`

---

## World objects

### What selects a harvest node's regrowth step
A node's health climbs back on `StatIIR` `HealthCurrent` in a fixed step on a fixed cadence, and the corpus
measures four of them across 406 node series — `+25 / 15 s`, `+16 / 60 s`, `+26 / 60 s`, `+33 / 60 s`
(`docs/subsystems/resource-harvesting.md`). What is NOT known is what picks a node's step: it is not the node's
name (two Oak Trees in one capture regrow at different steps is not something the corpus shows either way, and no
column we hold distinguishes them), not the max health (every node caps at 1000), and no RDB field has been tied
to it. Close it by finding the client-side or RDB source of the rate, or by measuring enough named nodes to prove
it is per name after all. Tracked in Gitea #494.
`[V0: what selects a node's regrowth step]`

---

## The death record's animation timing

### What varies field12 on the demon/large-caster death animation
The death `BehaviorControlCenterIIR`'s field12 (`ActivationTime`) is per-ANIMATION and reads as that animation's
own length in centiseconds — humanoid 240, quadruped/beast 100, the environmental one 300, each invariant across
every captured record of it (166 / 32 / 2). The demon/large-caster animation (`17036`) is the one that does not
hold still: 280 on 30 of its 52 records, then 240, 160, 816, 176, 360, 592 and 120. Nothing in the record or its
actor separates them, so the modal value is what we send. Close it by finding what a 816 or a 120 has in common —
the creature TEMPLATE behind the handle is the first thing to check, since one animation is shared by many
creatures and a per-creature length would explain the spread exactly. Tracked in Gitea #781.
`[V0: what varies field12 on 17036]`

### What selects the 128031 death animation
A fourth death animation exists and is PLAYER-only: two records, both on the same character, both carrying
field12 300 where an ordinary player death carries 41877 / 240. One of the two lands 8 s before that session's
own annotation *"got incinerated by lava"* — inside the normal annotation lag — while the plain *"died"*
annotation two minutes earlier maps to an ordinary 41877 death, so an environmental death appears to play its
own animation. Two records is not enough to wire a rule on and nothing selects it today. Close it with a capture
of a second environmental death (lava, drowning, a fall) — or of a fatality, which is the other candidate for a
"special" death. Tracked in Gitea #781.
`[V0: what selects the 128031 death animation]`

---

## How to close a gap

Decode it, add a golden test, then promote the fact to `[V3: …]` in the relevant layer page and delete its entry
here. If it turns out to need a capture we don't have, say so here rather than dropping it.

---

## Teams and player trade

### The team member record's four per-character opaque words
**Tracked: Gitea #101.** [V0: the team member record's four per-character opaque words] The team member descriptor
([`../subsystems/teams-trade-friends.md`](../subsystems/teams-trade-friends.md)) ends with four u32 and a flag. The
SPLIT is settled — the client's own reader `FUN_005574d0` takes exactly that, which retired the earlier
`[u32][u32][u8] + zeros` reading — but the MEANING is not. The first two words differ per character
(`0x00000301 0x59EA5C54` and `0x00018181 0x3BC035EC` for the capture's two players) while every field around them
is identical in both; the last two words and the flag are zero in both. The values appear **nowhere else in the
capture**: not in login, not in the character snapshot, not in the chat tier, so this is CS-tier state we cannot
derive from anything we send, and there is no basis for computing it. We carry it (a captured member round-trips
byte-exact) and send zero for our own members until an in-client check says what breaks. A capture where the same
character appears in two sessions would settle whether it is per-character or per-session.

### The team settings block's master-looter slot and second flag
**Tracked: Gitea #669.** [V0: the team settings block's master-looter slot and second flag] The 18 bytes that close
a `cTeam` are five fields, not one blob: `[u32 lootMode][u8 useNeedGreed][u8 flag][ProtoID32 masterLooter]
[u32 lootThreshold]`. Three of them are corroborated by `TeamSetLootOption`'s captured c2s body (`00000000 01
00000002` — the same 0 / true / 2). The other two are not. The master-looter slot is read with the ID32 idiom and
has `TeamSetMasterLooter(const ID32&)` / `DispatchTeamMasterLooterChanged` for company, but every captured roster
carries `(0, 0)`, so nothing observed distinguishes it from two more zero words; the second flag has no c2s
counterpart at all. A capture of a group that sets a master looter closes both at once.

### The two unnamed trade replies
**Tracked: Gitea #101.** [V0: the two unnamed trade replies 0x206B and 0x206D] Both are EMPTY-bodied s2c replies in
the player-trade flow and both are `Reserved0x…` (methods `0x6B` / `0x6D` on GameCharInterface) in our catalog, i.e.
the client's string table gave no name.
Their ROLE is unambiguous from the capture — `0x206B` acks your own accept, `0x206D` lands on both sides as the
trade commits (immediately followed by the `TradeCompletedSuccessfully` overlay) — so the flow is implementable; it
is the client-side NAMES that are missing, which needs the CsPlayerInterface/GameServer dispatch tables in Ghidra.

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
**Tracked: Gitea #508.** [V0: what npc_type 1 and 2 mean] `NpcStats.npc_type` (field 14) is the NPC kind, and one
of the inputs to the nameplate's rank decoration. **4** is clear from behaviour: 534 of its 897 rows are shop
vendors and 513 have a conversation, i.e. ordinary interactive NPCs.

⚠️ **This entry used to say "3 is the boss/elite rank the client draws skulls next to". That was over-claimed and
is retracted.** The evidence is one annotation — `talked to npc keira (skulls next to name. like king conan)` —
and Keaira and King Conan do both carry 3. That is sound for those **two** NPCs; extending it to all 59 rows is
not. The roster contains `Tamed Puma` and `Pleasurable Company` (a pet and a Tortage civilian), `Mama Bear`,
`Yeti` and `Decaying Corpse`, and its health spans 12,600 to 300,000,000 within the single value. A skull rank
that includes a tamed pet is not a skull rank.

**The skull selector is now PINNED, and it is not `npc_type` at all — it is `normalset` (stat 205 / `0xCD`).** The
target-portrait updater `FUN_0163eec0` reads `GetStat(0xCD, 2)` and shows both `SkullLeft`/`SkullRight` bitmaps for
`{8, 14, 15, 19, 20, 22, 23, 24, 26, 46, 47, 48, 49, 50, 51}`, hiding them otherwise. Cross-checked against the very
annotation that started this ticket: Keaira and King Conan both carry normalset 15 and both fall in the set,
reached from the client's switch without reference to the annotation. It selects **eight** of our NPCs, and four of
those eight are `npc_type == 4` (ordinary interactive NPCs), which is what finally rules out the npc_type reading.
See `NpcStatCore.Normalset`.

**What normalset MEANS is a creature RANK, not an appearance set.** The nameplate builder `FUN_011d8b00` branches
on `GetStat(0xCD, 0) == 4` and resolves the LDB category-100 token `Pray`, whose English text is **"Prey"** — and
every normalset-4 NPC we hold is a harmless critter (Badger, Chicken, Hen, Moose, Old Mouser), agreeing with the
client without being consulted. A third reader, `FUN_0163fff0`, sorts the value into four portrait tiers: `2` → 0;
`{10, 38, 39, 42}` → 1; `{15, 22, 26, 48, 51}` → 2; everything else → 3.

**The three boss tiers are a SEPARATE field, and not a stat.** The same builder reads a non-stat member — dynel
`+0x330` dereferenced at `+0x18`, valued 1, 2 or 3 — and picks the LDB texts **"Minion"**, **"Mini Boss"** and
**"Boss"**, all rendered in one place on the nameplate (confirmed in-client: Minion appears in the same spot as
Mini Boss and Boss). The client's own stat-name table has no minion/boss entry, so no stat carries this.

⭐ **That member is `npc_type` itself — which resolves this entry's own question, and #758's.** Four independent
lines:

1. **The DOMAIN is exact.** The client tests `== 1 || == 2 || == 3` and renders nothing otherwise; `npc_type`
   takes exactly `{1, 2, 3, 4}`, and `4` is the untitled majority (897 of 1,021) — the ordinary interactive NPCs a
   boss title has no business appearing over.
2. **The MEMBERSHIP matches the three labels.** `3` = King Conan, Saddur, Ape King, Commander Vakarr, Battlemaster
   Torgvall, Torin Chieftain, Head Poacher — **Boss**. `2` = Taskmaster Olaf, Pict Chief, Argus the Thief, Crow
   Nightblade — **Mini Boss**. `1` = Rabbit, Hen, Spiderling, Scorpion, Target Dummy, Vanir Berserker — **Minion**.
3. **The health ordering already noticed here agrees**: 2's members run 98k–937k against 3's up-to-300M.
4. ⭐ **It renders on OUR server**, and we send `npc_type` on every NPC (`NpcStatCoreRankTests.EveryNpc_CarriesARank`
   gates the column as fully populated). The title was observed in-client here, so whatever fills `+0x330` is
   something we already put on the wire — and `npc_type` is the only 4-valued NPC field we send whose 1/2/3 could
   feed it. See `if_it_renders_on_our_server_the_client_owns_it`.

⚠️ **One row of the reading is uncomfortable and is not being smoothed over:** value `2` also holds the Tortage
destiny NPCs (Delia, Sakumbe, Valerius), and "Mini Boss" over a friendly quest-giver's head reads wrong. Either the
builder gates the title on hostility in a part of `FUN_011d8b00` the decompiled fragment does not cover, or the
label genuinely appears there. **That is one in-client look** — stand in front of Delia and read her nameplate —
rather than the eight-candidate `+0x330` offset hunt the previous plan called for, and the offset hunt was itself
flagged as a dead end because `+0x330` collides across classes.

⚠️ **The `Difficulty` stat (1398 / `0x576`) lead is DEAD, measured not assumed.** `--report npc-generic-stats`
over the whole corpus: **23,116 NPC spawns read, 0 carry `generic_stats` at all.** Retail never sends a generic
stat on an NPC spawn, so no stat in that block can be feeding a nameplate the client draws for every mob.

Also note this makes the earlier remark "Eerika is #502's encounter boss yet is 2 while Saddur, a smaller boss,
is 3 — so 2 and 3 are not simply two tiers of the same ladder" a non-objection: under Minion/Mini Boss/Boss they
ARE two tiers of one ladder, and which side of the line a designer put a given encounter on is their call, not a
contradiction.

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

### What records a learned tradeskill
**Tracked: Gitea #735.** [V0: what records a learned tradeskill] A character must LEARN a tradeskill profession,
which comes in ranks, and that is what gates the recipe book. We DO carry the state and DO send it correctly — it
rides the spawn scaffold's feat/ability list (`CharacterSkills`, `[u8 rank][u32 id]`), imported per character and
replayed verbatim, which is why an imported character's professions match retail. What is unknown is which of
the tier is recorded in. The profession itself is settled: a profession is a `CharacterSkills` row whose
`Rank` byte is 65–75, and that byte IS the profession id — 5 crafting + 6 gathering is exactly 11 slots, with
70–75 the gathering six in the client's list order (fixed at the end by a single-profession control: the
assassin's only profession is Miner and his only row is slot 75) and 65–69 the crafting five. What is NOT in
the table is the TIER: slot 72 (Skinner) carries the identical `SkillId` `328908` at Master on one character
and Novice on another, and slot 73 (Stonecutter) `326000` at Master and Apprentice. So neither the rank byte
nor the id encodes Novice…Grandmaster. Around it:

- `TradeskillFlags` (stat `0xCE`) is **not** it — it rides NPCs (a *Dockworker* carries 99), not the player.
- `Craftmanship` / `MaxCraftmanship` (`0x88` / `0x89`) DO ride the player and move (0 → 1, 0 → 2 → 3 in early
  play), which fits slots-used / slots-allowed — but an L80 who actually crafts reads max 2, so it is unproven.
- Learning is GameAction `53198 LearnRecipe` (one argument, key 39), fired from RDB content rather than an RPC;
  untraining is `CraftUntrain(int nCraftID)`, a separate id space.

No capture teaches a profession, so the character-side state is unobserved. The bounded next step is an RDB sweep
for `53198` instances: each is a recipe taught by a specific piece of content, and grouping them by source should
recover the professions without a capture.

### Whether a harvest node under one swing's worth can still be harvested
**Tracked: Gitea #732.** [V0: whether a node under 100 health can still be harvested] The decrement itself is no
longer a gap — a swing costs exactly **100** of the 1000 max and grants exactly **1** unit, so a full node is ten
swings (`docs/subsystems/resource-harvesting.md`). What is still unmeasured is the FLOOR. Retail leaves nodes
sitting at 31 and 28 and lets them regrow, so the older "bottoms out at 1, then despawns" was what those
particular captures' nodes happened to do, not a rule; and nobody in any capture swung at a node holding less
than 100, so whether a partial pool pays out a partial (or a whole) unit is unknown. We require a full swing's
worth and stop there rather than invent a payout.

The retail swing sequence is still undecoded: the client selects the node with `LookAtIIR` carrying class
`0xC879` (`EntityClass.Destructible`) and then sends `BehaviorControlCenterIIR` swings (`BehaviorId.UseDynel`,
`0x12`), but the cast time is not established. One capture of a player taking a node from full to empty settles
the floor.

---

## Raids

### The raid fan-out's routing
**Tracked: Gitea #669.** [V0: the raid fan-out's routing] **No session in the corpus ever forms a raid** — a sweep
of all 120 dumps finds zero raid traffic of any kind — so every one of the 30 messages in
[`../subsystems/raids.md`](../subsystems/raids.md) comes from the client's own tables rather than from retail
bytes. Their PARAMETER LISTS are the client's (`docs/re/aoc-client-rpc-methods.json`) and their BODY LAYOUTS are its
own readers, which is as certain as static RE gets: a wrong shape desyncs the client's stream and it says so by
name. What is NOT settled is who retail sends each one to. Ours is the team flow one level up — the whole raid
learns a join, the joining side learns the whole raid — which is the same analogy #104 records for guilds and is
not evidence. A retail raid on tape settles it.

### The `cMatch` tail
**Tracked: Gitea #669.** [V0: the cMatch tail] `DispatchYouJoinedRaid`'s body ends, after the member teams and the
per-character flag map, with an ID32, a word, a flag, a second ID32 and three more words. The client's reader
(`FUN_005584c0`) gives their ORDER and WIDTH and nothing else — it stores each straight into a field of the raid
object. The two bools of each flag-map entry are in the same position. We send zeros, which is the same "we model
none of this" the empty flag list states. The raid UI is what consumes them, so an in-client raid with a debugger
on those fields would name them; a capture would too.

### What a `cMatch`'s HEADER team is
**Tracked: Gitea #669.** [V0: the cMatch header team] A `cMatch` opens with a whole `cTeam` and then lists the raid's
member teams. Nothing says what the header one is FOR. We send it as the raid's own header — the raid id, the raid
leader, an EMPTY member list and the raid's settings — because listing the raid's people there as well as in the
sub-teams would duplicate them, and because the settings have to live somewhere. The alternative reading, that the
header is the leader's team repeated, is not excluded by anything. Same for the raid id's entity CLASS: it rides
the slot a team id rides, so we stamp `0xDEA9`.

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

### Which mount grants which mount ability
**Tracked: Gitea #303.** [V0: which mount grants which mount ability] Mount abilities come with the mount, not
with a class (see [`../subsystems/player-actions.md`](../subsystems/player-actions.md)) — but the corpus cannot
say which mount brings which ability. Its two mount owners each own a horse **and** a mammoth, and each holds all
six captured mount abilities, so nothing separates "Mammoth Charge comes with the mammoth" from "these six come
with owning any mount". The names suggest the first, and that is exactly why it is written down here rather than
implemented: a name is not a mapping. Neither the mount's spell record (its whole property run is ten fields, none
of them an ability reference) nor the ability's own name carries the link. A capture of a character who owns ONE
kind of mount settles it in one sample.

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

### Retail's 0x003D on a guild leave/kick
**Tracked: Gitea #663.** [V0: retail's 0x003D on a guild leave/kick] The `~Guild` chat-channel SUBSCRIPTION is
captured twice (a create and a guilded login - see
[`../subsystems/create-guild-decode.md`](../subsystems/create-guild-decode.md)), but no session in the corpus
ever leaves or is kicked from a guild, so the drop is not. We send the `ChannelUnsubscribe` (0x003D) on that
channel: the same message the corpus does show retiring a `~Team` when the group dissolves and a `~Playfield` on
a zone change, whose body is exactly the `(type, id)` pair the subscription opens with. The MESSAGE is retail's;
applying it to the guild case is the inference. A two-client guild leave settles it, alongside #104's fan-out.

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

### What sets the MagicBuff apply payload's hostile flag
**Tracked: Gitea #658.** [V0: what separates the 134 from those 554] `descriptor[4]` of the 54-byte
`MagicBuffIIR` apply payload is a boolean — its only non-zero value is `1` — and it is clear on every one of the
2,241 captured SELF-applies, so "the caster is not the target" is a necessary condition for it. It is not a
sufficient one: 554 of the 688 non-self applies leave it clear. The ten spells that do carry it are hostile
debuffs and DoTs (`Pestilential Blast (Rank 6)`, `Flesh to Worms (Rank 5)`, `Mark of the Devourer`, `Repenting`,
`Poisoned`, `Ice Shackle`), which is a lead and not a rule — nothing we hold marks a spell hostile, and the
sweep of the spell's own RDB 1040005 record (every head word, every property field, and every bit of both) finds
nothing that separates the two populations. `--report buff-descriptor` prints the cross-tab.

### Whether an interpolated ACG item's flags can differ from its template1's
**Tracked: Gitea #756.** [V0: whether an interpolated ACG item's flags can differ from template1's] The client's
bind-on-pickup test is bit 26 of `AcgItemData_t::GetInterpolatedItem(quad)[+0xC8]`, and that dword is the item's
`Flags` word — `InventoryGame_c::AddTemplateItem` copies it straight into stat 0, and retail's arrivals match the
template's own `Flags` 174 times out of 179. On that reading `Bindings of Dark Terror` (3762969, template `Flags`
`0x04000001`) binds on pickup, yet its in-client GM tooltip renders *"Binds when Equipped"* with no `Character
Bound` prefix, which the disassembled branch only reaches when the pickup predicate returned **false**. Two
explanations were open: the interpolated item's flags are not template1's verbatim (ACG composition — #752), or
`GetInterpolatedItem` failed to resolve the degraded quad our server sent at the time (`flags = 1`, `template2`
flattened onto `template1`). **Neither survives as stated.** #756 put the real `Flags` word on the wire and #752
the real template pair, and this template carries `template2 == template1` anyway, so there is no composition to
do. One in-client look now settles whether the tooltip still renders wrong.

### The gem/slot colour match
**Tracked: Gitea #754.** [V0: the gem/slot COLOUR match - the client asserts on "Invalid gem slot colour" and the
stat table names GemSlot1Color..GemSlot4Color (694-697) against the gem's own GemColor (698), but neither is stored
yet] Socketing enforces the socket COUNT (`Item.GemSlots`, template field 114) and not the colour. The client
validates both — it draws each slot in its colour and asserts on a mismatch in `ItemInfoView::UpdateFromItem` — so
a stock client cannot send a mismatched pair and the gap only matters against a modified one. Closing it is two
more harvested fields, not a decode: `--report gem-slots` already prints them (a 4-slot weapon declares
`[12,13,14,15]`, a 3-slot item `[7,8,9,0]`).

