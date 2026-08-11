# Guilds

Decoded 2026-06-12 from `Captures/Hoxxling_mage-heraldofxotli_login-guild_invite_accepted-…json`
(a live retail invite→accept) and `…-claim_login_reward.json` (a later login while guilded).
Dissector + validation: `GuildPacketTests`.

## Where guilds live on the wire

All guild RPCs run on the **CsPlayerAgent** connection (port 7001, `faolan.json` `CSPlayerAgentPort`). The 16-bit opcode after the
RpcHeader is a **method index** (its position, rendered `0x2000 + position` in a dump) into the recovered RPC tables
(`docs/re/aoc-client-rpc-methods.json`):

- client→server: `CSPlayerAgent.client_to_server` — alignment verified (0x201C = `AcceptGuildInvitation`
  fired exactly once on the accept; 0x201E/0x202F/0x2040/0x2041 all match their observed behavior).
- server→client: `CSPlayerInterface.server_to_client` — true table recovered from the dispatcher switch
  (211 methods; the live build inserts mostly no-arg methods the string catalog missed). Full table:
  `docs/re/aoc-rpc-method-opcodes.md`.

One GameServer packet participates: sub-opcode `0x2550C99A GuildUpdateIIR_c` sets the in-world guild
tag: `[ID32 char][u16 0][s16 guildName]`. Retail sends it once on guild join; an empty name clears it.

ID32 = `[u32 type][u32 instance]`; guild type `0x0000DEB7`, character type `0x0000C350`.
Strings = u16-BE length + bytes.

## Observed retail flow (invite → accept)

```
DL 0x202A GuildInvite          [guild ID32]["Sepulchra Invictus"][invitee ID32]["Kyloth"]
UL 0x201C AcceptGuildInvitation[guild ID32]
DL 0x203F GuildFullUpdate      (14 KB: header + MOTD + renown block + 165-member roster)
DL 0x202F GuildMemberJoined    [invitee ID32][member record]      ← also fans out to online members
GS 0x2550C99A GuildUpdateIIR   in-world guild tag for the joiner
```

At login while guilded, the server pushes `0x203F` immediately after the Authenticate ack.
`GuildRequestMemberList` (UL 0x202F) is answered with a fresh `0x203F` + the 0x20A5 summary.
`0x2030 GuildMemberUpdate` re-sends a member record on level-up / zone change / login / logout.
`0x2042` / `0x2043` answer `ClientRequestGuildInfo` for same-guild / other-guild characters.

## Member record (validated across the full roster)

```
[03 03][charId u32][name s16][rank u8][online u8][playfield u32][level u8][class u32]
[unknown u32][lastSeenUnix u32][48 bytes sparse renown/pvp counters]
```

- rank 0 = fresh member (Kyloth, who could invite, had 5). **The guild LEADER is rank 4** — proven by the
  create-guild capture's member record (`docs/subsystems/create-guild-decode.md`); the server previously guessed 6.
  `Character.GuildLeaderRank = 4` (no rank-comparison logic depends on the value; it only hits the wire record).
- playfield is 0 and lastSeen is set when offline; both flip when online
- class values match our `Class` enum (0x2B Herald of Xotli, 0x29 Necromancer, …)
- the level byte ticked 4→5 mid-capture when Hoxxling leveled

## 0x203F GuildFullUpdate body

```
[version u8 =3][guildId u32][govForm u8 =1][cash u64][b u8][u32 0][b u8]
[name s16][description s16][motd s16]
[277-byte renown/feat/city-stats block]
[self member record][selfContribution u32][other member records … to packet end, no count]
```

The whole body is the `i_pcGuild` argument deserialized by `CoreGuild_c::Read` (FUN_00565900). NOTE the first
byte is the **CoreGuild_c streaming version (==3)**, NOT the government form (an earlier note mislabeled it); the
**government form is the byte after guildId (==1)**, which selects the reader's case 1 → fall-through case 3
layout. The 277-byte block is now a **typed codec** (`GuildRenownBlockCodec`, byte-exact, `GuildRenownBlockCodecTests`),
built per-guild from `Guild.Renown` (`GuildRenownBlock.ForRenown`) — no longer a verbatim blob. The full reader
walk (``GuildPacketTests` --walk`) deserializes the whole arg and lands byte-exact on the next
member record, proving the field grammar; only `RenownCounters[1]` (param_1[0x40]) varies between snapshots (the
live renown total), so the other feat/city/cap fields stay constant pending their (binary-stripped) semantics.

## Server implementation (Faolan)

- DB: `Guild` table + `Character.GuildId`/`GuildRank` (created in the `InitialCreate` migration). Members are characters
  with `GuildId` set; deleting a guild releases members (`SetNull`).
- `Faolan/CSPlayerAgent/GuildService.cs` — the guild collaborator extracted from the listener: wire
  builders (member record + invite body byte-exact against the capture: `GuildPacketTests`) plus the
  RPC handlers (CreateGuild, GuildInvite(ByName), Accept/Decline, GuildLeave, GuildMessageOfTheDay,
  ClientRequestGuildInfo, member-list refresh). Pending invites are in-memory; cross-client pushes use
  reply endpoints captured at Authenticate (the listener forwards them via `_guild`).
- In-world tag: `SendGuildTag` through the `IGameServerDispatcher` seam (live join/leave) and on the
  existing-character login path. The cross-connection member lookup is the `ClientForCharacter` Func
  seam injected into `GuildService`.
- **Create (no relog):** `HandleCreateGuild` emits `DispatchGuildCreated` (0x2044) — retail's immediate create
  response, which opens the guild window without a relog — built byte-exact against the create-guild capture
  (`GuildPacketTests.GuildCreatedBody_Matches_CapturedBytes`). See `docs/subsystems/create-guild-decode.md` (#306). The
  login `0x203F` push still runs, so relog remains a fallback.
- **0x3F1 count-framed lists (the "not in guild" fix):** every list on the guild wire begins with a u32 word ==
  `(elementCount + 1) * 0x3F1` — the client's list readers assert `word % 0x3F1 == 0` and take `count = word/0x3F1 - 1`,
  aborting the whole dispatch ("stream bad after '<arg>'") and dropping the CsPlayerAgent connection on any non-0x3F1
  word. `DispatchGuildUpdate` (0x203F) reads THREE arguments (`FUN_013428d0`): `i_pcGuild` (version…renown block),
  `i_pcMember` (the self record), then `i_cMemberList` = `[u32 (otherCount+1)*0x3F1][other member records]`. The old
  builder wrote a bare `0` where that count-word belongs (misread as a "self contribution" counter); `0` isn't a 0x3F1
  multiple, so the client aborted `GuildUpdate`, never registered the guild, and re-polled every 30s — THIS was the
  "not in guild after relog" bug. Verified in-client: with the count-word correct the CsPlayerAgent connection stays
  persistent (1 connect, matching retail) instead of the 30s reconnect loop. The whole body is `GuildFullUpdateCodec`
  (#530 wave 7); its list count is `ListFrame.ForCount` (= the renown block's `0x3F1` list framing).
- **Rankings (0x2038):** the global guild-renown leaderboard (the "Guild Renown Rankings" window, tabs
  Total/Valor/Glory/Artistry) is now CONSTRUCTED from the realm's guilds via `GuildRankingsCodec`
  (`Faolan.Core.Network/Codecs/Guild/GuildRankingsCodec.cs`, byte-exact against the capture —
  `GuildRankingsCodecTests`), no longer a verbatim retail blob. Its body is three nested
  0x3F1-framed lists: `[guildList][sectionsList (always 4 tabs)][u32 nextUpdateUnix]`, each section
  `[u32 tabIndex][entryList]`. The four renown categories live on the `Guild` table
  (`RenownValor`/`RenownGlory`/`RenownArtistry`, plus `Level`; the Total column is their sum, `RenownTotal`); a guild
  appears on a tab only once it has renown there, so a fresh server shows an empty board rather than fake guilds. The
  wire `prevRank` word is the guild's PREVIOUS-snapshot position (drives the "Change" column); with no history we emit
  the current position (no movement).
- **CS-agent reply endpoints:** every `CsPlayerAgent` s2c is addressed FROM the guild-server object endpoint
  (`GuildService.ReplySender` stamps the fixed instance `0x04` onto the class the client targeted), matching retail —
  the client targets the class with instance 0, and captures always show a nonzero server instance on replies.
- **Search / matchmaking requests** (`Faolan/CSPlayerAgent/Packets.cs`): the client's remaining CS-agent requests are
  now all answered so no window is left hanging. `SearchForGuilds` (0x2043 → `0x205C`) is CONSTRUCTED from the realm's
  guilds via `GuildSearchResultCodec` (byte-exact, `GuildSearchResultCodecTests`) — the guild-finder browse list.
  `RequestRenownHistory` (0x2040 → `0x2039`) returns a zeroed history — still zeros, but now the *decoded* zeros of
  `GuildRenownHistoryCodec` rather than a `new byte[112]` (see below). The
  character/team/PvP-matchmaking searches (`0x200A`→`0x2010`, `0x200B`→`0x200F`, `0x2054`→`0x2092`) return well-formed
  empty `0x3F1` lists (retail's own no-result bodies), and `0x2055` gets no reply (retail sends none).
- Create path: the capture-true `CreateGuild` RPC (0x2021 → `HandleCreateGuild`), triggered in-client from a
  registrar NPC window. There is no GM guild-create command — creation is only via that in-world registrar window.

### The guild bank (#132)

It has **two** halves, and they are on **different tiers** — which is the client's own arrangement, not ours.

| | method | tier | body |
|---|---|---|---|
| withdraw | `CSPlayerAgent.GuildWithdrawMoney` (`0x35`) | CS — where `Guild.Cash` lives | `[u64 amount]` |
| deposit | `GameCharAgent.GuildTransferMoney` (`0x11`) | game — where the WALLET lives | `[u64 guildInstance][u64 amount]` |
| refusal | `GameCharInterface.DispatchGuildNotEnoughCash` (`0x03`) | game | `[u64 amount]` |

⚠️ **This section used to say "there is no deposit method"** and that the `GameCharAgent` pair were inter-agent
server notifications rather than client packets. That was wrong, and the client settles it: `GuildTransferMoney`
is listed in `GameCharAgent`'s **client_to_server** table with the signature `(uint64 i_nGuildInstance,
Explicit64_c i_cAmount)`. Guild money is fed by the tax *and* by members paying in (#654).

**None of it is captured** — `--report method-census --report-arg Bank` finds zero guild-bank frames across all
120 captures, because no captured session ever opened the bank window. So the bodies come from the client's
method table, and the LAYOUT is the measurable part: a `GameCharAgent` request body is its declared parameters
concatenated big-endian with no header, at **ID32 = 8, int = 4, uint64 = 8**, which reproduces the captured length
of every scalar-parameter method on the interface — `RequestBindLocation(ID32)` 8, `SetTradeCash(uint64)` 8,
`BuyItem(ID32,int)` 12, `AddTradeItem(ID32,int,int)` 16, `SellItem(ID32,ID32,int)` 20,
`SplitInventoryItem(ID32,int,int,int)` 20, `TryLootItem(ID32,int,ID32,int)` 24. Nine methods, no misses
(`GuildBankTests`). Two 64-bit parameters therefore make a 16-byte body.

⚠️ **The reply ENDPOINTS are not recovered from anything.** The method table gives the method and its parameters;
it says nothing about the RpcHeader, whose sender is a server OBJECT id only a capture reveals. And it is
demonstrably not constant per interface — captured `GameCharInterface` replies arrive from `GameCharAgent{0x58}`
*and* from `GameCharAgent{0x10}` — so the refusal's `0x10` is the nearest neighbour's, a coin flip. If the refusal
does not render in-client, suspect the endpoint before the codec; that is the failure #101's team-invite window
cost a day to.

The transaction **spans two tiers** in both directions: the CS tier owns `Guild.Cash` and the rank ladder, the
game tier owns the wallet and is the only one whose `UpdateCash` the client renders. So the withdrawal debits the
guild here and pays out through `IGameServerDispatcher.CreditCharacterCash`, and the deposit debits the wallet on
the game tier and credits the guild through `ICsPlayerAgentDispatcher.DepositToGuildBank`.

Two deliberate choices, both about not minting money:

- **Whoever HOLDS the money moves it first.** The withdrawal debits the guild then credits the wallet; the deposit
  debits the wallet then credits the guild. Either way the second step's failure triggers a refund of the first.
  The other order would leave the money in both places on a failure between the steps, and a money handler is
  exactly where a dupe hides (#368). Losing a transfer is recoverable; minting one is not.
- **A deposit the payer cannot afford gets retail's own refusal**, `DispatchGuildNotEnoughCash`, rather than the
  silence every other refusal in this subsystem uses. The difference is that the client can legitimately send this
  one while being wrong — it does not know the wallet moved — and retail has a named message for exactly that.
- **Leader only.** Retail has per-rank bank permissions and nothing pins which rung they hang off, so the rule is
  the narrowest one that cannot be abused, by that guild's own governing form (`GuildRankLadder.LeaderRank` — the
  top rung is 3, 4 or 5 depending on the form).

### The two renown messages (#104)

Both were on the open list as "replayed blob" / "semantics unknown"; both are decoded, from the client's recovered
method fields plus the corpus.

**`0x203A DispatchClientAddGuildRenown`** — "you earned renown", NOT the "counters update" it was filed as. The
client names its fields `(renownType, amount, guildOnly, capType)` and its c2s twin types them
(`AddGuildRenown(const ID32&, uint8 grType, uint64 amount, bool onlyGuild)`), which gives
`[u32 renownType][u64 amount][u32 guildOnly][u8 capType]` = 17 bytes. All ten captured records agree: type 2,
guildOnly and capType 0, and only the amount moves (1 ×7, 3, 108, 139)
[V3: GuildRenownCodecTests.AddGuildRenown_RoundTripsTheCapturedRecord]. Which tab type 2 is stays open — it is the
only type captured.

**`0x2039 DispatchGuildRenownHistoryUpdate`** — four fixed entries, not a time series: the client's signature names
them `(currentEntry, lastEntry, highestEntry, averageEntry)` and 112 = 4 × 28, so an entry is
`[u64 rank][u64 total][u64 recent][u32 level]` (every u64 carries a zero high word, which is what fixes the widths)
[V3: GuildRenownCodecTests.RenownHistory_RoundTripsTheCapturedBody]. What each entry varies over confirms the
names: the first moves between two logins of the same character, the second and fourth track the GUILD, and the
third is byte-identical across both guilds in the corpus (`11228 / 2772084 / 318876 / 128`) — a row two different
guilds share is realm-wide, which is what "highest" reads as
[V3: GuildRenownCodecTests.RenownHistory_TheHighestEntryIsIdenticalAcrossGuilds]. The column meanings inside an
entry are still ours, from magnitude alone.

### The management fan-out (#104)

The roster actions no capture performs — leave, kick, promote, demote, and what the inviter is told. Every body here
comes from the client's own `CSPlayerInterfaceDispatch_t` readers, each located by the assert string that NAMES the
field it was about to read ("stream bad after 'i_cMemberName'"), so the field order is read rather than inferred.
⚠️ Nothing here is gated against retail bytes — no capture exercises any of it. [V0: the guild management fan-out]

| method | opcode | reader | body |
| --- | --- | --- | --- |
| `DispatchGuildMemberLeft` | `0x202E` | `FUN_013408f0` | `[ID32 member][string memberName]` |
| `DispatchGuildMemberJoined` | `0x202F` | `FUN_01340aa0` | `[ID32 member][member element]` |
| `DispatchGuildMemberUpdate` | `0x2030` | `FUN_01340c70` | `[member element]` |
| `DispatchGuildInviteReplyToInviter` | `0x2031` | `FUN_01340db0` | `[string message][u8 joined]` |
| `DispatchGuildGMPromoteFeedback` | `0x2032` | `FUN_01340fb0` | `[u32 form][u32 oldRank][u32 newRank][string name]` |
| `DispatchGuildGMDemoteFeedback` | `0x2033` | `FUN_01341220` | same |
| `DispatchGuildInviteNoSuchName` | `0x2034` | `FUN_01341490` | `[string name]` |
| `DispatchGuildOnInviteCooldown` | `0x204E` | `FUN_01343980` | `[u32 inviteCooldownStop]` |
| `DispatchGuildRankChanged` | `0x2065` | `FUN_013451e0` | `[u8 newRank][u8 oldRank]` |

Three of those would have been guessed wrong, and each is a body the client would have run off the end of:

* **`MemberLeft` is not the mirror of `MemberJoined`.** Joined's second field is the whole member ELEMENT — its
  reader calls the element deserializer `FUN_00566740` — while Left's calls the STRING primitive `FUN_0048f840`. A
  departure only needs the row to remove and the name to print. So the catalog's field name `i_cMemberName` is
  accurate on Left and misleading on Joined, where our capture-verified codec already carried the element.
* **`RankChanged`'s two ranks are single BYTES**, read with `FUN_00cba1b0(dst, 1)` — not the u32 reader
  `FUN_0048f900` that every neighbouring guild field uses. Four-byte ranks would put the client six bytes past the
  end of a two-byte body.
* **…and in `GMPromoteFeedback` the same ranks ARE u32.** Two readers, two primitives; the asymmetry is real, and it
  is exactly what a "ranks are one byte everywhere" assumption breaks on.

⚠️ **`GM` in `DispatchGuildGM*Feedback` is GUILD MASTER, not game master.** The client puts `GM` at the FRONT for its
game-master methods (`DispatchGMGuildNameChanged`, `DispatchGMReplyGuildInfoFromChar`, c2s `GMGuildName`); this pair
is `DispatchGuild` + `GM`, and its payload is exactly a promoter's toast — who, from which rung to which, and the
governing form to name them off.

**What the server does.** `GuildLeave` (`0x2028`) and the newly-handled `GuildKick` (`0x2027`) share one removal
path: clear the guild, clear the in-world tag, then fan `MemberLeft` out to every online member **including the one
who left** — the message names them, so it is their own notification too, and their client is the one that has to
close the guild window. `GuildPromote` (`0x2025`) / `GuildDemote` (`0x2026`) move the target one rung along their
guild's ladder and send three pushes to three different audiences: `GMPromoteFeedback`/`GMDemoteFeedback` to the
mover, `RankChanged` to the member it happened to, and `MemberUpdate` to everyone so the roster's rank column
refreshes. Promotion stops one rung BELOW the top: the top rung is the leader's, and handing it out would create a
second leader — retail transfers leadership through the governing-form change instead
(`DispatchGuildGoverningFormChanged` carries a `newLeader`).

Accepting or declining an invite now answers the INVITER with `InviteReplyToInviter`, carrying the invitee's name and
`joined` 1 or 0. An invite typed at a name nobody answers to gets `InviteNoSuchName` back, echoing what was typed. A
target who EXISTS but is already guilded stays silent — the client's catalog has no code for that refusal, and
borrowing this one would tell the inviter a real character does not exist.

Two things are decoded but deliberately unsent. `DispatchGuildOnInviteCooldown` (`0x204E`, one u32) needs an invite
cooldown we do not model — its c2s twin `RemoveGuildInvitationCooldown` (`0x2024`) says retail has one. And WHO may
kick/promote/demote is leader-only here rather than retail's real rule: the client ships a per-rung permission
BITMASK (RDB 1000518, see `GuildRankLadder`) that we do not model, so the choice was between the narrowest safe rule
and a guessed threshold that would let the wrong members kick people.
[V0: which guild ranks may kick, promote and demote]

The ticket also listed the **invite ACCEPT packet** as undecoded ("not isolated by sub-op in any capture; likely an
Interact"). It is not an Interact: the c2s table has `AcceptGuildInvitation(const ID32& i_cGuildID)` at `0x201C`,
which is what has been implemented and working all along.

Open work is tracked in Gitea #104.

## Ghidra: guild s2c reader topology (2026-06-16, #219)

Hunt for the 277-byte renown-block reader. The s2c (CSPlayerInterface) dispatch indices DRIFT vs the
recovered name table, so the wire-opcode→handler mapping must be confirmed per-function by its assert
string, not by a registration index. Confirmed so far (decompiles via `tools/ghidra/DecompileFns.java`):

- `FUN_013428d0` = `DispatchGuildUpdate`     (reads one leading member + member list)
- `FUN_01344670` = `DispatchGuildMemberList` (member list only)
- `FUN_013463d0` = `DispatchGMReplyGuildInfoFromChar` (asserts `i_cGuild` + `i_cMembers`)
- `FUN_0134ed30` = guild **member-list reader**: `[u32 word]`, `count = word / 0x3F1` with the **0x3F1
  list-framing invariant** (validates `word == count*0x3F1`, caps `count-1 < 0x7531`), then loops
  `count-1` × the member reader. Same 0x3F1 invariant as the FA015807/GameActionList lists.
- `FUN_00566740` = **GuildMember_c reader**, streaming version 3 (asserts "GuildMember_c streaming
  version mismatch! Got N wanted 3"). In-memory record size 0x88 (136 b). Case-3 field reads (confirms
  the wire member grammar): ReadString(name), ReadByte, ReadByte(bool online), ReadU32, ReadByte,
  ReadU32, ReadU64(time), then **6× ReadU64** — i.e. the member record's 48-byte tail = 6 u64 renown/pvp
  counters (not opaque).
- Stream primitives (shared, from `ghidra_vtable_xref_readers`): `FUN_0048f900` ReadU32(BE),
  `FUN_004e0390` ReadByte, `FUN_0048f840` ReadString, `FUN_0048f720` ReadU64.

RESOLVED (2026-06-17, #219) — the **Guild_c header+renown reader IS `FUN_00565900`** (`CoreGuild_c::Read`,
not a ctor as earlier supposed). It reads: streaming version (==3), guildId, government form, then switches on
the form; **case 1 falls through into case 3**, reading cash (ReadU64), two enum/bytes around a u32, the three
strings (name/desc/motd), then the renown block = `ReadByte(flag)` + `ReadList`(13 stat ids @param+0x39) +
2 bytes + 3×u32 + 2 bytes + 2×u32 + `ReadList`(3 ids @param+0x1c) + 6×u32 + **6×u64 (RenownCounters, @param+0x3e..**
where `[0x40]` is the live renown total) + 2×u32 + 8×u64 + 3×u64 + u32 + u64. ``GuildPacketTests`
--walk` replays this exact sequence and lands byte-exact on the following member record (helpers: FUN_0048f900
ReadU32, FUN_0048f720 ReadU64, FUN_0048f6d0/FUN_004e0390 ReadByte, FUN_0048f840 ReadString, FUN_004d4c70
ReadList). The C# field↔offset map is in `GuildRenownBlock`/`GuildRenownBlockCodec`. The per-guild meaning of the feat/city/cap fields is
not recoverable from the stripped binary (no symbol names; only RenownCounters[1] varies in capture).

## Guild cities: the booking guard and its error table (2026-08-11, #743)

#743 observed that a refused plot booking produces **zero traffic** — the client declines with a popup and
never asks the server — and concluded the requirement is therefore recoverable statically. It is, and it is
**24 guild members**.

`FUN_0115b2b0` is the `BookCitySpot` handler (the script binding is registered in `FUN_01473630` alongside
`GetGuildCityInfo`). Its whole guard, before it builds the request:

```c
if (guild == NULL)                      -> LDB(100, 0x4EF3 /* 20211 */), return;   // no guild at all
if (guild->memberCount /* +0x1e8 */ < 0x18) {                                      // < 24 MEMBERS
    if (!override())                    -> LDB(100, 0x4EF5 /* 20213 */), return;   // vslot +0x1fc
}
/* only here does it send RequestGuildCitySpot */
```

`0x18` = **24**, and the client's own help book states the same number twice, independently of the code:
LDB `50020`/`606` and `50020`/`689` both open with *"Only available to guilds with 24 or more members."*
⚠️ LDB `50020`/`44` is an older prose entry that says *"a minimum of thirty members"* — it is stale; two
structured requirement headers and the compiled comparison all say 24.

`50020`/`689` also names where the spots are: **Lacheish Plains** (Cimmeria), **Poitain** (Aquilonia) and
**Purple Lotus Swamp** (Stygia).

### `DispatchGuildCityBookingFailed(i_nErrorCode)` — the code IS an LDB id

`FUN_00532750` renders the failure with `LDB(100, i_nErrorCode)` and nothing else — no mapping table, no
base. So the wire field is a **verbatim LDB category-100 id**, and the whole error alphabet is readable out
of the LDB. A `CONST=` sweep of the client separates who emits which:

| id | text | emitter |
|---|---|---|
| 20199 | Booking failed. There was a database error while attempting to book your spot. | **server** |
| 20200 | Booking failed. There are no more free city spots. | **server** |
| 20201 | Booking failed. Your guild already has a city spot. | **server** |
| 20210 | Booking failed. An unknown city spot was assigned to your guild. | **server** |
| 20211 | Could not fetch your guild data. You have to be in a guild to do this. | **client** (`FUN_0115b2b0`) |
| 20212 | You do not have enough guild rank to do this. | **server** |
| 20213 | Your guild does not have enough members to do this. | **client** (`FUN_0115b2b0`) |

Only 20211 and 20213 appear as immediates anywhere in `AgeOfConan.exe`; the other five appear nowhere, which
is exactly what "the client only renders whatever code the server sent" looks like. That split is the
mechanism behind #743's observed silence: the two the client owns are the two that cost no traffic.

Success is `FUN_00561f00`, which caches the three `DispatchGuildCityBooked` fields on the guild object
(`+0xa0`/`+0xa4`/`+0xa8`, flag `+0x8c`) and shows LDB `100`/`129749027` *"Booking succeeded. Your guild has
been assigned a city spot."*

### The request/response shape needs no capture

#743 listed *"a capture of a SUCCESSFUL booking … the only way to learn the request/response shape"*. It is
not — the client's own method signatures name every field, and they are already catalogued in
`docs/re/aoc-client-rpc-methods.md` and `Faolan.Core/Enums/Opcodes.cs`:
`RequestGuildCitySpot(i_cCharacter, i_cGuildID, i_cPlayfieldModelID)` (CSPlayerAgent `0x45`),
`DispatchGuildCityBooked(i_nCitySpotId, i_nCityPlayfieldModelInstance, i_nCitySubgroup)` (`0x6B`),
`DispatchGuildCityBookingFailed(i_nErrorCode)` (`0x6C`). The layouts are tabulated in
`docs/re/minigame-massivepvp-messages.md`. What a capture would still add is the *placement* data — which
`PlayfieldModelID`s are bookable spots — not the grammar.
