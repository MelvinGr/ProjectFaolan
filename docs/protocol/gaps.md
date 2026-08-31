# Gaps — what we do NOT know

Every `[V0: …]` tag in the protocol reference must appear here, verbatim, or `ProtocolCitationTests` fails.
This is the register of things we have **observed but not proven**: a byte we reproduce because the capture says
so, without knowing what it means.

The point of this file is that a gap can never go silent. A doc that quietly omits the unknown reads as complete.

> **Not the same as the coverage matrix.** [`gen/coverage.md`](gen/coverage.md) counts *message types* with no
> builder. This file lists *facts* inside messages we DO build — the fields we copy verbatim without understanding.

Every gap below is a tracked Gitea issue, so it lives on the backlog, not just here.

⚠️ **`ProtocolCitationTests` cannot check that the tracked issue is still OPEN**, only that the V0's text appears
in this file. So a gap whose ticket closes goes silently untracked with the gate green — which is exactly what
happened to four of them (#132, #426, #498, #508 all closed with their gaps still live; refiled as #795–#798 on
2026-08-11). The test has no network, so this cannot be automated in the suite. Audit it by hand when closing a
ticket that owns a gap:

```zsh
python3 - <<'EOF'
import json, urllib.request, re
tok = open('<your gitea token path>').read().strip()
for n in sorted({int(m) for m in re.findall(r'Tracked: Gitea #(\d+)', open('docs/protocol/gaps.md').read())}):
    d = json.load(urllib.request.urlopen(urllib.request.Request(
        '<gitea api base>/repos/<owner>/projectfaolan/issues/%d' % n,   # the concrete host/owner is in the agent memory
        headers={'Authorization': 'token ' + tok})))
    if d['state'] == 'closed':
        print('⛔ #%d is CLOSED but still tracks a gap: %s' % (n, d['title']))
EOF
```

### The hostile-flip `CarsGroup` restate's second copy
When a conversation turns its NPC on the player, retail restates the creature's collision group to the mob one
before anything else — `StatIIR [class 0xC350][handle][env 0x00][stat 0x41A][3]` — and that record is what makes
the flip real to the client (see [combat.md](../subsystems/combat.md)). It is sent **twice**: two separate packets
in one TCP segment, byte-identical, with no second value and no `env` 0x00/0x01 pair. 3 of 3 in the corpus, and
those three are the only character `CarsGroup == 3` records in all 134 captures. We replay both because the
capture says so.

⭐ **The doubling is not specific to the hostile flip** (`--report duplicate-records`). Retail sends a whole
FAMILY of stats as an exact back-to-back pair, every time — measured over 30,125 `StatIIR` records:

| stat | pairs | singles |
|---|---|---|
| `WaypointType` | 336 | 336 |
| `SoulFragments` | 56 | 56 |
| `GuildID` | 38 | 38 |
| `MovementMultiplier` | 35 | 35 |
| `CarsGroup == 3` (the flip) | 3 | 3 |

A doubled count *equal* to the single count is one "first" and one "repeat" per event — i.e. **always** a pair.
`FeatTrainerID` is doubled 15,829 times against 348 singles, so it is nearly always one too. So the second copy
is a property of retail's stat SENDER across a set of stats, not something about turning an NPC hostile, and the
"two emitters on one event" reading is an ordinary shape rather than a puzzle specific to this record.

⚠️ What remains unknown is why retail's sender doubles *at all*, and that is about its server internals: it is
recoverable from neither the client (which merely applies what it is told) nor the corpus (which shows the
behaviour without explaining it), so Gitea #842 is CLOSED as answered in substance rather than left open for a
route that cannot exist. This entry is the residue.
`[V0: why retail's stat sender emits a pair for this family of stats.]`

### The spell FLAGS word's bits 8 and 19

`FUN_007ebe70` (on the magic apply route, called from 13 sites and from the damage path) runs a block only when
**both** bit 19 and bit 8 are clear, so either bit suppresses it. The block builds and emits something through
the client's string/feedback machinery, but the emitted text is not resolved, so what is being suppressed is
named only structurally. Bits 8 and 19 are therefore one question, not two. See
[spell-record-fields.md](../rdb/spell-record-fields.md). Tracked in Gitea #632.
`[V0: what the block bits 8 and 19 suppress actually does.]`

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

### What a quest pays in XP and cash
Completing a quest awards nothing on our server, and the payout cannot be measured here (Gitea #820).

⭐ **Retail never sends the player's experience in a stat update.** `--report quest-xp` over the whole corpus:
**10,016** `StatUpdateIIR` stat blocks parse cleanly, carrying **20,213** records across **33** distinct stat
ids, and `CharacterStatId.Exp` appears in **none** of them. Its only live carrier is the level-up ding
(`NewLevelIIR`), which fires ~20 times in 134 captures — so no experience delta in the corpus isolates a single
quest. `xxx_…_play4` holds exactly one live Exp reading, the ding at *Rescue Arias*' completion, so any delta
ending there spans the entire 334-second session before it; the tightest completion-spanning interval anywhere
is 128 s.

What the corpus *can* state is a lower bound, for a quest whose completion levels the character: the ding
carries the running total and the threshold just crossed, and the award that fired it took the player from
below that threshold to that total. Quest 10830 paid **≥ 532** at level 7, quest 10915 **≥ 13** at level 6.

⚠️ Mining is exhausted — the limit is the wire, not the sample size, so more talk-completed quest completions
would add nothing. The stored candidates are all eliminated already: `XpReward`/`MoneyReward` are populated on
21 of 5,049 quests and zero on every quest of the captured chain, the RDB catalog's per-line reward disagrees
with both the value and the cadence, and the client's tuning table holds no reward curve (its level curve is
server-side here too).
`[V0: what a quest pays in XP and cash]`

### What governs a creature's respawn interval
The interval itself is measured (`--report respawn-interval`): a killed creature comes back **on a new entity
handle** at its old spawn point, at **150 s** for Brutal Pirate (7 agreeing kills, 0.3 s spread, 2 unrelated
captures) and **120 s** for Wild Cow (2 kills, 2 captures; Wild Pig's one clean reading of 119.9 s agrees).

**These are timers, not delivery artifacts**, and the argument needs no assumption about streaming range: the
Brutal Pirate kills behind the 150 s cluster are **62 s apart within one capture**, so no single moment of the
player returning can put every fill at a fixed offset from its own death — and the cluster spans two sessions,
which share nothing at all.

What is NOT known is what SELECTS one number over the other, and the corpus **cannot** decide it: the only two
creatures with a measured timer are a beast in **Poitain** (2070) and a humanoid in **City of Tortage** (4020), so
creature identity and playfield are perfectly confounded. Nine candidate attributes survive the cross-tab —
map, level, health, community, superfamily, npc_type, normalset, race, monster_data — and only `faction_id` and
`profession_type` are eliminated, by being unset on every measured creature. The client is no help either: the
tuning sheet (1000516) carries only world-boss/festival schedule tweaks, and the RDB spawn layout (1010506) is
consumed to EOF with no respawn field.

**Nor is it a harvesting problem — the corpus is exhausted.** Of 251 kills that matched nothing, 151 never see
that creature again in the dump at all and 89 more only ever see a *re-streamed* handle (an entity the client
already knew, which is not a respawn); loosening the anchor radius would add ten candidates, all 50–300 u from the
spawn point. So `NpcTemplate` has no respawn column and `WorldEntity.DefaultRespawnDelaySeconds` gives every
creature the best-attested 150 s.

⭐ **One capture decides it**: kill a **Panther in City of Tortage** — a beast on the humanoid's playfield — and
stand there for three minutes. 120 s means the creature picks the number; 150 s means the playfield does. (A
humanoid killed in Poitain is the same experiment from the other side.) Tracked in Gitea #855.

⚠️ Two traps this cost a cycle each. The obvious `death(handle)` → `spawn(same handle)` test finds **nothing**,
because a respawn is a new entity id. And "the client watched for N seconds and nothing came back" is not a usable
bound — it cannot tell a missing respawn from a client that was out of range when it happened; the capture showing
`Brutal Pirate 0xaa88` back at 150.0 s also holds a 334 s window in which it "never returned".
`[V0: what governs a creature's respawn interval — creature vs playfield are confounded in the corpus]`

⭐ **The client says the answer is PER-ENTITY, which makes a single interval the wrong shape of question.** Stat
**146** is named `RespawnTimeInSeconds` — the client carries a per-entity respawn delay, in seconds, as an
ordinary stat (`docs/re/aoc-client-stat-names.tsv`; LDB category 615 labels the same id *"Respawn Time"*, so the
naming is corroborated from two sources). So retail does not pick between "120 s" and "150 s" globally: each
creature carries its own value, and the two candidate figures this gap weighs are two creatures' values rather
than two hypotheses about one constant.

⛔ **And it is never sent.** Stat 146 has **zero** occurrences across all 134 captures
(`--report stat-series --report-arg 146`), which is what a server-side scheduling field looks like — the client
is told a creature spawned, never when it will. That closes the "read it off the wire" route as firmly as the
measurement above closes the "infer it from timings" one, and leaves the ticket's own conclusion intact: it needs
a deliberate stationary capture, per creature.

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

### The `Phase1And2` battlekeep record's highlighted ID32
`DispatchBattleKeepInfoRequestReturnPhase3And4` (0x20AA) is decoded byte-exact (`BattleKeepInfoCodec`,
`docs/subsystems/guild-cities-and-sieges.md`) and every column is named from the client's keep window: owner and
attacker are guild ID32s, the time is the NEXT phase's start, and the phase is LDB category 10032 (War Declaration
/ Skirmish / Silence before the storm / War / Aftermath). Its `Phase1And2` twin (0x20A9) is recovered from the
client reader — the keep record plus a nested attacker-ranking list of `[ID32 guild][kills][total]` — but has NO
captured body (the frame once taken for one is a GameCharInterface loot reply with the same lossy wire rendering),
and one of its words stays unnamed: the ID32 at struct `+0x20` that the window renders as the ONE yellow row of
the attacker list. The client does not use it to find the viewer's own guild (the Withdraw/DeclareWar predicate
walks the list for that), and retail's battle-queue feedback names a designated *Top Guild* per keep
(`TopGuildInQueue` / `NoLongerTopGuildInQueue` / `WinnerOfQueue`), so the reading is the queue's Top Guild; a body
in phase 0 or 1 showing the highlighted row is the top-ranked one would close it. Tracked in Gitea #147.
`[V0: the Phase1And2 record's highlighted ID32]`

### `PvPMatchRules_c` C/D/E/H columns
The element the matchmaking messages carry a whole match in (`DispatchPvPMatchMakingYouJoinedMatch` /
`MatchInfo` / `SearchResult`, and the c2s `PvPMatchMakingCreateMatch`). Its layout is recovered field-by-field
from the client, and **eight of its twelve fields are now named from the client's own consumers** — the
community-server debug view, the `PvPMinigame` Flash API and the match views (`A` match type, `B` map, `F` time
limit, `G` score limit, `I` queue type, the trailing match id, the `FlagB` fifo/extra-words gate) — in the
`i_cMatch` section of `docs/re/minigame-massivepvp-messages.md`. What is left is `C`, `D`, `E`, the string,
`FlagA` and `H`, which are only INFERRED from column types and the debug command's defaults (`2`, `3`–`6`,
password + has-password): the one view that would name them, the PvP search-result list, is dead code in this
build — no call site constructs its row item, the page it sits on is never opened, and the client never sends
`PvPMatchMakingSearchForMatches` (its only sender has no caller), so a `SearchResult` body cannot be captured by
playing (Gitea #894) — and **no capture contains a single instance** of the element. Close it by filling a queue
in a capture (`YouJoinedMatch` / `MatchInfo` carry the same element), or by an in-client run of the create-match
window with known values typed into it. Tracked in Gitea #146.
`[V0: the PvPMatchRules_c C/D/E/H columns]`

### `MinigameScore_c`'s six unnamed scalars
The score record `DispatchMinigameFullScore` (0x2088) carries is carved field-by-field from its reader
(`FUN_00559940`, in `docs/re/minigame-massivepvp-messages.md`): four u32s, the per-character score map, two more
u32s, two side maps, one more u32 and a third list into the second side map's container. Its named parts are the
ones the client's own consumers read back — the score map is what `GetTotalCharacterScore` walks and what the
status view renders as Kills / Deaths / Score, the two side maps are what `GetSideScore(side, 0|1)` looks a side
up in, and `+0x4c` lands on the singleton's match-start clock (`+0x1b8`) that `GetTimeLeft` computes from. The
six scalars at `+0x04`, `+0x08`, `+0x0c`, `+0x10`, `+0x24` and `+0x28`, and the fifth value word of a score
entry, have no consumer that names them. **No capture contains a single instance** — no captured session ever
scores in a minigame — so this is why the server ends a match without pushing a score. Close it by capturing a
minigame with kills in it, or by finding a consumer for those offsets on `PvPMatchMaking_c` (`+0x170`, `+0x174`,
`+0x178`, `+0x17c`, `+0x190`, `+0x194`). Tracked in Gitea #146.
`[V0: the MinigameScore_c scalars at +0x04/+0x08/+0x0c/+0x10/+0x24/+0x28 and the entry's fifth value word]`

### The matchmaking board's innermost map key
`DispatchPvPMatchMakingInfoRequestReturn` (0x2092) is a three-level count-framed map that round-trips all 43
captured bodies (`PvpMatchMakingInfoCodec`), but its innermost key is **0 in every one of them**. It is read as
`queueType` because that is the one field both `PvPMatchMakingRequestInfo` and `PvPMatchMakingPutIntoMatchMaking`
carry and both hold at 0 throughout — inference from the neighbouring messages, not measurement. Close it with a
capture where a client asks about, or signs up under, a non-zero queue type. Tracked in Gitea #882.
`[V0: the innermost map key of the matchmaking board]`

### `i_AsTeamMember` on a matchmaking signup
`DispatchPvPMatchMakingYouAreInMatchMaking` (0x2080) carries `[u8 i_ShowFifo][u8 i_AsTeamMember]`, and the eight
captured acks are `1, 0` without exception — the capture was solo, and `i_bBringTeam` is 0 in all eight signups
that caused them. So the corpus shows only one side of both flags. The server sets `i_AsTeamMember` on the
members a `bringTeam` signup drags along, which is the field's evident purpose but is not something a capture has
confirmed. Close it by queueing as a team. Tracked in Gitea #882.
`[V0: i_AsTeamMember with a team]`

---

## Dungeons and instances

### What makes a playfield a dungeon, beyond the ones the group finder queues into
`DispatchInDungeonPlayfield` (GameCharInterfaceProject `0x07`) is a one-byte per-playfield flag and the corpus
carries both values, so it is real state rather than a constant. What it is state ABOUT is only half-pinned:
both captured `01` frames land in a playfield the group finder queues into (Black Castle 3001, The Amphitheatre
of Karutonia 1092) and the lone `00` is an ordinary city world-entry, so "the finder queues into it" is what
separates the two populations and is what the server sends on
(`GroupFinderActivityCatalog.IsFinderDestination`, Gitea #882). That is certainly NARROWER than "is a dungeon" —
a dungeon walked into on foot must send `01` too — but no capture shows one, and no client data table we hold
carries a dungeon flag to widen it with. Close it by capturing a walk-in dungeon entry, or by finding the
client-side table retail's server read this off. Tracked in Gitea #309.
`[V0: the dungeon flag for a playfield the finder does not queue into]`

### `DispatchGroupFinderRemoved`'s `silent` flag
The trailing byte of the finder's removal push (CSPlayerInterface `0xC5`; the client's own argument list is
`(reason, silent)`). It is 1 on the login push and 0 on every other captured frame — the instance exit and all
three `LeaveGroupFinder` answers — and three values do not say what makes the login one different. The server
sends each captured reason with the flag that reason carried. Tracked in Gitea #882.
`[V0: DispatchGroupFinderRemoved's silent flag]`

### `DispatchSendPlayfieldLockoutData`'s six `lockoutInfo` fields
The instance reset/lockout push (GameCharInterface `0xAA`) is fully framed - the dispatcher reader
(`FUN_004cddb0`) and the composite reader it calls (`FUN_0077df00`) give
`[u32 playfieldId]` then `[string][u32][u32][u32][u8][string]`, with no nesting and no list, so the message is
byte-constructible today (`docs/subsystems/playfield-instance-ownership.md`).

What is missing is what the six carry. The reader names only `lockoutInfo` as a whole - there are no per-field
bail-out strings the way the guild-name refusals have - and **no capture in the corpus carries this message**, so
there is nothing to correlate the columns against. A reset timer and an instance name are the obvious readings
and are exactly the guess that must not be shipped. Tracked in Gitea #309.

`[V0: DispatchSendPlayfieldLockoutData's six lockoutInfo fields]`

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

### The team-search row's trailing string
**Tracked: Gitea #889.** [V0: the team-search row's trailing string is the looking-for-members message] A
`DispatchTeamSearchResult` row is a whole `cTeam` followed by a string - that much is the client's own reader
(`FUN_0134e9c0` calls the `cTeam` reader `FUN_00557960` then the string read `FUN_0048f840` per element). WHAT the
string says is not, because all six captured replies are the empty result: no traced session had a team flagged as
looking for members, so no row exists to read. It is sent as the team's looking-for-members advert, on the grounds
that this is the only per-team string the LFG feature set has - `SetLookingForMemberStatus(bool, i_cMessage)` and
`DispatchTeamLookingForMemberStatus(i_bLookingForMember, i_cMessage)` carry it for the same team - but a single
in-client search against a flagged team settles it, and would also settle whether retail lists unflagged teams at
all.

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

### The friend/guild state stamp's ONLINE value — SOLVED (Gitea #888, closed 2026-08-22)
The chat-tier friend state (`[u32 id][u8 online][u8 level][u32 stamp][u8 class]`) carries a u32 that is a UNION on
the online flag. Both halves are now observed:

* **OFFLINE — a unix LAST-SEEN.** Identified because the captured offline friend's `0x6A64AD51` is 2h06 before that
  capture was taken, and confirmed on four more records in `NecroL80_..._annotated_9`, whose stamps decode to dates
  spread across 2011, 2016 and 2021 — dormant friends, which is what a last-seen looks like.
* **ONLINE — the friend's PLAYFIELD-INSTANCE TOKEN**, per friend.

⭐ **What settled the online half is a WITHIN-CAPTURE, PER-FRIEND match, not a resemblance.** In `addfriends`, each
of the five online friends' stamps appears again inside **that same friend's own record** in the who-result
(`DispatchCharacterSearchResult`), ~72 bytes in — exactly where `TeamMemberRecord`'s embedded `PlayfieldProxy`
carries its instance token. Four friends carry `0x01EDA8B2` and `Llewyl` carries `0x01EDAF93`, and the who-result
reproduces that split friend-for-friend, so it is not one shared session constant being seen twice.

Two independent corroborations from the same corpus:

* the player's OWN `LoginConnectReady` handoff ends in a `PlayfieldProxy` (`0x62` version byte, class `0xC79C`,
  map `0x0FB4`, `0x9C50`, token) whose token is that same `0x01EDA8B2` — the observer and the four friends are in
  one instance;
* in `xxx_2clients_jumping` two online friends carry `0x01EDB057` / `0x01EDB082`, values that appear **nowhere
  else in that capture** — friends in another instance, which is what a per-instance token predicts and what a
  timestamp (these read as 1971) cannot explain.

The RE trail below stands and is kept: it is what established the union, and its conclusion — that the client
files the online value and never reads it back — is why the meaning had to come from the wire rather than from a
consumer.

⚠️ **This entry used to say the stamp is a key into a global id→string map. That was a MIS-ATTRIBUTION and is
withdrawn.** The map is real — `FUN_0043ac00`, an rbtree find on the singleton at `DAT_03c03100` — and it is now
named: `FUN_0138b7c0` fills it by dynamic-casting to **`RDBPlayerClassData_c`**, i.e. the **class registry**
(`[u32 classId][string name][u32 tier]`, 17 records, ids `0x00`–`0x2C` — see
[`client-data-tables.md`](../rdb/client-data-tables.md)). Its key is `ppj::Buddy_t` dword **9**, and dword 9 is the
record's **class byte**, not the stamp. So that lookup renders the friend's CLASS NAME, and `"unknown"` is what an
unrecognised class id shows — it says nothing about the stamp.

⭐ **The client's own parser confirms the whole record and splits the stamp itself.** The `S2C_ADD_BUDDY` case of
`FUN_00a71e00` unpacks it with the format string **`"IBBIB"`** — exactly `[u32 id][u8 online][u8 level][u32 stamp]
[u8 class]` — and then stores the stamp in **two different members depending on the online flag**: offline into
`Buddy_t[10]`, online into `Buddy_t[12]`. Two slots means two meanings, stated by the client rather than inferred,
which is direct support for the union reading. The buddy-apply copies `[8]` (level), `[11]` (online) and `[10]`
(the offline last-seen) and never touches `[12]` at all, so whatever consumes the ONLINE stamp is not the list node.

⭐ **And the store fires a SIGNAL, which is where the reader will be found.** Read off the disassembly rather
than the decompilation (`0x00a72426`–`0x00a7247b`), the branch is exactly

```
[ESI+0x2c] = online ; [ESI+0x20] = level ; [ESI+0x24] = class
online == 0  ->  [ESI+0x30] = 0      ; [ESI+0x28] = stamp     (offline: the last-seen)
online != 0  ->  [ESI+0x30] = stamp  ; [ESI+0x28] = 0         (online:  the token)
```

and BOTH branches then `CALL FUN_0069d0d0` on the chat client itself — the same slot-list walk shape as the
signal emits named in #801 (`*this` is the head, each node's vtable slot 2 is the callback).

⛔ **And the reader of `Buddy_t[12]` does not exist. The client files the online stamp and never reads it back.**
The struct is now pinned exactly: `sizeof(ppj::Buddy_t) == 0x34`, held in a plain vector spanning
`[chatClient+0x110] .. [+0x114]` — the add path walks it with `ADD ESI,0x34`, which is what fixes both the size and
the field map (`+0x00` id, `+0x04` name `std::string`, `+0x20` level, `+0x24` class, `+0x28` last-seen, `+0x2c`
online, `+0x30` online stamp). So every reachable consumer either iterates that vector or is handed a record by the
buddy-changed signal, and both were swept over all 139,441 functions
([`DumpBuddyFieldReaders.java`](../../tools/ghidra/DumpBuddyFieldReaders.java)):

- **four** functions in the chat client address both `+0x110` and `+0x114` (the vector's begin/end) — `FUN_00a70560`,
  `FUN_00a70a30`, `FUN_00a70de0`, `FUN_00a71e00` — and **none of them touches `+0x30`**;
- the co-read sweep for `+0x2c` (the online flag, i.e. the union's own discriminator) beside `+0x30` returns, in the
  chat/friend-list code, only the STORE (`FUN_00a71e00`), the record COPY (`FUN_00a73b20`/`FUN_00a77750`/
  `FUN_00a77f60`) and `FriendListNode_c` — whose `+0x2c`/`+0x30` are a *different* struct's fields (see below).

The store site is the sweep's own positive control and it shows up in both halves, so a null result is a measured
negative rather than a mis-aimed query. **Nothing further about this value is recoverable from the client**: the
standing reading — a **playfield-instance token**, since `TeamMemberRecord` embeds a `PlayfieldProxy` carrying this
exact `0x01EDA8B2` in its instance-token slot (Gitea #454), and the distribution agrees (five of six captured
friends share it, one carries `0x01EDAF93`, 1761 higher) — can only be confirmed by watching the client, not by
reading it.

### ⚠️ The friend state's `online` byte is NOT a boolean

Found while sweeping for the above. `FriendListNode_c` copies the byte to its own `+0x30` (the buddy-apply's
`*(int *)(node + 0x30) = Buddy_t[11]`), and `FUN_006ca510` — the node's presence-category accessor — switches on it:

```
node+0x2c (buddy data present) && node+0x30 == 1  ->  category 0
                                 node+0x30 == 2  ->  category 1
                                 node+0x30 == 3  ->  category 2
otherwise                                        ->  the offline branch (category 3; 4 for node+0xe0)
```

The social-list row builder `FUN_016bf320` then routes each row into one of five sub-lists by that category, and
**increments its online counter (`+0x438`) for 0/1/2 while decrementing it for 3** — so all three of `1`, `2` and
`3` are ONLINE states and only they are. Retail sends `1` (online) / `0` (offline) in every captured record and we
send the same, so nothing is wrong today; what `2` and `3` mean is simply not in the corpus, and
`FriendState.Online` stays a `bool` rather than gaining three invented names for two unobserved values.


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

### Which guild ranks may kick, promote, demote and withdraw — SOLVED, from the client's own data
Both of these were `[V0]`s (#104's rank actions and #799's bank withdrawal) on the same premise: RDB 1000518
ships a per-rung permission BITMASK, strictly nested `0` ⊂ `30` ⊂ `286` ⊂ `6430` ⊂ `7518` ⊂ `0x7FFFFFFF`, and
which BIT is which right was not recoverable *from the table*. It is recoverable from the CODE that renders it:
the guild-management view builds its permission list by pairing each bit with the LDB token that labels it, and
hands the bit itself to the `GuildPermission` object as `m_permissionId`.

[V1: tools/ghidra/DumpGuildRankPermissions.java — `0x001` CanDisbandGuild, `0x002` InviteOthers, `0x004`
KickOthers, `0x008` Promote, `0x010` Demote, `0x040` WithdrawMoney, `0x080` ChangeGoverningForm, `0x100`
ChangeMotd, `0x200` ChangeName, `0x400` ChangeRecruitmentMode, `0x800` ChangeVulnerabilityWindow.]

So the rules are the client's, not ours: **the top two rungs may withdraw** on six governing forms and **the
leader alone on Stygian** (whose second rung is `6430`, the mask without `WithdrawMoney`); invite/kick/promote/
demote sit in every non-zero mask and so reach one to two rungs further down. Enforced through
`GuildRankLadder.Allows`, gated by `GuildRankPermissionTests`.

⚠️ One rule beside them is still OURS: an action may not be aimed at an equal or higher rung. The table grants
a right, it does not say who it may be used ON, and without that guard an Aquilonian Patrician could demote their
own Potentate.

⚠️ The one captured guild-banking session (`demol80-demonologist_login_gameplay_annotated_3`) never could settle
this: the member banking there is the guild's own leader, so a successful withdrawal is consistent with every
threshold. What it did close is the message bodies (#795) — see `GuildTransferMoneyCodec` /
`GuildWithdrawMoneyCodec` / `GuildUpdateCashCodec`, all now `[V2: …]`.

### What sets the MagicBuff apply payload's hostile flag
**Tracked: Gitea #464** (the corpus-exhausted register; the decode work was #658, closed). [V0: what separates the 134 from those 554] `descriptor[4]` of the 54-byte
`MagicBuffIIR` apply payload is a boolean — its only non-zero value is `1` — and it is clear on every one of the
2,241 captured SELF-applies, so "the caster is not the target" is a necessary condition for it. It is not a
sufficient one: 554 of the 688 non-self applies leave it clear. The ten spells that do carry it are hostile
debuffs and DoTs (`Pestilential Blast (Rank 6)`, `Flesh to Worms (Rank 5)`, `Mark of the Devourer`, `Repenting`,
`Poisoned`, `Ice Shackle`), which is a lead and not a rule — nothing we hold marks a spell hostile, and the
sweep of the spell's own RDB 1040005 record (every head word, every property field, and every bit of both) finds
nothing that separates the two populations. `--report buff-descriptor` prints the cross-tab. ⛔ The CLIENT
cannot name it either: the word is parsed into the `MagicBuffIIR` and never read — the client consumes exactly
two of the descriptor's seven words, and this is not one of them
(`docs/subsystems/npc-spell-applyspell.md`, #658) — so the setter rule is retail-server-internal and only a
larger corpus can characterise it further.

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

### ~~The two daily kill counters~~ RESOLVED (#887, 2026-08-31): the caps are daily-challenge completion TARGETS
The old [V0] here asked what `DailyPvEKillsCap` = 5 caps, `1000069` having been identified as the daily fatality
count (and `1000070` as every witnessed kill, clamped at 20 — `--report daily-kills` has both identifications).
The corpus answers it directly: **crossing the cap fires the daily-challenge COMPLETION burst** — the instant
`DailyPvEKills` reaches 5, and the instant `DailyPvPKills` reaches 20, the server pushes
`DailyDailyCounter`/`DailyWeeklyCounter`/`DailyMonthlyCounter` + lifetime `CounterDailyChallenges` together with
level-scaled cash and a `DispatchDisplayItemLootChatMessage` announce. Observed at exactly the cap crossing in
four independent sessions (three PvE completions, four PvP) and annotated by the player in one ("daily challenge
compelted"). Full measurement in [daily-challenges.md](../subsystems/daily-challenges.md); the residual clamp
asymmetry (`DailyPvPKills` alone saturates at its target) stays with the #509 entry below.

## The daily-challenge reset timestamps are never on the wire

**Tracked: Gitea #509.** [V0: the reset timestamps' wire form.] The daily-challenge block is twelve contiguous
character stats (1000066–1000077, see [daily-challenges.md](../subsystems/daily-challenges.md)), and retail
pushes the **nine counters** routinely — each occurs in 46–72 of the 134 captures. The three RESET INSTANTS
(`DailyResetTimestamp` 1000066, `WeeklyResetTimestamp` 1000067, `MonthlyResetTimestamp` 1000068) occur **zero**
times across the whole corpus.

That asymmetry is the finding: it is not that the block is unobserved, it is that these three members of it
specifically never travel, while their nine neighbours do. So either retail keeps them server-side, or they ride
a message no capture contains.

⚠️ Their unit and epoch are therefore **ours** — `DailyChallengeService` stores unix seconds of the last boundary
applied. Nothing observable depends on the choice, because the client is never shown them; if a capture ever
carries one, that is the moment to check it rather than to assume this was right.

⭐ **A neighbouring question this DOES answer**, for the #656 entry above: a cap is a completion THRESHOLD rather
than a clamp on at least two counters — the corpus runs `DailyQuestsCompleted` to **7** against a cap of 3 and
`DailyPvEKills` to **13** against a cap of 5. ⚠️ But it does not generalise cleanly: `DailyPvPKills` stops dead on
exactly **20**, its own cap, in three independent captures. So the two behaviours coexist and the remaining
question is why that one counter is bounded when the other two are not.

## How many vulnerability-window changes a guild gets

**Tracked: Gitea #147.** [V0: how many window changes a guild gets.] The client refuses a move with *"There are
no more changes left to the vulnerability window."* (LDB 100/245141412), so a cap exists — but no string, tweak
or table states its value, and no capture performs a move.

`GuildCityRules.CanMoveVulnerabilityWindow` therefore takes `changesRemaining` from its caller rather than
holding a number: the rule that running out refuses is retail's, the allowance is not ours to invent. The same
applies to how many free city spots a realm has, which is content we do not hold.

## What sets `regenDelayed`

**Tracked: Gitea #867.** [V0: what sets regenDelayed — no capture carries a 1] `DispatchSyncRegenDelayed` is a
server-owned boolean the client is told about 1,226 times across 65 captures, and **every one of those frames
carries `00`** (`--report sprint`). So the corpus proves the transport and the CLEAR, and says nothing about the
set: retail is only ever observed telling the client that regeneration is no longer held.

The clears cluster just after a sprint ends — 514 of them follow an `isSprinting = 0`, median 820 ms later — which
is why `TrackSprintEnergy` sends the clear there and sends nothing else. Whatever holds regen (a fight, a cast, the
sprint itself) is not visible from a message that never carries the flag it would set.
