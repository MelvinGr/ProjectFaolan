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

  **All of them are declared in one place, `ChatChannels`** — a `ChatChannel` is the (type, name, flag) triple, and
  the id is either a constant (the standing channels, which the login burst iterates) or the thing the player
  belongs to. `ChatChannelCatalogTests` re-derives every captured subscription body from its descriptor, so the
  catalog is the wire fact rather than literals re-typed at each call site. Nothing special-cases the assistant
  channel below either: it is one more descriptor, gated on the MCP host being up.
  ⚠️ **Subscription ORDER is not significant** — retail varies its own. The Hoxxling login sends the list three
  times in one burst: `Trial,Global,LFG,Playfield,NewbieHelp`, then `Global,LFG,Trial,Playfield,NewbieHelp`, then
  the full set with GlobalEnglish. Three orders, one session.
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

  ⚠️ Hashing the group name ALONE and hunting the value in the client answers nothing, and the control says so:
  `elf28("RenownBystanderLovers")` hits 6–10 times across ~GBs of RDB + exe, while `elf28("conversation1")` — a
  token the client demonstrably uses — hits 5. At that base rate the search has no discriminating power. It is
  the whole token that is hashed, not the group.

### ⭐ The group names ARE in the client — a file grep could not see them

This section used to say `RenownBystanderLovers` "appears **zero times as text** anywhere in the client — not in
`RDB/*.rdbdata`". **That was a measurement artifact.** Most RDB records are zlib-compressed in the pack, so a grep
over the pack FILES cannot see a string that is plainly shipped; `--report rdb-text-scan` searches the
**decompressed** bodies instead and finds all five known groups, plus `renownbystandersstygian`, in one place:

```
RDB 1000010 record 1 — the client's global id -> NAME table, 192,291 entries / 187,474 distinct
  ...  4285601 renownbystanderwomen   4285603 renownbystanderlovers   4285611 renownbystandersstygian ...
```

`RdbNameTable` reads it (`--report rdb-names`), and the per-type index records beside it narrow the pool further.
Every one of the six is referenced by **`1020004/1010029`**, the name list for RDB type **1010029** — the
server-side SCRIPT catalog, **7,102 names of which 5,810 are bare identifiers** (`aquiluspatroltoluco`,
`acgregionambush`, `applyfearlessbuffv2wrapper`, `sewer4555spawn`). So a bark group is a script name, and the
whole candidate pool is those 5,810.

⚠️ That also explains the earlier sweep's clean miss: it pulled identifiers out of **`SCRIPTGROUP2` (1010028)**,
the type NEXT TO the one that holds them.

⚠️ **What still blocks discovery is CASE.** The name table is lowercased by the build, and the token hash is
case-sensitive — `elf28("sayinchat(3)|conversation1(3)|RenownBystanderLovers")` resolves to *"That's just
dreadful, Lord Ovidius, really."* while the same token with `renownbystanderlovers` resolves to nothing. So a pool
entry has to have its CamelCase restored before it can be swept.

⭐ The acceptance test for a restored casing is strong enough to make that safe, which is the difference from the
`elf28(group)`-alone search above: a candidate is accepted only when **two or more** `sayinchat(N)` values resolve
for the same (name, casing, channel). A single 28-bit collision against the 1,277-entry category has probability
~4.8e-6, so two independent ones inside one 512-wide sweep is ~3e-9 — against ~10⁶ (name, casing, channel) triples
that is far below one expected false group, where the single-hit threshold would have produced thousands.

`NpcStatCore.ClientScryScript`, the other route #331's body proposes, is `0` on all 1,194 stat cores, so it is
not how an NPC is tied to its group.

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

⚠️ An earlier discovery attempt swept `SCRIPTGROUP2` (1010028) identifiers and was thrown away: not one of the
five groups the corpus proves appears among those 11,118 identifiers, so it found no real group while happily
returning hash noise that looked like results. The pool above is the corrected one — the same idea against
`1010029` rather than `1010028`, with a two-line acceptance test instead of a hit count on the bare group name.

Open work tracked in Gitea #331.

### A bark's CUE is in its channel (#694)

A bark is not one thing on a clock. The `channel` component of the token — a column we already store — names what
makes the NPC say it, and the shipped corpus (22 rows, 6 NPC types) splits three ways with no leftovers:

| channel | cue | groups |
|---|---|---|
| `conversation1` / `conversation2` | **ambient** — the tick's clock, said to everyone in say-range | `RenownBystander{Lovers,Women,Child}` |
| `sendtell` | **detection** — a player walks up; a *tell*, so it goes to that player alone | `RenownCrier` |
| `event` | **encounter script** — the encounter's own proximity trigger (#502) | `RnBLadyInDistressBrainScript` |

`NpcChatterBarks.TriggerOf` is that split, and `WorldBroadcaster` sorts each NPC type's lines into the three pools
at spawn (`ChatterLines` / `GreetingLines` / `EncounterLines`) so the world tick never has to ask.

⚠️ Everything used to sit in one pool on the ambient clock, and it was wrong in two visible ways: the crier
delivered *"You should consider joining a guild, {DetectedChar.name}!"* on a timer — to a player who had been
standing there for ten minutes, or to nobody at all — and the Damsel's cry for help **double-fired**, once on her
encounter trigger and again on the clock.

The session slot corroborates the split but runs **one way only**: every bark carrying one (`DetectedChar.name`,
`Target.Name`) is non-ambient, and no ambient bark carries one — but the converse does not follow. A tell has exactly
one recipient whether or not it names them, so an unaddressed tell would still be said on detection. Hence the channel
is the discriminator and the slot only decides whether the body must be built per-recipient
(`NpcChatterBarks.Addressed`, #691). [V3: NpcBarkTriggerTests.TheCrier_HasOneTell_AndItIsADDRESSED]

⚠️ This paragraph used to cite the crier's `SayInChat` 10 as the corpus' unaddressed tell. **It was never a captured
line** — the corpus holds only 57. It came from the #331 LDB sibling harvest, and resolved against the client it reads
*"Excuse me, I wanted to ask you something."* out of a `sendtell` set that is otherwise a guild-advertising
transaction (18 lines, 13 of them mid-sale). In-client the crier greeted a player with it. The harvest therefore fills
**ambient channels only** (#717): on a conditional channel the script's state picks the line, and the harvest's
slot-free filter selects *against* the NPC's own lines there, since those are exactly the ones carrying slots.

Detection state is per NPC (`WorldEntity.GreetedPlayers`): a player is greeted once on arrival and forgotten again
only after leaving `WorldState.GreetingForgetRange`, which is deliberately wider than `GreetingRange` so someone
loitering on the boundary is not greeted, forgotten and greeted again every few ticks. Both numbers are **ours** —
`GreetingRange` is matched to `SayRangeUnits` (50 u) on the ground that greeting someone too far away to hear it
would be worse than not greeting them; nothing shipped states an NPC's detection radius, and the one annotated
capture times a crier line without pinning the distance the player crossed to earn it.

## An item LINK in chat carries the item's decryption key (#751)

When a player links an item into chat, the client emits the whole `ProtoAcgItem` **as plain text**, inside an
ordinary anchor:

```
<a style="text-decoration:none" href="itemref://T1/T2/T3/LEVEL/KEY1/KEY2/KEY3"><font color=#RGB>[Name]</font></a>
```

so one link yields `Items.Id`, `Name`, `Level` and the `Key1..Key4` TEA quad outright. It rides three carriers in
the corpus — ChatServer `0x0041` (trade channel, most of them), ChatServer `0x0022`, and the c2s Conan `0x2002` —
and `itemref` is the only item-bearing href scheme anywhere in it.

**Why it matters far more than a name.** An item's RDB template is TEA-encrypted under a key the server assigns
per item, so without the key the template cannot be read at all (#203, closed: there is no client-side key
source). A chat link is therefore not an id+name stub — it unlocks the stats, quality, value, damage and search
category behind the record. And it is the only source that reaches items the capturing character **never held**:
whatever a stranger was advertising in trade chat.

Measured over the 132 dumps: **43 links, 25 distinct templates, 12 of them absent from the catalogue**. The other
13 agree with their stored row exactly on name, level and key quad — that agreement is the oracle, and it is
non-circular because the key comes from the link and never from the database.

⚠️ **Read `key1`.** `key3` is sometimes the `NullIdentity` sentinel `b290805c:29e627ca:b290805c:29e627ca` (a
pre-existence item), and `key2` belongs to the SECOND template of a level-scaled pair (#752) — equal to `key1` on
everything that is not one.

⚠️ **The URL drops the hash word** the loot-chat body form carries
(`[template][template][0][level][hash][k1][k2][k3]`). Harmless: `AcgHash.ForWire` mints a token at send time and
`Items.Hash == 0` means "not harvested" (#723).

⚠️ **Both the key words and the colour are UNPADDED lowercase hex** — `45e19f7` is seven characters and `#6ea7`
is four — so a build that zero-pads either does not reproduce what the client sent.

⚠️ **A link is player-typed text.** The client composes it, but the importer must not let one outrank a
snapshot-sourced row: `chat-itemref` goes through `CaptureItemImporter.UpsertAsync`, where a name only fills an
EMPTY one and a level only fills a 0. `ChatItemLinks.In` counts the anchors it could not read rather than
swallowing them.
[V3: ItemRefLinkCodecTests.ALinkedItemWeHold_AgreesWithTheStoredKeyQuad]

## ⛔ The CamelCase sweep is a closed route (#331)

The plan recorded here was: the bark group names are the 5,810 lowercased identifiers in `1020004/1010029`, the
token hash is case-sensitive, so restore the CamelCase and sweep. `--report chatter-groups` implements exactly
that - vocabulary from every English LDB page plus the name table's filename parts, iterative-deepening
segmentation, and the proposed acceptance test of **two or more** `sayinchat(N)` hits. It does not work, and the
report exists to say so with numbers.

| | |
|---|---:|
| casings swept | 152,183 |
| probes | 2,493,366,272 |
| (casing, channel, count) combinations passing the two-hit rule | 3,848 |
| pool names passing it | 781 |
| **…of those, passing under MORE THAN ONE contradictory casing** | **350 (45%)** |

⚠️ **The two-hit rule assumes the hits are independent and they are not.** The sweep gives every
(casing, channel, K) 256 chances - one per `N` - and the ELF-28 hash's low bits depend only on the last few
characters, so every candidate for one group lands in the same narrow band:

```
sayinchat(137)|broadcastcycle(6)|AchievementsPetHeraldOfDeeds  ->  0x0a93eb03
sayinchat(138)|broadcastcycle(6)|AchievementsPetHeraldOfDeeds  ->  0x0d93eb03
```

The effective space is far smaller than 2²⁸, so hits are cheap. `AchievementsPetHeraldOfDeeds` (correct) and
`AchievementsPetHeRaLdoFdeEds` (nonsense) both resolve nine or more lines.

⛔ **Ranking by hit count does not rescue it.** The two known groups the vocabulary can reach do rank first -
`RenownCrier sendtell(3)` 18 lines and `AchievementsPetHeraldOfDeeds broadcastcycle(6)` 13, both matching their
captured `NpcChatter` rows - but the overall top of the ranking is `HalloweenSnakeBoSscOmbAt` (24 lines) and
`PlayerLocalTeLePoRt` (22). And the control settles it: for `renownbystanderlovers`, whose true casing is known
from a capture, the sweep accepts a casing and it is **not** the true one.

⚠️ **There is also a vocabulary floor no client-derived source can lift.** `bystander` occurs **nowhere** in the
client - not in the 152 English LDB pages, not in any of the 192,291 name-table entries - except inside the four
`renownbystander*` names themselves. An authoring word the game never says out loud cannot be recovered from the
game's words, so four of the six known groups are unreachable by segmentation whatever the acceptance test.

**What that leaves.** Group discovery from the client alone is closed by this measurement. The lines themselves
remain fully readable once a group name is known, so the route back in is a capture that carries the token - the
same route that produced the six groups already harvested.
