# Project Faolan

Project Faolan is an `Age of Conan` server protocol emulator written in C#.

The solution contains:

- `Faolan/` - the server implementation (UniverseAgent, PlayerAgent, CsPlayerAgent, AgentServer, GameServer)
- `Faolan.Core/` - shared data, networking, enums, crypto, and database code
- `Faolan.PacketAnalyzer/` - packet capture dump/analyzer tooling (pcapng -> JSON)
- `Faolan.Importer/` - CLI that (re)builds the DB from the captures (pcap -> DB); see `docs/faolan-importer-plan.md`
- `Faolan.Wireshark/` - a Wireshark/tshark C plugin that inflates GameServer zlib streams
- `Captures/` - reference `.pcapng` and `.json` traces used for protocol comparison

## Server flow

`UniverseAgent (7010) -> PlayerAgent -> GameServer`

Only **UniverseAgent (7010) is a fixed port**. Every other service (PlayerAgent, CsPlayerAgent,
AgentServer, GameServer) is assigned per session. Each server hands the client the address of the next as
a `hostname:port` string in its payload, e.g. PlayerAgent advertises `gs013-nj4.ageofconan.com:7012` for
the GameServer. The analyzer auto-detects the per-session ports by classifying each endpoint's opcodes.

## Architecture

### Processes & listeners

The server is a single host (`Faolan/Program.cs`) running each protocol service as a hosted background
service via `AddScopedHostedService<...>`: `UniverseAgentListener`, `PlayerAgentListener`,
`CsPlayerAgentListener`, `AgentServerListener`, `GameServerListener`, plus `DatabaseMigrator` (startup
migrate + data import). Each listener is a `Server<ConanPacket, ...>` accepting TCP clients; per-client state
lives on `client.Session` (account, character, conversation id, world-entry flags) as plain in-memory objects.

### Data access (game-server persistence model)

Live state is authoritative **in memory** (the session); the database is a **persistence boundary**, not a
live ORM context.

- The DbContext is **short-lived, one per unit of work**, created from a **pooled `DbContextFactory`**
  (`AddPooledDbContextFactory` in `Statics.ConfigureFaolanDatabase`). It is never held open across a connection
  or a game tick - a long-lived context leaks, goes stale, and isn't thread-safe (sharing one across all
  clients is what silently corrupted early equipment writes).
- `IDatabaseRepository` (`Faolan.Core/Database/DatabaseRepository.cs`) owns **all** context lifetime: every
  read/write opens a fresh context, does its work, disposes. Returned entities are **detached**. One-off work
  uses `Execute` / `Query`.
- **No lazy-loading proxies** (they tether entities to a live context). Navigations are loaded explicitly with
  `Include` at load time (e.g. `GetCharacterEquipment` eager-loads every item slot).
- **Writes are coarse save points.** The session mutates detached in-memory entities during play, then persists
  at save points via repository methods that load the tracked row and copy fields (`SaveCharacter`,
  `SaveCharacterEquipment`, `SaveAccount`, `UpdateLastInfo`). The create-editor accumulates edits across many
  packets on the cached `Session.Character` and commits once at name-time (`SaveCharacter` persists the
  character + Body graph). Setting a navigation on a *tracked* row is what reliably clears an FK to NULL -
  attaching/updating a *detached* graph does not, which silently dropped the first equip/unequip writes.
- The desktop `Faolan.DatabaseEditor` is the deliberate exception: a single-user editing session holds one
  context for the window's lifetime (load -> edit tracked entities in the UI -> save).

### Database & data (the DB is the source of truth)

`faolan.db` (SQLite) is **committed in git and is the single source of truth**. On startup the server only
**migrates the schema** (`DatabaseMigrator`) and reads — it carries no embedded game data and does not seed.

Game data is (re)built from the raw sources by the **`Faolan.Importer`** tool (see
`docs/faolan-importer-plan.md`): it reads the `Captures/` (pcapng / their json dumps / .bin) and upserts the
extracted data straight into the DB. It imports: **item identities** from inventory snapshots
(`CaptureItemImporter` + `ArchetypeHarvester`: template id + `Data1..4` quad + archetype → `Items`,
~**384 items / 177 buildable**); **NPC conversations** (`ConversationExtractor` → `ConversationNode`/
`ConversationPacket`); **maps** from `MapChange` packets (`CaptureMapImporter` → new `Map` rows); and **cast
spell ids** from `ApplySpell` (`CaptureSpellImporter` → id-only `Spell` rows; names/params are client-side). With
`--archive <dir>` it also imports the archive's **ability/spell definitions** (`p_abilities` → `Ability`,
`const_abilitys` → `ClassAbility`) - the spellbook is class/level-derived, not in captures. It also catalogs
**NPC spawns** per map (`NpcSpawnExtractor` → `NpcSpawn`) and stores each map's full **world burst**
(`MapWorldBurstExtractor` → `MapWorldBurst`) - the data the login path replays to render any map. Each run prints
a before/after delta table; imports upsert (no wipes). The OLD C++ server's base data (`p_items`/`spelldata`/`npcs`)
was imported once and now lives in the committed DB (the archive `.sql` files were deleted after that one-time
import; the `Archive/` zips remain the historical source). NPCs (session-handle identity spike) come next.

## Working conventions

- **Pcap files are leading.** If local behavior diverges from a retail capture, fix local to match.
- Verify a packet against a capture (`Faolan.PacketAnalyzer --dump`, or the `Captures/*.json`) before
  changing related code. Prefer pcap-backed changes over speculative guesses.
- Build: `dotnet build '/home/melvin/Development/C#/Project Faolan/ProjectFaolan.slnx' -v q`
- `faolan.db` is the SQLite database; it is migrated and seeded on startup (see Architecture -> Database & seed
  data). The DB is a persistence boundary touched only at save/load points - never a live per-request context.

## Character creation -> world flow (implemented)

Create-new-character works end to end. The flow spans two GameServer connections, both using **one
generated client instance** - the character's real DB id (`PlayerAgent.CreateCharacter` hands it out; no
magic const). This mirrors retail, where the whole create session uses a single generated instance.

1. **Creator (editor) connection.** The client opens a GameServer `Hello` (0x2003) with the new character's
   id. The server sends the editor scene burst so the character creator appears, then the client streams
   its script callbacks (`0xA36D3B74`): `IntroDone`, `ChangeSex/Race/Class/HeadMesh/Size/Voice`, `Finish`,
   `SetMorphValue*`, `TheNameIs`.
   - Morphs: each non-empty `SetMorphValue` is acked with a `0xBADF5A4B` packet that echoes the value.
   - After the morph stream goes quiet (debounced off `Finish` + each morph - the body morphs / `thigh`
     are only sent if the body was actually morphed) the server sends the post-morph trio that prompts
     `TheNameIs`.
   - `TheNameIs` -> commit the name, then `0xADCE0CDA` + `NicknameOk` + `CharCreationFinished`. The named
     char id is added to a static `PendingCreateWorldEntry` set so the *next* Hello is recognised as the
     first world entry (not a normal login).
2. **Enter world.** The world server address is **never derivable** - it is advertised dynamically by the
   creator via a **`0x2002`** packet (`[0x00][byteLen]["host:port"]`) in response to the client's `0x206E`.
   The client then opens a second GameServer `Hello` (same instance) to that address. Because the id is in
   `PendingCreateWorldEntry`, that connection takes the world-entry path: the full creation scene, then the
   Tortage intro cutscene (`ThisIsKalanthes` -> shipwreck -> `Kneel` -> beach -> `ApproachCasilda`)
   delivered one chunk per client trigger (`ClientLoadingDone`, `StartScript`x2, `Move`, `IntroDone`).
   - The post-`IntroDone` chunk is truncated at the `ApproachCasilda` finale; the recorded gameplay tail
     (combat spawns, movement, the NPC conversation) is dropped. The intro NPC conversation is replayed only
     when the player clicks the NPC (an `Interact` / `ActionType 0x16`), keeping it interaction-driven.
   - The player spawn in the scene is the **retail player Spawn packet** (capture `dikketest` idx 484, the
     `0xFA015807` for the player), constructed for the session instance, so position/orientation/pose match
     retail. (It still carries the capture's appearance + name until per-character customization is
     persisted.) The hand-built `SpawnPlayer` is used for the normal login path.

The flow lives in `Faolan/GameServer/Retail*Burst.cs` (sender methods), the handshake logic in
`Faolan/GameServer/CreateWorldHandshake.cs`, and the `0xA36D3B74` handler in
`Faolan/GameServer/Packets/HandleCommandPacket.cs`.

### Packets are constructed explicitly

Every packet inline with `PacketStream` - no verbatim byte blobs, no array+loop:

```csharp
var id = client.Session.Account.ClientInstance;
await new PacketStream().WriteHeader(sender, receiver, opcode)
    .WriteArray(/* before id */).WriteUInt32(id).WriteArray(/* after id */)   // when the body carries the id
    .SendAsync(client);
await new PacketStream().WriteHeader(sender, receiver, opcode).WriteArray(body).SendAsync(client); // when it doesn't
```

The client instance goes into the body via `WriteUInt32(id)` and into the receiver/sender conversation id
via `AppendVarint([family-prefix], id)` (helpers in `Faolan/GameServer/PacketBurstHelper.cs`). To make any
other field editable, split that `WriteArray(...)` and write the value.

`Account.ClientInstance` is the session's client-used id (the real character id throughout the create flow
and during play). It is deliberately **not** a foreign key, so deleting a character never cascades into
deleting the account.

Character ids are allocated in the **4-byte protobuf-varint range** `[0x02000000, 0x0FFFFFFF]`
(`DatabaseRepository.AllocateCharacterId`, with `Character.Id` set `ValueGeneratedNever`). The id doubles
as the on-wire client instance, and the verbatim packets have nested length prefixes sized for a 4-byte
varint, so the instance must encode to 4 bytes everywhere it is substituted. (SQLite autoincrement, seeded
high by a manual char, produced 5-byte-varint ids that corrupted those prefixes.)

## Conversation ids (sender / receiver headers)

Every Conan packet header carries a **sender** and **receiver** conversation id, each a small protobuf
message: a `fixed32` family tag + a varint sub-id, e.g. sender `0D 13 CE 71 B1 10 <sub>` and receiver
`0D 47 C1 67 6C 10 <varint clientInstance>`. The receiver's trailing varint is the **client instance**
(`0x021662C8` -> `C8 C5 D9 10`); a few server->client packets instead carry the instance in the sender tail.

A whole world session rides **one conversation**. Confirmed from the Strie login capture, the entire login
world-entry (auth `200B/200C/2001`, `MapChange`, world objects, the player spawn, **all NPC spawns**, time)
uses sender `0D 13 CE 71 B1 10 58` + receiver `0D 47 C1 67 6C 10 + varint(char instance)` (the `0x201C`
handshake / spawn sub-protocol uses a different family `0D 91 F7 87 8B 10` but the same `58` sub-id +
instance tail). Conversation alignment is necessary but **not** what gates rendering - see the spawn
sub-protocol below; an aligned-but-incomplete world setup still renders nothing.

Practical consequence: anything that builds a packet by hand must use the session conversation, not a
hardcoded id copied from a capture. The login world-entry builders (`Send0x2000_MapChange`,
`Send0x2000_SpawnPlayer`, `Send0x2000_0x737A6DF9`, `Send0x201C`, the auth in `Packets.cs`, `SetTimeOfDay`)
use the shared `LoginWorldSender` / `LoginWorldReceiver(client)` helpers (PacketBurstHelper.cs); the
create-flow bursts append the instance via `AppendVarint([family-prefix], id)`.

### Auth-layer receivers echo the client (UniverseAgent / PlayerAgent)

The pre-world auth services don't have a character instance yet. Instead, **every server->client response
echoes the client's own conversation id back as the receiver** - the value the client put in its *sender*
field. Each listener captures it once per packet (`SenderBytes.ToArray()[2..]`, skipping the `0x0a`/len
tag) into `Session.ClientConversation`, and the response builders use that as the receiver:

- **UniverseAgent** (`UniverseAgent/Packets.cs`): `SendPlayerAgentRealm` (0x2001), `SetRegion` (0x2005),
  `SendInvalidLogin` (0x2001). Only `InitAuth` (0x2000) keeps a server-assigned id - it issues the
  conversation before the client has one, so it is not derivable from captures.
- **PlayerAgent** (`PlayerAgent/Packets.cs` + `PlayerAgentListener.cs`): every response, including the
  `0x1A07` service-connection handoffs (`CsPlayerAgent`, `AgentServer`, `GameServer`).

Verified across `faolanx`, `demol80`, and `Strie`: the receiver in the `0x1A07` packets differs per session
and equals byte-for-byte the client's PlayerAgent sender (e.g. `faolanx` `0D 38 57 15 7D 10 EC EB 80 DE 03`
vs `Strie` `...EE A0 A9 CF 05`). The `0x1A07` *headerData* (the `0D 38 57 15 7D 10 01 20 <id>` blobs) and the
body layout (ip / port / accountId / `0xC350` / clientInstance) are otherwise constant/derived. A
capture-derived hardcoded receiver only worked for the one account it was copied from.

### Tortage beach NPCs

`SpawnTortageBeach(client)` (`RetailTortageBeachSpawn.cs`) spawns the persistent beach NPCs (Casilda,
Kalanthes, crocodiles, scavengers, pillagers, panthers, a poacher) verbatim from the dikketest capture -
each keeps its own handle/appearance/position; only the receiver is built for the session instance. It is
called from the login world-entry (gated to the Tortage beach map `0xFAA`) so login shows the same beach
the create flow streams. The galley objects (cutscene-only, they sink) and the combat NPCs that stream in
during play are intentionally excluded. **This hand-built path does not render NPCs** (it omits the static
world setup - see below); it is kept only as the fallback for non-replay characters.

## Existing-character login: world build + spawn sub-protocol

What actually makes the client render the world (diagnosed by diffing our `logs.txt` against the Strie login
capture; tool: `Captures/compare_log_to_dump.py`). Conversation alignment was already correct - that was
**not** the problem. The retail world-entry order is:

```
200B x1 -> 200C x1 -> 201C x5 (channel handshake)
  -> 2000 x71  (the world build, all opcode 0x2000 with different sub-opcodes, sent up front)
  -> client floods 0x2020 (per-entity spawn polls)
  -> server 0x2008 SpawnCheck x54 (one per poll) -> 0x2076 manifest -> 0x2024 -> 0x2052 x7
```

The `2000 x71` burst builds the **static world first**: `MapChange` (`5D85BFC7`), then world objects /
geometry (`SpawnObject 66AEDD50` x5, `F096251E` x6, `33A56FB0`), then the entity `Spawn`s (`FA015807`) and
their `96C46740` registrations, then the inventory dump (`737A6DF9`). Our old hand-built login sent
`MapChange` then jumped straight to entity spawns, omitting every world object - so the client had no
playfield, **discarded the spawns, and never polled** (`0x2020` count was 0). Adding the world objects is
what unblocks rendering.

Key sub-protocol facts:
- The `0x201C` channel handshake is **five** registrations: prefix `0x65` states `{5, 6, 0x10, 0x12}` then
  prefix `0x68` state `1` (`Send0x201C.cs`). An incomplete set stalls the client before it ever polls.
- `0x2020` (client->server, 16 bytes `C350 <handle> C350 <instance>`) is a spawn poll for one entity handle;
  the server answers each with `0x2008` SpawnCheck (9 bytes `C350 <handle> <status>`, status `0x02` for the
  player's own avatar, else `0x00`) on the `0x91F7878B10` (201C) conversation.
- The entity handle is assigned by the `FA015807` Spawn packet; the player's own handle is the first spawn.

### World-burst replay + progressive DB-driven rebuild (`RetailLoginReplay.cs`)

A real login is ~244 packets (about half of them world build) with character-specific bodies, so the working
path **replays a captured world burst from the DB** and rewrites it for the logged-in character. Each map's burst
(every `0x2000` download from `MapChange` to the first `0x2020` poll) lives in the **`MapWorldBurst` /
`MapWorldBurstPacket`** tables, imported from the captures by `Faolan.Importer` (`MapWorldBurstExtractor`). On
existing-character login, if the character's map has a stored burst, `ReplayMapWorldBurst` sends it (re-addressed
from the burst's `CapturedInstance` to the live character) and `SendSpawnCheckResponse` answers the `0x2020`
polls; otherwise a hand-built fallback (no NPCs) runs. This **generalizes the old beach-only path to any map** -
the beach (4010) is just the burst whose source is the proven 71-packet blob (seeded into the DB by the importer;
the embedded resource was retired). What we do to each burst packet:

- **Instance re-address** (`RewriteBurstInstance`): the burst's captured instance is replaced with the live
  character instance everywhere - as the receiver/inventory varint and the raw entity handle/owner uint32. Both
  are 4 bytes for in-range ids (`0x02000000..0x0FFFFFFF`), so it is length-preserving; the CRC is recomputed.
- **Player spawn from DB** (`TryRebuildPlayerSpawnFromDb` + `Spawn/SpawnPacketBuilder.cs`): the player's own
  `FA015807` is rebuilt with the DB character's **position** and **name** (`SetSpawnName` adjusts the name's
  nested length prefixes). Rotation is kept verbatim (DB rotation rebuild stalled the client). Race lives at
  `field4.field1` (enum-confirmed); morphs/appearance are not yet decoded into the spawn.
- **Inventory from the DB** (`Packets/0x737A6DF9_Test/`): the `0x737A6DF9` snapshot is parsed
  (`InventorySnapshotInspector`), then entries are replaced **in place** from the character's
  `CharacterEquipment` and re-serialized (parse->rebuild is byte-exact, tests). An item entry is built from a
  real DB `Item`: tail `f4/f5` = `Item.Id`, the identity payloads = `Item.Data1..4`, and `tail.f8` = `Item.Archetype`.
  `SnapshotItemBuilder.BuildEntry` reproduces a captured entry byte-for-byte (the proof that a real-identity item
  is accepted in-client). Items in the DB come from the one-time `p_items` base import (named templates + quad)
  plus the **`Faolan.Importer`** tool, which harvests the full identity (quad + archetype) of every item seen in a
  real captured inventory straight into `Items` (`CaptureItemImporter` + `ArchetypeHarvester`; re-run the tool
  when adding captures). An item is **buildable iff `Archetype != 0`** (~177 items today; archetype is only in
  captures, not `p_items`). Beach characters are seeded default starter
  gear (`EnsureBeachStarterEquipment`); equip/unequip persists back to `CharacterEquipment`
  (`EquipmentPersistence.cs`, from the `0x642CD3D6` `MoveEquippedToFromInventory` action). This settled the old
  "add/remove stalls" problem: the stall was a *fabricated* item identity - a real item works. Replace-in-place
  only reflects slots the captured snapshot already has; rendering items in new equip slots (e.g. a ring) on
  relogin needs ADD/REMOVE of entries (next step).

GOTCHA - the send loop must be tight. Burst packets go out one raw `client.SendAsync(byte[])` after another;
the GameServer download is uncompressed and the client frames it per TCP read, so **any heavy CPU work between
sends (e.g. parsing/re-serializing the 9 KB inventory mid-loop, or PacketStream's synchronous CRC) reshapes
the segmentation and the client misframes and hangs on the loading screen.** Pre-build every packet (instance
rewrite, spawn rebuild, inventory rebuild) BEFORE the send loop, then send in a pure loop.

This replaces the NPC-less hand-built path for beach logins. The end goal (see `docs/spawn-dissection-plan.md`)
is to construct the whole world entry from the DB; in progress - done so far: position, name, handles,
instance-generalization, the archive data import, and DB-driven inventory with equip/unequip persistence.

### NPC dialogue (`ConversationReplay.cs`)

Talking to an NPC sends `Interact` (`642CD3D6`, actionType `0x16`); `(TypeId, Mode, Field3)` from the args
select the conversation node, and `SpellTarget.field2` is the clicked NPC handle. `ConversationReplay` looks the
node up in the DB (`ConversationNode`/`ConversationPacket`, via `GetConversationNode`), substitutes the live NPC
handle + instance (raw and varint forms), and sends the `4A3D899A`/`F164A4A5` `remoteformat` dialogue packets.
The nodes are extracted from captures into the DB by **`Faolan.Importer`** (`ConversationExtractor`), which
replaced the old `extract_conversations.py` + embedded `conversations-strie.bin`. Coverage-bound: only captured
`(TypeId,Mode,Field3)` branches replay; full trees need the conversation content (not derivable from packets).

## Packet capture workflow

Dump a capture to JSON (stdout):

```zsh
dotnet '/home/melvin/Development/C#/Project Faolan/Faolan.PacketAnalyzer/bin/Debug/net10.0/Faolan.PacketAnalyzer.dll' --dump '/home/melvin/Development/C#/Project Faolan/Captures/<capture>.pcapng' > '/home/melvin/Development/C#/Project Faolan/Captures/<capture>.json'
```

The analyzer now does **TCP reassembly** (orders by sequence, drops retransmits, buffers gaps) before
inflating - without it, retransmitted/out-of-order segments corrupt the single zlib GameServer download
stream and ~2/3 of the stream is lost. **Re-dump any capture made before this fix** - older JSON dumps are
truncated.

### GameServer download compression

The GameServer download stream is one continuous zlib stream, prefixed by a 9-byte header that starts with
the marker `0x80 00 00 05`. The **decompressed** stream also contains in-band 9-byte `0x80000005` markers
between packet groups (literal stream framing, not packet lengths) - the framer skips them.

### Client-side logs (ground truth for "why did the client reject it")

The AoC client writes its own log - invaluable when the client silently stalls/disconnects:
`/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/AgeOfConan.log` (current session) and
`.../OldLogs/AgeOfConan<UTC-timestamp>.log` (rotated). Timestamps are `Z`/UTC (local = UTC+2). Look for
`stream bad`, `Dispatcher call ... failed`, `Playfield.Loading`, and `ResourceManager ... does not exist`
(the last are expected - the client is missing most RDB assets). KNOWN PRE-EXISTING (every session, harmless):
`DispatchOverrideGameTweaks ... stream bad after 'overrides'` / `Dispatcher call 2085091171 (OverrideGameTweaks)
failed` - a game-tweaks/overrides packet we send is malformed; the client disconnects that one route but
recovers. Worth fixing for cleanliness; not a login blocker.

### Dissection workbench & tooling (`Captures/*.py`, `Faolan/GameServer/Spawn/`)

- `compare_log_to_dump.py` - decode our `logs.txt` GameServer output and a capture JSON into (conversation
  family, opcode) counts + chronological timeline, instance-independent. The primary diff tool.
- `dissect_spawn.py` - break an `FA015807` spawn into handle/position/rotation/name + a `diff` mode.
- NPC conversation extraction moved into the `Faolan.Importer` tool (`ConversationExtractor`), which writes the
  `ConversationNode`/`ConversationPacket` DB tables (replaced the old `extract_conversations.py`).
- `SpawnPacketBuilder` (+ `SpawnHandleTable`) and the inventory parser are unit-tested in
  `Faolan.PacketAnalyzer.Tests` (byte-exact round-trip is the acceptance gate before anything ships dynamic).

### Wireshark plugin (ground truth)

`Faolan.Wireshark/` builds an EPAN plugin (`faolan_gs_zlib.so`) that inflates GameServer streams with real
zlib + Wireshark's TCP reassembly. Use it to validate the analyzer:

```zsh
tshark -r '<capture>.pcapng' -d tcp.port==<gsPort>,faolan_gs \
  -Y 'faolan_gs && faolan_gs.output_bytes>0 && ip.src==<gsIp> && tcp.srcport==<gsPort>' \
  -T fields -e faolan_gs.output_blob
```

GUI display filter: `(tcp.port >= 7000 && tcp.port < 7100) && (data || faolan_gs)`. Reassemble the
`output_blob` bytes; they contain decompressed Conan packets with literal 9-byte `0x80000005` markers
between groups.

## Retail reference values

`stricken997` / `Strie`:
- Account handoff ids are **session-scoped** (differ per session): create `0x3391A2B5`, login `0x36CF73BD`.
- Character `Strie` id: `0x02165EFF` (`35020543`).

`stricken998` create captures:
- The capture's create session instance is `0x01316D2B` (used everywhere). The `Retail*Burst.cs` packets
  were normalised to the placeholder `0x021662C8` at generation time; at send time they are rebuilt with
  the live character id, so `0x021662C8` no longer appears on the wire.
- `ReadyForPlayScreen` subopcode `0x864CFEF8`; post-ready family `0xF508F4C1`.
- World address advertised via `0x2002` (e.g. `dikketest`: creator `:7016`, world `:7012` - **not** +1).
- `dikketest` world connection: 585 GameServer download packets / ~172 KB (the create->world + cutscene).
- Player Spawn = `dikketest` world-conn idx 484 (`0xFA015807`); position floats are **little-endian**
  (`322.5, 145.31, 704.9`) - that's both the cutscene start and the post-cutscene beach drop (the player
  washes up there and walks). Note `Move` (`0x4517AA81`) position floats are **big-endian**.

Manually-inserted local char `ConanHimself` clientInstance `0x1101B4F5` - a 5-byte-varint id (`>= 2^28`),
outside retail's range. New chars are now allocated in the 4-byte range instead (see above), so log in with
a freshly-created char rather than `ConanHimself` for retail-faithful instance bytes.

`stricken997` / `Strie` login: the whole world-entry uses sender `0D 13 CE 71 B1 10 58` + receiver
`0D 47 C1 67 6C 10 + varint(0x02165EFF)` (`FF BD D9 10`) - the single login conversation (see below).

## Protocol notes

- `SmallCharList` (PlayerAgent `0x20A5`): count = `(n+1) * 1009`, then per character `Id` + length-prefixed
  `AuthChallenge`. No per-entry trailer (an extra field shifts subsequent entries and the client loops with
  2+ characters).
- Conversation ids: see the "Conversation ids" section above. The receiver's trailing varint is the client
  instance; a world session rides one conversation; hand-built packets must use the session conversation.
- `0x2003` is overloaded: a 24-byte body is the connection `Hello` (clientInstance + accountId + version);
  a 4-byte body is an in-world entity notification (a uint32 handle, no reply). Branch on body length.
- `0x2000` is overloaded: `GameServer.AckAuthentication` and `UniverseAgent.SendPlayerAgentRealm` are both
  `0x2001`; the analyzer disambiguates by port.
- World build + spawn polling (`0x2020`/`0x2008`/`0x2076`/`0x2052`): see "Existing-character login" above.
  The static world objects must be sent before entity spawns or nothing renders.
- `Captures/compare_log_to_dump.py` decodes our `logs.txt` and a capture JSON into (conversation family,
  opcode) counts + a chronological timeline, independent of the client instance - the primary tool for
  finding which world-entry packets we send wrong or omit. Default dump: the Strie login.

## Roadmap / current focus

`docs/spawn-dissection-plan.md` is the living plan for replacing the verbatim replay with DB-driven world
entry. Status: world + NPCs render for any beach character; player **position** and **name** come from the DB;
NPC **dialogue** works (coverage-bound); the archive **item/spell/NPC data** is imported; **inventory** items
are built from the DB (`CharacterEquipment` x `Items`) and **equip/unequip persists**; ~177 items are buildable
(every item seen in a real captured inventory). In progress / blocked: **appearance morphs** (spawn encoding not
crackable from the available captures - needs chars differing in one morph at a time); **add/remove of inventory
entries** so persisted gear renders in any slot on relogin (only slots present in the captured snapshot reflect
today); and ultimately the full DB-built snapshot incl. the tails.

## TODO

Concrete next steps, grouped by area. See `docs/` for the dissection writeups behind each.

### Stats & combat (`docs/player-stats.md`, `docs/quests-and-combat.md`)
- [ ] **Level-up path**: bump `Character.Level`, call `Character.RegenerateMaxStats()`, `SaveCharacter`, then
      push a `0x96B8DC59` vitals update (`SendVitals`) so the client sees the new stats. (Hold until the vitals
      packet is confirmed rendering in-client.)
- [ ] **Verify the `0x96B8DC59` vitals packet in-client** (test live with `.vitals` / `.setstat`), then wire
      `SendVitals(client)` into the world-entry sequence so vitals show on login.
- [ ] Confirm **ApplySpell (`0x6B87E316`) magnitude** sign/scale, and whether damage is carried in the cast or
      only in the following vitals update. (Combat loop is implemented in `Faolan/GameServer/Combat/`:
      `CastSpell` → ApplySpell + damage + `0x96B8DC59` vitals update; damage formula is provisional.)
- [ ] **Combat death/XP**: decode the death/despawn packet and the XP grant; wire into `ResolveOffensiveCast`
      (currently HP→0 is detected and logged only). Real **NPC max HP** (placeholder today).
- [ ] **Mounts**: the summon spell is `0x3BAD7D` (channeled ApplySpell `0x6B87E316`, decoded from demol80's
      `very_long_loading` capture; `.mount` broadcasts it). Still needed for full mounting: spawn the mount
      entity (`0xFA015807`, e.g. "Fading Black Horse") and drive the MountedRun (`MovingType 0x010A`) transition.
- [ ] Fix the wrong stat ids in `CapturedCharacterStats.cs`: `MaxHealth 0x01` (decodes to ~196, not max),
      `MaxStamina 0x1F9` / `MaxMana 0x1FB` (suspect); find the real max-* ids or confirm max==current-when-full.
- [ ] Determine whether **attributes** (Str/Int/Con/Wis/Dex) are sent at all or computed client-side from
      gear/buffs (their sheet values don't appear as plain stat records).
- [ ] Decode the high-volume combat entity-update sub-ops `0xFEC4158A` / `0xD751EBB4` / `0x04B82B2D`
      (animation / aggro / buff state, unconfirmed).

### Conversations / NPC routing
- [ ] **Wrong NPC dialogue** (e.g. talking to Casilda shows Kalanthes text). Two causes: (1) the create-world
      fallback `SendNpcIntroConversation` sends ONE hardcoded conversation (id 12493) for every NPC; (2) DB nodes
      are keyed by `(TypeId, Mode, Field3)` only. Partial fix done: `TryReplayConversation` now also runs in
      create-world sessions (DB routes by the client's NPC-specific Mode/Field3), with the hardcoded intro as a
      last-resort fallback. STILL TODO: verify the client sends NPC-distinct `(Mode, Field3)` (needs a create-flow
      capture talking to Casilda); decode the NPC spawn's stable conversation/dialogue id so handle→conversation
      is unambiguous; rebuild the create-world intro per-NPC instead of the single hardcoded 12493.

### Quests (`docs/quests-and-combat.md`, `docs/capture-events.md`)
- [ ] Isolate the **quest accept vs decline** action and the **quest-log / objective state** packet (numeric
      quest ids; candidate `0x2038` title / `0x20a5` tracker pair, bodies undecoded).
- [ ] Decode the **quest-complete / reward grant** beyond the `0xA36D3B74` "QuestRewards" token (the actual
      reward/turn-in state change).
- [ ] Isolate the **level-up "ding"** event packet (Level rises in the snapshot/vitals, but the event packet
      isn't pinned).

### World entry & rendering (`docs/spawn-dissection-plan.md`)
- [ ] **Appearance morphs**: decode the spawn appearance encoding (blocked — needs captures of characters
      differing by one morph at a time).
- [ ] **Add/remove inventory entries** so persisted gear renders in any slot on relogin (today only slots
      present in the captured snapshot reflect).
- [ ] Build the **full DB snapshot incl. tails** (retire the captured-burst replay entirely).
- [ ] **NPCs from the DB** (session-handle identity) instead of verbatim burst spawns.

### Mail / auction / trade-post (`docs/capture-events.md`)
- [ ] Implement the **`0x2002 category=629`** token remoteformats (`TradePostGUI_*`) — the mail/auction/
      trade-post UI protocol.

### Cleanup
- [ ] Fix the malformed **`OverrideGameTweaks` (`0x2085...`)** packet — client logs `stream bad after
      'overrides'` and drops that route every session (harmless, not a login blocker).

## Notes

- The repository still contains hardcoded/verbatim protocol payloads (the `Retail*Burst.cs` create-flow files).
  The only embedded data left in the server is `Faolan/GameServer/Data/spawn-player-template.bin` (used to rebuild
  the player's own spawn). The `strie-login-worldburst.bin` file stays in the repo only as the import source for
  the beach burst (not embedded). Game data + world bursts are NOT embedded - they live in the committed `faolan.db`, (re)built
  from `Captures/` by the `Faolan.Importer` tool.
- Generating EF migrations needs the design-time factory in `Faolan/Helpers/DatabaseContextDesignTimeFactory.cs`
  (the runtime `ConfigureFaolanDatabase` registration - now a pooled `DbContextFactory` - can't be used by
  `dotnet ef`).
- Build: `dotnet build '/home/melvin/Development/C#/Project Faolan/ProjectFaolan.slnx' -v q`. Tests:
  `dotnet test Faolan.PacketAnalyzer.Tests/Faolan.PacketAnalyzer.Tests.csproj` (the spawn/inventory round-trip
  tests are the acceptance gate before anything ships dynamic).
