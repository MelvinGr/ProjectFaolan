# Chat (ChatServer + local say)

The chat paths below are implemented and byte-matched against the captures. Cross-player relay (channel chat
reaching a second player, proximity say between two players) needs two connected clients to verify in-client.
Workbench: any `*gameplay*` / `*guild_invite*` capture (`ProtocolName == "ChatServer"`).

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
  Plus the two membership channels, whose id is the thing you belong to and whose flag is **0x8004**, not 0x8044:
  ~Guild(03/**id = the guild id**) and ~Team(82/**id = the team id**). Those three — map, guild, team — are the
  only channels that move, and all three go through the same `MoveChannel` (drop the old, subscribe the new, stamp
  the session slot), driven by `RefreshPlayfieldChannel` / `RefreshGuildChannel` / `RefreshTeamChannel`.
  There is no officer channel and no other `~` channel anywhere in the corpus — see
  [`create-guild-decode.md`](create-guild-decode.md).

  **All of them are declared in one place, `ChatChannels`** — a `ChatChannel` is the (type, name, flag) triple, and
  the id is either a constant (the standing channels, which the login burst iterates) or the thing the player
  belongs to. `ChatChannelCatalogTests` re-derives every captured subscription body from its descriptor, so the
  catalog is the wire fact rather than literals re-typed at each call site. Nothing special-cases the Claude
  channel below either: it is one more descriptor, gated on the MCP host being up.
  **Subscription order is not significant** — retail varies its own. The Hoxxling login sends the list three
  times in one burst: `Trial,Global,LFG,Playfield,NewbieHelp`, then `Global,LFG,Trial,Playfield,NewbieHelp`, then
  the full set with GlobalEnglish. Three orders, one session.
- **`0x0041` ChannelChat** (out, `ChannelChatCodec`). **in** (client posts): `[byte type][u32 channelId][u16-str message][u16 0000]` — no
  sender. **out** (relay): `[byte type][u32 channelId][u32 senderId][u16-str message][u16 0000]` — sender inserted,
  preceded by a `0x0014` sender-info. Inbound `0x0041` → `BroadcastChannelChat` relays `0x0014`+`0x0041` to every
  connected client, preserving the channel type+id. Per-channel routing is the client's own subscription filter —
  a relayed message only shows in a channel the client subscribed to, so global channels reach everyone while
  ~Playfield (id = map) is naturally same-map; no server-side membership table is needed.
- **`0x0014`** (out, `ChatSenderInfoCodec`): `[u32 senderId][u32 0][u16-str name]` — sender identity, sent before a `0x0041`.
- **`0x0022` CharacterChat** (out, `CharacterChatCodec`): the **local "say"** relay. `[u32 charId][u16-str message][u16 0001][byte 0]`
  (byte-exact vs the captures: "hell", "hello beach", "say", …). The longer remoteformat-XML `0x0022`s are NPC/scry
  "say". See the say flow below.
- **`0x0023` SystemMessage** (out, `ChatSystemMessageCodec`): `[u16 0000][u16-str message][u16 0000]` (message may be `<remoteformat>` XML).
- **`0x0064` ChatReady** (in): a small chat-init handshake (body `00 01 02`); echoed back verbatim.
- **`0x002A` SetSocialStatus** (in, `SocialStatusCodec`): `[u32 SocialStatus]` — the player's own presence, which is
  what a friend's list row draws beside their name. Nothing answers it on the wire; the work is the fan-out, so
  every watcher's row is re-sent (`AnnouncePresenceToWatchers`) and the icon changes there.

  It is the LFG toggle's second half. All three occurrences in the corpus (`NecroL80_..._annotated_9`) fire ~50
  microseconds after the CS tier's `SetLookingForTeamStatus`, carrying **2** when that flag goes on and **1** when it
  goes off — one UI action, two tiers, because the friend list is chat-tier and the CS tier owns team search.

  The values are the client's presence enum, and the friend record's `online` byte is the same enum, not a flag.
  `FriendListNode_c::PresenceCategory` switches 1 → 2 → 3 into three distinct online categories, and the
  friends-list icon table names them in that order — `icon_offline`, `icon_online`, `icon_lfg`, `icon_away`,
  `icon_other`, `icon_ignored`. So `SocialStatus` is `Offline = 0, Online = 1, LookingForGroup = 2, Away = 3`, with
  the first three observed and `Away` [V0].

  Chat is not RPC, so there is no registration catalog to read a name off — the client has no
  `ChatProxy_t`/`Dispatch_t` strings at all. A chat opcode can only be named from behaviour plus what consumes it,
  which is why the pairing above and the icon table are the whole basis for this one.

## Local "say" flow + proximity

1. Player types → **GameServer `ChatCommand` (0x2002)**: `[u32][u32][u32][u32][u16-str text]`. (A leading `.` from a
   GM is a GM command instead.)
2. The GameServer computes the proximity recipients (`WorldSimulation.SayRecipients`): connected players on the
   same map within `SayRangeUnits` (~50 world units ≈ metres) + the sender (always hears their own say). Positions
   are live only on the GameServer (Move updates `Session.Character.Position`); the ChatServer loads its own
   Character per connection and has no live position, which is why the scope is computed GameServer-side.
3. → `dispatcher.CharacterSay(senderId, text, recipientIds)` → ChatServer delivers `0x0022` to only those clients
   (`recipientCharacterIds = null` means server-wide, for any non-proximity caller).

`SayRangeUnits` is our value; tune it if the in-client range feels off.

## Whispers / tells (0x001E)

Capture `Hoxxling_mage-heraldofxotli_login_move-from-night-to-day_send-and-receive-whisper`: the tell is
op **0x001E both directions**, same body shape: `[u32 peerCharId][string][00 01 00]` (peer = the target in the
client's request, the sender on delivery). Delivery is preceded by a `0x0014` sender info, like channel chat;
retail echoes nothing back to the sender (the client renders its own outgoing tell locally). Golden tests:
`WhisperTests` (byte-exact both captured packets). Implemented in `WhisperCodec` (one codec, both directions);
offline target -> system message (uncaptured fallback).
The client whispers by a character id it already knows (from a 0x0014 it has seen); whispering a name the client
has never seen chat may involve a name-lookup exchange that is not in this capture.

## World/system chat via `ChatTextIIR` (0xE0B51102)

A GameServer remoteformat-XML chat line: byte-exact `ChatTextCodec`
(`Faolan.Core.Network/Codecs/Text/ChatTextCodec.cs`) proven by golden test
`ChatTextCodecTests.Build_FromParsedFields_MatchesCapture`, pushed via `SendWorldChat`; the `.syschat` GM command
sends the captured localized sample line. An arbitrary-literal-text remoteformat template has no captured form, so
`.syschat` replays the captured line. The local-say `0x0022` path covers player say.

## The in-game Claude channel

Every client is subscribed to the server-defined **"Claude" chat channel** (`04/0x00C1A0DE/flag 0x8044`, id far
outside the retail range) on ChatServer login. It exists so Claude has a voice in the game, and that is all it is:

| tool (`Faolan.Mcp/Tools/ClaudeChannelTools.cs`) | direction |
| --- | --- |
| `send_claude_channel_message` | broadcasts in-game as "Claude" to every connected client |
| `send_claude_whisper` | private tell to one connected character |

Both speak as the synthetic sender `0xC1A0DE05`/"Claude" through the standard `0x0014`+`0x0041` relay pair. Both are
gated on `Mcp:Enabled`: with it off the channel is not offered at login (`ChatChannels.StandingAtLogin`), because
without the MCP host nothing can speak on it. In-client render of the channel is not verified.

The channel is outbound only. A player's post on the channel, or a `/tell` to the synthetic sender id, is relayed
to other players and written to the `ChatMessage` history like any other chat, and reaches Claude nowhere: there is
no in-memory message log, no read tool, and no virtual friend entry for `/tell Claude` (such an entry would
advertise a friend whose tells answer "Player is not online"). `ClaudeChannel` is four constants (the channel/sender
ids and names). So Claude cannot answer in-game chat: talking to players is something a Claude session is told to
do, not something it is prompted into.

## Ambient NPC barks — the catalog is harvestable

An NPC's ambient line is a local-say `CharacterChat` (0x0022) carrying
`<remoteformat token="sayinchat(N)|<channel>(K)|<Group>" category="50015" key="…" knubot="0">`, stored per NPC
type in `NpcChatter` and built by `NpcChatterCodec`. The lines live in the client, and `--report npc-chatter`
extracts them. It rests on two facts:

- **Category 50015 is plaintext** (`flags=0`), not one of the encrypted eleven. Every bark line the client ships
  is readable offline, and the `key=` a captured bark carries is inert — the client's decrypt is guarded on the
  string's own encrypted flag, so it never reads a key on a `flags=0` page
  ([`ldb-string-index.md`](../rdb/ldb-string-index.md)). A harvested line therefore needs no captured key.
- **A bark's string id is computable from its token** — the client's own 28-bit ELF hash
  (`LdbStringIndex.TokenId`) of `sayinchat(N)|<channel>(K)|<Group>`, all of which are `NpcChatter` columns. All 13
  captured barks resolve to their text through it, which is the end-to-end proof.

  Hashing the group name alone and hunting the value in the client answers nothing, and the control says so:
  `elf28("RenownBystanderLovers")` hits 6–10 times across ~GBs of RDB + exe, while `elf28("conversation1")` — a
  token the client demonstrably uses — hits 5. At that base rate the search has no discriminating power. It is
  the whole token that is hashed, not the group.

### The group names are in the client's name table

Most RDB records are zlib-compressed in the pack, so a grep over the pack files cannot see a string that is
plainly shipped; `--report rdb-text-scan` searches the decompressed bodies and finds all five known groups, plus
`renownbystandersstygian`, in one place:

```
RDB 1000010 record 1 — the client's global id -> NAME table, 192,291 entries / 187,474 distinct
  ...  4285601 renownbystanderwomen   4285603 renownbystanderlovers   4285611 renownbystandersstygian ...
```

`RdbNameTable` reads it (`--report rdb-names`), and the per-type index records beside it narrow the pool further.
Every one of the six is referenced by **`1020004/1010029`**, the name list for RDB type **1010029** — the
server-side script catalog, **7,102 names of which 5,810 are bare identifiers** (`aquiluspatroltoluco`,
`acgregionambush`, `applyfearlessbuffv2wrapper`, `sewer4555spawn`). So a bark group is a script name, and the
whole candidate pool is those 5,810. None of the five corpus-proven groups is among the 11,118 identifiers of the
neighbouring type `SCRIPTGROUP2` (1010028); sweeping that type returns only hash noise.

**Case blocks discovery.** The name table is lowercased by the build, and the token hash is case-sensitive —
`elf28("sayinchat(3)|conversation1(3)|RenownBystanderLovers")` resolves to *"That's just dreadful, Lord Ovidius,
really."* while the same token with `renownbystanderlovers` resolves to nothing. A pool entry has to have its
CamelCase restored before it can be swept, and restoring it from the client alone does not work (see
[the CamelCase sweep](#the-camelcase-sweep-is-a-closed-route) below).

`NpcStatCore.ClientScryScript` is `0` on all 1,194 stat cores, so it is not how an NPC is tied to its group.

### Harvesting a known group

Once a group name is known, its whole line set is enumerable by sweeping `N`. For the five captured groups the
sweep finds **54 lines where the captures carry 13** — e.g. `RenownBystanderLovers/conversation1` is an eleven-line
exchange between Lord Ovidius and his lady, of which captures caught five.

Of the 41 uncaptured lines, 16 carry no template slot; the other 25 contain a `{Slot}` (`{TopGloryName}`,
`{AdGuildName}`, `{DetectedChar.name}`, `{AdvertiseMinutes - AdvertiseCounter}`) needing a matching
`<parameter>` element filled from live server state.

`--only npc-chatter` takes **9** of those 16, and the gap between the two numbers is the rule that makes the
harvest safe. A bark group is often a two-hander: `RenownBystanderLovers` is one conversation shared by the
Nobleman and the Noblewoman, and the client's line set interleaves them (`[2]` is Lord Ovidius on his sons, `[3]`
is her answering *"That's just dreadful, Lord Ovidius"*). The captures assign 3/8/16/23 to her and 6/19 to him,
so the split is real — and it is not mechanically recoverable: strict alternation from those anchors reproduces
every line except the last, which is hers and would be given to him. So a group with more than one captured
speaker is skipped entirely; harvesting it would put her lines in his mouth, which is worse than the silence it
replaces.

The harvest also fills **ambient channels only**. On a conditional channel the script's state picks the line, and
the harvest's slot-free filter selects against the NPC's own lines there, since those are exactly the ones carrying
slots. The crier's `sendtell` set shows why: it is a guild-advertising transaction (18 lines, 13 of them mid-sale),
and its one slot-free line, *"Excuse me, I wanted to ask you something."*, is not a greeting.

A harvested row is identifiable by its empty `FormatKey` — legitimate, since a key is only read for an
encrypted string. Gated by `SeedContentTests.HarvestedBarks_AreInTheSeed_AndNoTwoHanderGroupWasHarvested`, which
asserts both halves: the harvested lines are present, and no line of the shared conversation is among them.

### A bark's cue is in its channel

A bark is not one thing on a clock. The `channel` component of the token — a column we already store — names what
makes the NPC say it, and the shipped corpus (22 rows, 6 NPC types) splits three ways with no leftovers:

| channel | cue | groups |
|---|---|---|
| `conversation1` / `conversation2` | **ambient** — the tick's clock, said to everyone in say-range | `RenownBystander{Lovers,Women,Child}` |
| `sendtell` | **detection** — a player walks up; a *tell*, so it goes to that player alone | `RenownCrier` |
| `event` | **encounter script** — the encounter's own proximity trigger | `RnBLadyInDistressBrainScript` |

`NpcChatterBarks.TriggerOf` is that split, and `ZonePopulationLoader` sorts each NPC type's lines into the three
pools at spawn (`NpcPresentation.ChatterLines` / `GreetingLines` / `EncounterLines`) so the world tick never has to
ask. A single ambient pool would be wrong in two visible ways: the crier would deliver *"You should consider joining
a guild, {DetectedChar.name}!"* on a timer — to a player who had been standing there for ten minutes, or to nobody
at all — and the Damsel's cry for help would fire twice, once on her encounter trigger and again on the clock.

The session slot corroborates the split but runs one way only: every bark carrying one (`DetectedChar.name`,
`Target.Name`) is non-ambient, and no ambient bark carries one — but the converse does not follow. A tell has exactly
one recipient whether or not it names them, so an unaddressed tell would still be said on detection. Hence the channel
is the discriminator and the slot only decides whether the body must be built per-recipient
(`NpcChatterBarks.Addressed`). [V3: NpcBarkTriggerTests.TheCrier_HasOneTell_AndItIsADDRESSED]

Detection state is per NPC (`NpcPresentation.GreetedPlayers`): a player is greeted once on arrival and forgotten again
only after leaving `WorldState.GreetingForgetRange`, which is deliberately wider than `GreetingRange` so someone
loitering on the boundary is not greeted, forgotten and greeted again every few ticks. Both numbers are ours —
`GreetingRange` is matched to `SayRangeUnits` (50 u) on the ground that greeting someone too far away to hear it
would be worse than not greeting them; nothing shipped states an NPC's detection radius, and the one annotated
capture times a crier line without pinning the distance the player crossed to earn it.

Open work tracked in Gitea #502 (the Damsel's encounter).

### The CamelCase sweep is a closed route

`--report chatter-groups` restores CamelCase over the 5,810 lowercased pool names and sweeps them: vocabulary from
every English LDB page plus the name table's filename parts, iterative-deepening segmentation, and an acceptance
test of **two or more** `sayinchat(N)` hits for the same (name, casing, channel). It does not work, and the report
exists to say so with numbers.

| | |
|---|---:|
| casings swept | 152,183 |
| probes | 2,493,366,272 |
| (casing, channel, count) combinations passing the two-hit rule | 3,848 |
| pool names passing it | 781 |
| **…of those, passing under more than one contradictory casing** | **350 (45%)** |

**The two-hit rule assumes the hits are independent and they are not.** Treated as independent, a single 28-bit
collision against the 1,277-entry category has probability ~4.8e-6 and two inside one 512-wide sweep ~3e-9. But the
sweep gives every (casing, channel, K) 256 chances - one per `N` - and the ELF-28 hash's low bits depend only on the
last few characters, so every candidate for one group lands in the same narrow band:

```
sayinchat(137)|broadcastcycle(6)|AchievementsPetHeraldOfDeeds  ->  0x0a93eb03
sayinchat(138)|broadcastcycle(6)|AchievementsPetHeraldOfDeeds  ->  0x0d93eb03
```

The effective space is far smaller than 2²⁸, so hits are cheap. `AchievementsPetHeraldOfDeeds` (correct) and
`AchievementsPetHeRaLdoFdeEds` (nonsense) both resolve nine or more lines.

**Ranking by hit count does not rescue it.** The two known groups the vocabulary can reach do rank first -
`RenownCrier sendtell(3)` 18 lines and `AchievementsPetHeraldOfDeeds broadcastcycle(6)` 13, both matching their
captured `NpcChatter` rows - but the overall top of the ranking is `HalloweenSnakeBoSscOmbAt` (24 lines) and
`PlayerLocalTeLePoRt` (22). And the control settles it: for `renownbystanderlovers`, whose true casing is known
from a capture, the sweep accepts a casing and it is not the true one.

**There is also a vocabulary floor no client-derived source can lift.** `bystander` occurs nowhere in the
client - not in the 152 English LDB pages, not in any of the 192,291 name-table entries - except inside the four
`renownbystander*` names themselves. An authoring word the game never says out loud cannot be recovered from the
game's words, so four of the six known groups are unreachable by segmentation whatever the acceptance test.

So group discovery from the client alone is closed. The lines themselves remain fully readable once a group name is
known, so the route in is a capture that carries the token - the same route that produced the six groups already
harvested.

## An item link in chat carries the item's decryption key

When a player links an item into chat, the client emits the whole `ProtoAcgItem` **as plain text**, inside an
ordinary anchor:

```
<a style="text-decoration:none" href="itemref://T1/T2/T3/LEVEL/KEY1/KEY2/KEY3"><font color=#RGB>[Name]</font></a>
```

so one link yields `Items.Id`, `Name`, `Level` and the `Key1..Key4` TEA quad outright. It rides three carriers in
the corpus — ChatServer `0x0041` (trade channel, most of them), ChatServer `0x0022`, and the c2s Conan `0x2002` —
and `itemref` is the only item-bearing href scheme anywhere in it.

**Why it matters far more than a name.** An item's RDB template is TEA-encrypted under a key the server assigns
per item, so without the key the template cannot be read at all (there is no client-side key source). A chat link
is therefore not an id+name stub — it unlocks the stats, quality, value, damage and search category behind the
record. And it is the only source that reaches items the capturing character never held: whatever a stranger was
advertising in trade chat.

Measured over the 132 dumps: **43 links, 25 distinct templates, 12 of them absent from the catalogue**. The other
13 agree with their stored row exactly on name, level and key quad — that agreement is the oracle, and it is
non-circular because the key comes from the link and never from the database.

- **Read `key1`.** `key3` is sometimes the `NullIdentity` sentinel `b290805c:29e627ca:b290805c:29e627ca` (a
  pre-existence item), and `key2` belongs to the second template of a level-scaled pair — equal to `key1` on
  everything that is not one.
- **The URL drops the hash word** the loot-chat body form carries
  (`[template][template][0][level][hash][k1][k2][k3]`). Harmless: `AcgHash.ForWire` mints a token at send time and
  `Items.Hash == 0` means "not harvested".
- **Both the key words and the colour are unpadded lowercase hex** — `45e19f7` is seven characters and `#6ea7`
  is four — so a build that zero-pads either does not reproduce what the client sent.
- **A link is player-typed text.** The client composes it, but the importer must not let one outrank a
  snapshot-sourced row: `chat-itemref` goes through `CaptureItemImporter.UpsertAsync`, where a name only fills an
  empty one and a level only fills a 0. `ChatItemLinks.In` counts the anchors it could not read rather than
  swallowing them.
  [V3: ItemRefLinkCodecTests.ALinkedItemWeHold_AgreesWithTheStoredKeyQuad]

Open work tracked in Gitea #663 and #668 (in-client check of the ~Guild and ~Team tabs).
