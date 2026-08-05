# Chat (ChatServer + local say) — PINNED 2026-06-11

Status: the chat paths below are **implemented and byte-matched against the captures**; cross-player relay
(channel chat reaching a second player, proximity say between two players) is **not yet verified in-client** —
it needs two connected clients. Pinned here until then. Memory: `protocol_agentserver_chat`,
`protocol_stats_and_combat` (the local-say dispatch). Workbench: any `*gameplay*` / `*guild_invite*` capture
(`ProtocolName == "ChatServer"`).

## Two chat systems (different connections)

| chat | connection | how |
|---|---|---|
| **Channels** (~Global/~LFG/~Trial/~GlobalEnglish/~Playfield/~NewbieHelp/~Guild) | **ChatServer** (port `realm+...`) | `0x0041` |
| **Local "say"** (the default chat box) | **GameServer** receives it, **ChatServer** delivers it | `0x2002` in → `0x0022` out |
| **World/system chat** (remoteformat XML) | GameServer | `ChatTextIIR 0xE0B51102` (out; `.syschat` GM cmd) |

The ChatServer wire format is `[u16 opcode][u16 bodyLen][body]` (`ChatServerMessageFramer`).

## ChatServer opcodes

- **`0x0000` Hello** (in): `[u32 unk0][u32 clientInstance][u32 accountId]`. On receipt the server sends the channel
  subscription list, a `0x00050000` ready, `0x0014` char-info, and the welcome `SystemMessage`s.
- **`0x003C` ChannelSubscription** (out, `ChannelSubscriptionCodec`): `[byte type][u32 channelId][u16-str name][u32 flag][u16 0000]`. The list:
  ~Trial(04/0x232A/flag 0x8244), ~Global(04/0x2328), ~LFG(04/0x2329), ~GlobalEnglish(11/0x2335),
  ~Playfield(90/**id = the character's map id**), ~NewbieHelp(87/0x1E8480); flag 0x8044 except ~Trial.
  Plus the two **membership** channels, whose id is the thing you belong to and whose flag is **0x8004**, not 0x8044:
  ~Guild(03/**id = the guild id**, #663) and ~Team(82/**id = the team id**, #668). Those three — map, guild, team —
  are the only channels that MOVE, and all three go through the same `MoveChannel` (drop the old, subscribe the new,
  stamp the session slot), driven by `RefreshPlayfieldChannel` / `RefreshGuildChannel` / `RefreshTeamChannel`.
  There is no officer channel and no other `~` channel anywhere in the corpus — see
  [`create-guild-decode.md`](create-guild-decode.md).
- **`0x0041` ChannelChat** (out, `ChannelChatCodec`). **in** (client posts): `[byte type][u32 channelId][u16-str message][u16 0000]` — no
  sender. **out** (relay): `[byte type][u32 channelId][u32 senderId][u16-str message][u16 0000]` — sender inserted,
  preceded by a `0x0014` sender-info. Implemented: inbound `0x0041` → `BroadcastChannelChat` relays `0x0014`+`0x0041`
  to **every** connected client, **preserving the channel type+id**. Per-channel routing is the **client's own
  subscription filter** — a relayed message only shows in a channel the client subscribed to, so global channels
  reach everyone while ~Playfield (id = map) is naturally same-map; no server-side membership table needed.
- **`0x0014`** (out, `ChatSenderInfoCodec`): `[u32 senderId][u32 0][u16-str name]` — sender identity, sent before a `0x0041`.
- **`0x0022` CharacterChat** (out, `CharacterChatCodec`): the **local "say"** relay. `[u32 charId][u16-str message][u16 0001][byte 0]`
  (byte-exact vs the captures: "hell", "hello beach", "say", …). The longer remoteformat-XML `0x0022`s are NPC/scry
  "say". See the say flow below.
- **`0x0023` SystemMessage** (out, `ChatSystemMessageCodec`): `[u16 0000][u16-str message][u16 0000]` (message may be `<remoteformat>` XML).
- **`0x0064` ChatReady** (in): a small chat-init handshake (body `00 01 02`); echoed back verbatim.

## Claude channel (MCP bridge) — added 2026-06-12

A server-defined channel **"Claude"** (`04/0x00C1A0DE/flag 0x8044`, id far outside the retail range) is
appended to the login subscription list. The whole bridge is gated on the `Mcp:Enabled` key in `faolan.json`
(default true): disabled = no MCP host, no channel subscription, no log capture. Player posts on it broadcast normally AND are recorded in the
in-memory `ClaudeChannel` log (`Faolan.Core.Network/Server/ChatServer/ClaudeChannel.cs`, ring of 500, monotonic
sequence numbers). The MCP tools `read_claude_channel` (poll by `afterSequence`) and
`send_claude_channel_message` bridge it to Claude; Claude's replies broadcast in-game as the synthetic
sender `0xC1A0DE05`/"Claude" via the standard `0x0014`+`0x0041` relay pair. Log is not persisted across
restarts. In-client render of the channel is not yet verified (needs a live client).

## Local "say" flow + proximity

1. Player types → **GameServer `ChatCommand` (0x2002)**: `[u32][u32][u32][u32][u16-str text]`. (A leading `.` from a
   GM is a GM command instead.)
2. The GameServer computes the **proximity recipients** (`SayRecipients`): connected players on the **same map**
   within `SayRangeUnits` (~50 world units ≈ metres) + the **sender** (always hears their own say). Positions are
   live only on the GameServer (Move updates `Session.Character.Position`); the ChatServer loads its own Character
   per connection and has no live position, which is why the scope is computed GameServer-side.
3. → `dispatcher.CharacterSay(senderId, text, recipientIds)` → ChatServer delivers `0x0022` to only those clients
   (`recipientCharacterIds = null` would mean server-wide; kept for any non-proximity caller).

## Not done / next (when revisited)

> Tracked as Gitea issues (`area:aoc-systems` / `area:in-client`): in-client cross-player verification. The notes
> below are the technical context for those entries. (World/system chat via `ChatTextIIR` now ships — see below.)

- **In-client cross-player verification** (2+ clients): confirm a channel post reaches another player and a say is
  heard only within range.
- ~~**~Guild** channel~~ — shipped (#663), along with ~Team (#668).

## Whispers / tells (0x001E) - decoded + implemented 2026-06-12

Capture `Hoxxling_mage-heraldofxotli_login_move-from-night-to-day_send-and-receive-whisper`: the tell is
op **0x001E both directions**, same body shape: `[u32 peerCharId][string][00 01 00]` (peer = the TARGET in the
client's request, the SENDER on delivery). Delivery is preceded by a `0x0014` sender info, like channel chat;
retail echoes nothing back to the sender (the client renders its own outgoing tell locally). Golden tests:
`WhisperTests` (byte-exact both captured packets). Implemented in `WhisperCodec` (one codec, both directions);
offline target -> system message (uncaptured fallback). A tell to Claude's synthetic identity
(`/tell Claude`, id learned from the channel's 0x0014) lands in the MCP bridge log as a private message.
LIMIT: the client whispers by character ID it already knows (from a 0x0014 it has seen); whispering a name the
client has never seen chat may involve a name-lookup exchange that is NOT in this capture.
- **World/system chat via `ChatTextIIR` (0xE0B51102)** — a GameServer remoteformat-XML chat line. Implemented (#103):
  byte-exact `ChatTextCodec` (`Faolan.Core.Network/Codecs/Text/ChatTextCodec.cs`) proven by golden test
  `ChatTextCodecTests.Build_FromParsedFields_MatchesCapture`, pushed via `SendWorldChat`; the `.syschat` GM command
  sends the captured localized sample line. Remaining gap: an arbitrary-literal-text remoteformat template (the
  `.syschat` bootstrap replays the captured line). The local-say `0x0022` path covers player say.
- Tune `SayRangeUnits` if the in-client range feels off.

## The in-game Claude channel and the Claude Code channel bridge

Every client is subscribed to the server-defined **"Claude" chat channel** (04/0x00C1A0DE) on ChatServer
login. Player posts on it land in the in-memory `ClaudeChannel` log; the MCP tools `read_claude_channel`
(cursor by sequence) and `send_claude_channel_message` (broadcast in-game as "Claude") bridge it to Claude.

**Push delivery (2026-06-12):** `Faolan.Mcp.ChannelBridge` (the assembly's exe entry point) is a Claude Code
*channel* (research preview, code.claude.com/docs/en/channels-reference): Claude Code spawns it over stdio
(`.mcp.json` server `faolan-chat`), it polls the game server's MCP endpoint every ~1.5 s, and pushes each new
player post into the running Claude Code session as a `notifications/claude/channel` event - so Claude reacts
to in-game chat without polling. Replies go back through its `reply` tool. Enable per session:

```bash
claude --dangerously-load-development-channels server:faolan-chat
```

(Custom channels are not on the research-preview allowlist, hence the development flag. The bridge tolerates
game-server restarts and skips the pre-launch backlog. SDK note: the experimental capability value must be a
JsonElement - a boxed Dictionary breaks the SDK's source-generated initialize serialization.)

**Reply in the addressed mode (#97):** each message carries the inbound MODE structurally — `ClaudeChannelMessage.ViaWhisper`
is set at ingestion (true for a `/tell Claude`, false for a channel post; `ChatServerListener` `Whisper` vs channel-chat
cases), *not* encoded as a `"(whisper) "` text prefix. The bridge surfaces it on the channel event as
`mode="whisper|channel"`, and the server instructions tell Claude to answer in kind: whisper back (`whisper_to` =
`sender_id`) for a whisper, broadcast (omit `whisper_to`) for a channel post. So a private tell stays private and a
public post stays public, automatically. Covered by `ClaudeChannelTests`.

## Ambient NPC barks — the catalog is HARVESTABLE (#331)

An NPC's ambient line is a local-say `CharacterChat` (0x0022) carrying
`<remoteformat token="sayinchat(N)|<channel>(K)|<Group>" category="50015" key="…" knubot="0">`, stored per NPC
TYPE in `NpcChatter` and built by `NpcChatterCodec`. The open half of the ticket was written as "the lines live
in the RDB scry scripts; a dedicated extraction pass is needed". They do, the pass exists now
(`--report npc-chatter`), and it rests on two facts that read the other way round at first glance:

- **Category 50015 is PLAINTEXT** (`flags=0`), not one of the encrypted eleven. Every bark line the client ships
  is readable offline, and the `key=` a captured bark carries is inert — the client's decrypt is guarded on the
  string's own encrypted flag, so it never reads a key on a `flags=0` page
  ([`ldb-string-index.md`](../rdb/ldb-string-index.md)). A harvested line therefore needs no captured key.
- **A bark's string id is computable from its token** — the client's own 28-bit ELF hash
  (`LdbStringIndex.TokenId`) of `sayinchat(N)|<channel>(K)|<Group>`, all of which are `NpcChatter` columns. All 13
  captured barks resolve to their text through it, which is the end-to-end proof.

Together those make a group's whole line set enumerable by sweeping `N`. For the five captured groups the sweep
finds **54 lines where the DB carries 13** — e.g. `RenownBystanderLovers/conversation1` is an eleven-line
exchange between Lord Ovidius and his lady, of which captures caught five.

Of the 41 unharvested lines, 16 carry no template slot; the other 25 contain a `{Slot}` (`{TopGloryName}`,
`{AdGuildName}`, `{DetectedChar.name}`, `{AdvertiseMinutes - AdvertiseCounter}`) needing a matching
`<parameter>` element filled from live server state — separate work from harvesting the text.

`--only npc-chatter` takes **9** of those 16, and the gap between the two numbers is the rule that makes the
harvest safe. A bark group is often a **two-hander**: `RenownBystanderLovers` is one conversation shared by the
Nobleman and the Noblewoman, and the client's line set interleaves them (`[2]` is Lord Ovidius on his sons, `[3]`
is her answering *"That's just dreadful, Lord Ovidius"*). The captures assign 3/8/16/23 to her and 6/19 to him,
so the split is real — and it is **not** mechanically recoverable: strict alternation from those anchors
reproduces every line except the last, which is hers and would be given to him. So a group with more than one
captured speaker is skipped entirely; harvesting it would put her lines in his mouth, which is worse than the
silence it replaces.

A harvested row is identifiable by its **empty `FormatKey`** — legitimate, since a key is only read for an
encrypted string. Gated by `SeedContentTests.HarvestedBarks_AreInTheSeed_AndNoTwoHanderGroupWasHarvested`, which
asserts both halves: the harvested lines are present, and no line of the shared conversation is among them.

⚠️ **Group DISCOVERY is still open, and the obvious route does not work.** Pulling identifiers out of the client's
`SCRIPTGROUP2` (1010028) records and hash-testing each was built and thrown away: not one of the five groups the
corpus already proves appears among those 11,118 identifiers, so it finds no real group — while happily returning
hash noise that looks like results. The id is 28 bits and the probes are not independent (names differing by a
digit cluster in ELF-hash space), so even a "3+ hits" threshold let ten confident non-groups through. The 1,277
strings in the category are reachable only once the group names are found somewhere else.

Open work tracked in Gitea #331.
