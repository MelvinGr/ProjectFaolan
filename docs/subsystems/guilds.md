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
[lastSeenUnix u64][48 bytes sparse renown/pvp counters]
```

⭐ **`lastSeenUnix` is ONE u64**, not the `[unknown u32][lastSeenUnix u32]` pair this used to read as (#464). The
client's `GuildMember_c` reader `FUN_00566740` takes it with `FUN_0048f720` (ReadU64) and passes the result
straight to `__ctime64` — a 64-bit `time_t`. So the "unknown" word was the stamp's **high half**, and its being 0
in every captured record needed no explanation: a `time_t` has a zero high word until 2106. #464 had it filed as
an invariant only a client-side consumer could name; the consumer named it by there being no field there at all.

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

⭐ **The money half IS captured, and every predicted body is right** (#795). This section used to say a sweep
found zero guild-bank frames; the sweep predates
`Captures/demol80-demonologist_login_gameplay_annotated_3.tshark.json`, which carries one session's whole guild
banking with the player narrating it in `/say`:

| time | direction | message | body | annotation |
|---|---|---|---|---|
| login | s2c | `GuildHeader` for `Faolan` (`0x86C47`) | `cash = 0` | |
| 09:45:09.520 | s2c | `DispatchGuildBankData` | the empty vault, container `0x6D` instance `0x86C47` | |
| 09:45:32.917 | c2s | `GuildTransferMoney` | `0000000000086c47 00000000000f4240` | *"just deposited 1g to guild bank"* |
| 09:45:33.036 | s2c | `DispatchUpdateCash` | `0000000000da31e9 00000000` | |
| 09:45:33.111 | s2c | `DispatchGuildUpdateCash` | `00000000000f4240` (1,000,000) | |
| 09:45:46.051 | c2s | `GuildWithdrawMoney` | `000000000007a120` (500,000) | *"withdrew 50s"* |
| 09:45:46.220 | s2c | `DispatchGuildUpdateCash` | `000000000007a120` (500,000) | |
| 09:45:46.337 | s2c | `DispatchUpdateCash` | `0000000000e1d309 00000000` | |

Three things fall out that the method table could not give:

1. ⭐ **`DispatchGuildUpdateCash` is the BALANCE, not a delta.** The guild started at cash 0, so the post-deposit
   1,000,000 is consistent with either reading — but the post-withdraw value is 500,000, which is the balance
   (1,000,000 − 500,000). A delta would have had to be negative there.
2. **It is sent on the deposit too**, not only the withdrawal.
3. **The player's own `DispatchUpdateCash` moves by exactly the amount** in both directions (15,299,113 →
   14,299,113 → 14,799,113), confirming the wallet leg, and its order relative to the guild update is *not* fixed —
   the two arrive on different tiers.

`GuildRenownCodecTests.GuildBankMoney_MatchesTheCapturedBodies` pins all three bodies.

The layout rule the bodies were originally *derived* from still holds and is still worth keeping, because it is
what will predict the next uncaptured `GameCharAgent` body: a request body is its declared parameters concatenated
big-endian with no header, at **ID32 = 8, int = 4, uint64 = 8**, which reproduces the captured length of every
scalar-parameter method on the interface — `RequestBindLocation(ID32)` 8, `SetTradeCash(uint64)` 8,
`BuyItem(ID32,int)` 12, `AddTradeItem(ID32,int,int)` 16, `SellItem(ID32,ID32,int)` 20,
`SplitInventoryItem(ID32,int,int,int)` 20, `TryLootItem(ID32,int,ID32,int)` 24. Nine methods, no misses
(`GuildBankTests`). Two 64-bit parameters make a 16-byte body — and `GuildTransferMoney`'s captured body is
exactly those 16 bytes, so the rule has now been *tested* rather than only fitted.

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
- **The top two rungs may withdraw — the CLIENT'S rule, not ours** (#799). RDB 1000518 ships a permission
  BITMASK per rung and the guild-management view names its bits, `CanWithdrawMoney` being `0x40`
  [V1: tools/ghidra/DumpGuildRankPermissions.java]. It is set in the masks `7518` and `0x7FFFFFFF` and in nothing
  below, which works out as the top two rungs on six governing forms and **the leader alone on Stygian** (whose
  second rung carries `6430`, the mask without it). `GuildRankLadder.Allows(form, rank, WithdrawMoney)` is the
  check; `GuildRankPermissionTests` gates the ladder. The same masks now gate kick/promote/demote, which reach one
  to two rungs further down — every non-zero mask carries the officer four.
  ⚠️ The captured session never could settle this: the member banking there is that guild's own leader, so a
  successful withdrawal is consistent with every threshold. The answer came out of the client's data instead.

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

One thing is decoded but deliberately unsent: `DispatchGuildOnInviteCooldown` (`0x204E`, one u32) needs an invite
cooldown we do not model — its c2s twin `RemoveGuildInvitationCooldown` (`0x2024`) says retail has one.

⭐ WHO may kick/promote/demote used to be leader-only here for want of retail's real rule. It is the client's rule
now: the per-rung permission bitmask in RDB 1000518, whose bits the guild-management view names
[V1: tools/ghidra/DumpGuildRankPermissions.java]. Every non-zero mask carries `InviteOthers|KickOthers|Promote|
Demote`, so the officer rungs get them — one to two rungs below the leader depending on the governing form. See
`GuildRankLadder.Allows` and the bank-withdrawal note above.

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
  counters (not opaque). ⭐ The `ReadU64(time)` here is the whole `lastSeenUnix` field (#464) — see the record
  layout above. The six counters are stored out of order (`+0x50 +0x58 +0x60 +0x70 +0x68 +0x78`), which is the
  client's business and does not affect the wire.
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

### Guild-city plots and battlekeeps on the wire (#498)

Every plot piece is an ordinary `DestructibleFullUpdateIIR` world object — the same message a harvest node uses
(#494) — and the two populations are separated by the **health pair in the destructible trailer**, whose first
two words are `[0] = current`, `[1] = max` (`WorldObjectGeometryColumns.HealthCurrentWord`/`HealthMaxWord`;
words `[2..4]` are 0 on all 976 captured destructibles). Over the shipped rows:

| max health | rows | what it is |
|---:|---:|---|
| 1,000,000 | 524 | **Poitain's unbuilt plot markers** — `Location Object - Keep / Walls / Curved Wall / Staired Wall / Towers / Corner Tower / Ending Tower / Gate / Centerpiece / Decoration / Profession / Tavern / Temple / Tradepost / Megalith`. Uniform and never damaged. |
| 1,000 | 193 | harvest nodes (#494) — the one band whose `current` varies |
| 100M–300M | 221 | **the Border Kingdoms battlekeep, BUILT** |

The marker set *is* the build menu, and Poitain is one of the three booking zones the client's own help book
names (LDB `50020`/`689`), so the 1,000,000 band is the ground truth for what a bookable spot looks like: the
"marker stone" that help entry tells you to right-click.

The built keep, on map 11040 (Border Kingdoms, Cimmerian End), carries the **tier in both the name and the
health** — `Wall II` → `Wall III` — and the ladder is regular:

| | wall | tower | building (Temple, Barracks, Library, Trade Post, the four workshops, Thieves' Guild) | keep |
|---|---:|---:|---:|---:|
| tier II | 100M | 125M | 150M | — |
| tier III | 150M | 175M | 200M | 300M |

So a tier step is +50M and a class step is +25M. That answers #498's three scope bullets: the body was already
decoded (`WorldObjectCodec`, byte-exact), **construction/upgrade state is the tier suffix plus the health band**,
and these are ordinary **world objects, not per-guild instances** — the full update carries no owner at all.
Ownership rides its own message, `GuildCityOwnershipChange(const std::vector<ID32>& i_cMembers, bool
i_bNewValue)` (GameCharAgent `0x15`), which names *members*, not objects.

⚠️ **The decode exposed a live bug.** 142 of the 193 shipped harvest nodes were **captured mid-harvest** (one at
`1/1000`), and the send path replayed the captured word verbatim — so a fresh zone rendered most of its nodes
empty while the server held them full, and an "exhausted"-looking node was still harvestable ten times.
`WorldObjectCodec.Build` now takes the object's **live** `currentHealth` and stamps it over the captured word,
exactly as it already did for a door's live `isClosed` (#598). The captured rows are untouched; only the send
corrects.
