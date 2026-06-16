# Guilds

Decoded 2026-06-12 from `Captures/account-faolan-character-hoxxling-login-guild_invite_accepted-…json`
(a live retail invite→accept) and `…-claim_login_reward.json` (a later login while guilded).
Dissector + validation: `Captures/decode_guild.py`.

## Where guilds live on the wire

All guild RPCs run on the **CsPlayerAgent** connection (port 7012). The 16-bit opcode after the
RpcHeader is a **method index** (0x2000 + position) into the recovered RPC tables
(`docs/aoc-client-rpc-methods.json`):

- client→server: `CSPlayerAgent.client_to_server` — alignment verified (0x201C = `AcceptGuildInvitation`
  fired exactly once on the accept; 0x201E/0x202F/0x2040/0x2041 all match their observed behavior).
- server→client: `CSPlayerInterface.server_to_client` — true table recovered from the dispatcher switch
  (211 methods; the live build inserts mostly no-arg methods the string catalog missed). Full table:
  `docs/aoc-rpc-method-opcodes.md`.

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

- rank 0 = fresh member (Kyloth, who could invite, had 5; we give the creator 6)
- playfield is 0 and lastSeen is set when offline; both flip when online
- class values match our `Class` enum (0x2B Herald of Xotli, 0x29 Necromancer, …)
- the level byte ticked 4→5 mid-capture when Hoxxling leveled

## 0x203F GuildFullUpdate body

```
[govForm u8 =3][guildId u32][01][cash u64][03][u32 0][00]
[name s16][description s16][motd s16]
[277-byte renown/feat/city-stats block]
[self member record][selfContribution u32][other member records … to packet end, no count]
```

The 277-byte block (renown total, 13 feat/unlock ids, city counters) has undecoded internal framing;
we emit the captured bytes verbatim (`GuildRenownBlockTemplate`) because a synthesized block could
misalign the roster that follows. Cosmetic-only until decoded.

## Server implementation (Faolan)

- DB: `Guild` table + `Character.GuildId`/`GuildRank` (migration `AddGuilds`). Members are characters
  with `GuildId` set; deleting a guild releases members (`SetNull`).
- `Faolan/CSPlayerAgent/GuildService.cs` — the guild collaborator extracted from the listener: wire
  builders (member record + invite body byte-exact against the capture: `GuildPacketTests`) plus the
  RPC handlers (CreateGuild, GuildInvite(ByName), Accept/Decline, GuildLeave, GuildMessageOfTheDay,
  ClientRequestGuildInfo, member-list refresh). Pending invites are in-memory; cross-client pushes use
  reply endpoints captured at Authenticate (the listener forwards them via `_guild`).
- In-world tag: `SendGuildTag` through the `IGameServerDispatcher` seam (live join/leave) and on the
  existing-character login path. The cross-connection member lookup is the `ClientForCharacter` Func
  seam injected into `GuildService`.
- GM bootstrap: `.guild create <name>` (the capture-true CreateGuild RPC needs a registrar NPC window).

## Open items

> Tracked in `TODO.md` §8 (the single tracker, "Guild protocol gaps"). The decode notes below are the context.

- Member-left / kick / promote / demote fan-out packets: no capture exercises them.
- The 0x202D invite-reply-to-inviter and failure replies (bad name, on cooldown): opcodes not decoded.
- 0x203A "counters update" (52 b, frequent): small u32 counters, semantics unknown.
- Renown block internal framing; renown history (0x2039) / rankings (0x2038) are replayed blobs.
- Other players' guild tags ride in their spawn data (cross-player visibility is future work anyway).

## Ghidra: guild s2c reader topology (2026-06-16, #219)

Hunt for the 277-byte renown-block reader. The s2c (CSPlayerInterface) dispatch indices DRIFT vs the
recovered name table, so the wire-opcode→handler mapping must be confirmed per-function by its assert
string, not by `0x2000 + index`. Confirmed so far (decompiles via `Captures/ghidra/DecompileFns.java`):

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

STILL OPEN — the **Guild_c header+renown reader** (govForm/guildId/cash/name/desc/motd + the 277-byte
renown block) is NOT yet isolated: it is not `FUN_005656a0`/`FUN_00565900` (those are ctors), and the
traced dispatchers only surface the member-list read. Next step: find the function that reads a u64
(cash) + 3 strings (ReadString ×3) — that is the Guild_c deserializer, and the renown block is read at
its tail. The renown-block scaffold (`[u8 ver][u32 hdr][13× u32 stat ids 0x4FFB0..0x50334][220-byte
counter region]`) is documented in `Captures/decode_guild_renown.py`.
