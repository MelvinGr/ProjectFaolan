# Chat (AgentServer + local say) — PINNED 2026-06-11

Status: the chat paths below are **implemented and byte-matched against the captures**; cross-player relay
(channel chat reaching a second player, proximity say between two players) is **not yet verified in-client** —
it needs two connected clients. Pinned here until then. Memory: `protocol_agentserver_chat`,
`protocol_stats_and_combat` (the local-say dispatch). Workbench: any `*gameplay*` / `*guild_invite*` capture
(`ProtocolName == "AgentServer"`).

## Two chat systems (different connections)

| chat | connection | how |
|---|---|---|
| **Channels** (~Global/~LFG/~Trial/~GlobalEnglish/~Playfield/~NewbieHelp/~Guild) | **AgentServer** (port `realm+...`) | `0x0041` |
| **Local "say"** (the default chat box) | **GameServer** receives it, **AgentServer** delivers it | `0x2002` in → `0x0022` out |
| (seen, not implemented) world/system chat | GameServer | `ChatTextIIR 0xE0B51102` (remoteformat XML) |

The AgentServer wire format is `[u16 opcode][u16 bodyLen][body]` (`AgentServerMessageFramer`).

## AgentServer opcodes

- **`0x0000` Hello** (in): `[u32 unk0][u32 clientInstance][u32 accountId]`. On receipt the server sends the channel
  subscription list, a `0x00050000` ready, `0x0014` char-info, and the welcome `SystemMessage`s.
- **`0x003C` ChannelSubscription** (out): `[byte type][u32 channelId][u16-str name][u32 flag][u16 0000]`. The list:
  ~Trial(04/0x232A/flag 0x8244), ~Global(04/0x2328), ~LFG(04/0x2329), ~GlobalEnglish(11/0x2335),
  ~Playfield(90/**id = the character's map id**), ~NewbieHelp(87/0x1E8480); flag 0x8044 except ~Trial. Retail also
  adds ~Guild(03/<guildId>) once the guild is known — we don't track guilds, so we don't send it.
- **`0x0041` ChannelChat**. **in** (client posts): `[byte type][u32 channelId][u16-str message][u16 0000]` — no
  sender. **out** (relay): `[byte type][u32 channelId][u32 senderId][u16-str message][u16 0000]` — sender inserted,
  preceded by a `0x0014` sender-info. Implemented: inbound `0x0041` → `BroadcastChannelChat` relays `0x0014`+`0x0041`
  to **every** connected client, **preserving the channel type+id**. Per-channel routing is the **client's own
  subscription filter** — a relayed message only shows in a channel the client subscribed to, so global channels
  reach everyone while ~Playfield (id = map) is naturally same-map; no server-side membership table needed.
- **`0x0014`** (out): `[u32 senderId][u32 0][u16-str name]` — sender identity, sent before a `0x0041`.
- **`0x0022` CharacterChat** (out): the **local "say"** relay. `[u32 charId][u16-str message][u16 0001][byte 0]`
  (byte-exact vs the captures: "hell", "hello beach", "say", …). The longer remoteformat-XML `0x0022`s are NPC/scry
  "say". See the say flow below.
- **`0x0023` SystemMessage** (out): `[u16 0000][u16-str message][u16 0000]` (message may be `<remoteformat>` XML).
- **`0x0064` ChatReady** (in): a small chat-init handshake (body `00 01 02`); echoed back verbatim.

## Claude channel (MCP bridge) — added 2026-06-12

A server-defined channel **"Claude"** (`04/0x00C1A0DE/flag 0x8044`, id far outside the retail range) is
appended to the login subscription list. Player posts on it broadcast normally AND are recorded in the
in-memory `ClaudeChannel` log (`Faolan.Core/Network/AgentServer/ClaudeChannel.cs`, ring of 500, monotonic
sequence numbers). The MCP tools `read_claude_channel` (poll by `afterSequence`) and
`send_claude_channel_message` bridge it to Claude; Claude's replies broadcast in-game as the synthetic
sender `0xC1A0DE05`/"Claude" via the standard `0x0014`+`0x0041` relay pair. Log is not persisted across
restarts. In-client render of the channel is not yet verified (needs a live client).

## Local "say" flow + proximity

1. Player types → **GameServer `ChatCommand` (0x2002)**: `[u32][u32][u32][u32][u16-str text]`. (A leading `.` from a
   GM is a GM command instead.)
2. The GameServer computes the **proximity recipients** (`SayRecipients`): connected players on the **same map**
   within `SayRangeUnits` (~50 world units ≈ metres) + the **sender** (always hears their own say). Positions are
   live only on the GameServer (Move updates `Session.Character.Position`); the AgentServer loads its own Character
   per connection and has no live position, which is why the scope is computed GameServer-side.
3. → `dispatcher.CharacterSay(senderId, text, recipientIds)` → AgentServer delivers `0x0022` to only those clients
   (`recipientCharacterIds = null` would mean server-wide; kept for any non-proximity caller).

## Not done / next (when revisited)

- **In-client cross-player verification** (2+ clients): confirm a channel post reaches another player and a say is
  heard only within range.
- **~Guild** channel (needs a guild/membership system) and **tells/whispers**.
- **World/system chat via `ChatTextIIR` (0xE0B51102)** — a GameServer remoteformat-XML chat seen once in captures;
  not implemented (the local-say `0x0022` path covers player say).
- Tune `SayRangeUnits` if the in-client range feels off.
