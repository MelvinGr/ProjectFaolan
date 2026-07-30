# Guilds

Decoded 2026-06-12 from `Captures/Hoxxling_mage-heraldofxotli_login-guild_invite_accepted-…json`
(a live retail invite→accept) and `…-claim_login_reward.json` (a later login while guilded).
Dissector + validation: `GuildPacketTests`.

## Where guilds live on the wire

All guild RPCs run on the **CsPlayerAgent** connection (port 7001, `faolan.json` `CSPlayerAgentPort`). The 16-bit opcode after the
RpcHeader is a **method index** (0x2000 + position) into the recovered RPC tables
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
  persistent (1 connect, matching retail) instead of the 30s reconnect loop. Shared multiplier: `GuildListMultiplier`
  / `ListFrame.Unit` (= the renown block's `0x3F1` list framing).
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
  `RequestRenownHistory` (0x2040 → `0x2039`) returns a zeroed 112-byte history (we track no time series). The
  character/team/PvP-matchmaking searches (`0x200A`→`0x2010`, `0x200B`→`0x200F`, `0x2054`→`0x2092`) return well-formed
  empty `0x3F1` lists (retail's own no-result bodies), and `0x2055` gets no reply (retail sends none).
- Create path: the capture-true `CreateGuild` RPC (0x2021 → `HandleCreateGuild`), triggered in-client from a
  registrar NPC window. There is no GM guild-create command — creation is only via that in-world registrar window.

Open work is tracked in Gitea #104.

## Ghidra: guild s2c reader topology (2026-06-16, #219)

Hunt for the 277-byte renown-block reader. The s2c (CSPlayerInterface) dispatch indices DRIFT vs the
recovered name table, so the wire-opcode→handler mapping must be confirmed per-function by its assert
string, not by `0x2000 + index`. Confirmed so far (decompiles via `tools/ghidra/DecompileFns.java`):

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
