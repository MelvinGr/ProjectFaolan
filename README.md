# Project Faolan

Project Faolan is an `Age of Conan` server protocol emulator written in C#.

The solution contains:

- `Faolan/` - the server implementation (UniverseAgent, PlayerAgent, CsPlayerAgent, AgentServer, GameServer)
- `Faolan.Core/` - shared data, networking, enums, crypto, and database code
- `Faolan.PacketAnalyzer/` - packet capture dump/analyzer tooling (pcapng -> JSON)
- `Faolan.Importer/` - CLI that (re)builds the DB from the captures + client RDB (pcap -> DB); see `docs/archive/faolan-importer-plan.md`
- `Faolan.ClientLauncher/` - launches the retail client unattended (patcher stand-in; auto-login; see `docs/client-launcher.md`)
- `Faolan.Wireshark/` - a Wireshark/tshark C plugin that fully dissects the Conan protocol (framing, zlib, RpcHeader endpoints, opcodes, handoff auto-follow)
- `Faolan.Tests/` - the unit/golden test suite (byte-exact round-trips are the acceptance gates)
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
The same host also runs an **in-process MCP server** (`Faolan/Mcp/`) for Claude tooling - see "Claude / MCP
tooling" below.

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
`docs/archive/faolan-importer-plan.md`): it reads the `Captures/` (pcapng / their json dumps / .bin) and upserts the
extracted data straight into the DB. It imports: **item identities** from inventory snapshots
(`CaptureItemImporter` + `ArchetypeHarvester`: template id + `Data1..4` quad + archetype → `Items`,
~**388 items / 191 buildable**); **NPC conversations** (`ConversationExtractor` → `ConversationNode`/
`ConversationPacket`); **maps** from `MapChange` packets (`CaptureMapImporter` → new `Map` rows); and **cast
spell ids** from `ApplySpell` (`CaptureSpellImporter` → id-only `Spell` rows; names/params are client-side). With
`--archive <dir>` it also imports the archive's **ability/spell definitions** (`p_abilities` → `Ability`,
`const_abilitys` → `ClassAbility`) - the spellbook is class/level-derived, not in captures. With `--rdb <dir>`
it imports the client's **character-customization registry** (RDB type 1000076 → `MorphTarget`, 1,072 rows:
every selectable appearance target; wire tag = registry id − 1 — what resolves the editor's resource refs into
the spawn's weighted entries). It **materializes the captured characters** (`CaptureCharacterImporter`): every
named player spawn (own chars AND other players visible in gameplay) becomes a playable DB character on its
ORIGINAL retail id — identity, class (the pre-IIR frame island @135's low byte; cross-checked against the
PlayerAgent 0x20EF char lists), looks, vitals, position/map, worn equipment, carried bag/quest inventory (from
the FullChar snapshots) and the full spawn-scaffold rows; existing names are skipped (re-runs never overwrite),
and they land on the dedicated `captured` account (`CaptureCharacterImporter.AccountId` to override). It also
catalogs **NPC spawns** per map (`NpcSpawnExtractor` → `NpcSpawn`) and
stores each map's full **world burst**
(`MapWorldBurstExtractor` → `MapWorldBurst`/`MapWorldBurstEntity`) - the data the login path replays to render any map. Each run prints
a before/after delta table; imports upsert (no wipes). The OLD C++ server's base data (`p_items`/`spelldata`/`npcs`)
was imported once and now lives in the committed DB (the archive `.sql` files were deleted after that one-time
import; the `Archive/` zips remain the historical source). NPCs (session-handle identity spike) come next.

**Buildable item identity only comes from the player's own inventory snapshot.** Vendor stock and auction-house
listings do NOT yield more buildable items (verified): AoC sends those by *referencing* item template ids (the
client renders them from its local RDB), so the full `Data1..4` quad + archetype the builder needs is never on
the wire there - the item-identity quad signature appears in zero non-`0x737A6DF9` packets in the trader/auction
captures. To grow the item set, capture the player actually holding/equipping more diverse items (their snapshot
is harvested); vendor/AH/NPC-gear packets don't help.

## Claude / MCP tooling

The server hosts an **in-process MCP server** (streamable HTTP) so Claude can both query the DB and interact
with the **live** running server. It lives in `Faolan/Mcp/` and is registered with one line
(`builder.Services.AddFaolanMcp()` in `Program.cs`).

- **Transport / hosting.** A nested ASP.NET Core / Kestrel app (`FaolanMcpHostedService`) runs as an ordinary
  hosted service on `McpPort` (faolan.json, default **7099**), serving `/mcp`. The generic host is otherwise
  untouched (no Web SDK switch); `Faolan.csproj` adds `<FrameworkReference Include="Microsoft.AspNetCore.App"/>`
  + `<AllowMissingPrunePackageData>true</AllowMissingPrunePackageData>` (NETSDK1226) and the
  `ModelContextProtocol.AspNetCore` package. The nested host **shares Faolan's root-container singletons by
  instance** (`IDatabaseRepository`, `IAgentServerDispatcher`, `IGameServerDispatcher`), so tools observe the
  exact same live objects the server uses - that is what lets them inspect/drive the running server, not just
  the DB file.
- **Connect Claude** (server must be running): `claude mcp add --transport http faolan http://127.0.0.1:7099/mcp`.
- **Tools** (`Faolan/Mcp/Tools/`, attribute-based `[McpServerToolType]`/`[McpServerTool]`, one-per-action):
  - DB reads via `IDatabaseRepository`: `get_item`, `get_spell`, `list_maps`, `get_map_npc_spawns`,
    `get_map_world_objects`.
  - Live agent sessions: `list_sessions`.
  - Live world: `list_world_sessions`, `run_gm_command`, `teleport_character`. **Mutating** world tools are
    gated behind `confirm=true`.
- **GameServer dispatcher.** World actions route through a `IGameServerDispatcher` that mirrors the agent
  dispatcher but is **multi-provider**: both GameServer listeners (creator + world ports) register, sessions
  aggregate across them, and a GM command routes to whichever listener holds the character. The listener
  implements the provider by reusing the already-validated GM command paths (`ClientForCharacter` +
  `HandleGmCommand`) rather than hand-rolled spawn protocol - so e.g. `run_gm_command(... "test")` spawns the
  test NPC and `teleport_character` wraps `.teleport`.
- **Open ends:** native MCP elicitation for the confirm step (currently a `confirm` bool); a dedicated
  on-demand spawn tool over the burst-free `NpcSpawnBuilder`/`MoveTaskBuilder` path (needs in-client validation).

## Working conventions

- **Pcap files are leading.** If local behavior diverges from a retail capture, fix local to match.
- Verify a packet against a capture (`Faolan.PacketAnalyzer --dump`, or the `Captures/*.json`) before
  changing related code. Prefer pcap-backed changes over speculative guesses.
- Build: `dotnet build '/home/melvin/Development/C#/Project Faolan/ProjectFaolan.slnx' -v q`
- `faolan.db` is the SQLite database; the server only **migrates the schema** on startup and reads (it carries no
  embedded game data and does not seed - data is built by `Faolan.Importer`; see "Database & data"). The DB is a
  persistence boundary touched only at save/load points - never a live per-request context.

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
   - The player spawn is **generated from the character's own DB rows** - name, race/sex/class, level,
     vitals, equipment, AND the full editor appearance: head mesh / voice / size (IIR `CharacterStats`,
     each = the editor value ÷ 2), the chosen resources (hair/beard mesh, hair/eye/skin color, tattoos,
     scars - the scaffold's weighted entries, resolved via the imported `MorphTarget` registry) and the
     morph sliders (zigzag-decoded editor values; spawn float = value/1000). See
     `docs/archive/player-spawn-generation-plan.md`.

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

**Converting the verbatim `Retail*Burst.cs` blobs (in progress).** Now that the IIR sub-opcodes are decoded,
the literal byte-blob bursts are being converted to named `Iir()`/`IirId()` helper calls (0x2000 envelope ->
`WriteUInt32(<IIR enum>).WriteArray(body)`, auto-computed length). Each conversion is proven **byte-identical** by
a golden test: `Faolan.Tests/BurstGoldenTests.cs` + `RecordingNetworkClient` capture each burst's exact bytes to a
committed `Faolan.Tests/Golden/<name>.bin`, so a single changed wire byte fails (the client only accepts the exact
retail bytes). `Captures/convert_burst.py` is the codemod: it converts only GameAgent->GameInterface 0x2000 IIR
packets with pure-byte bodies and leaves everything else verbatim (other opcodes/families, or packets with
computed sub-arrays like `EncodeVarint(id)`). Done: `CreationScriptBurst`, `InitialBurst`, `WorldPrepBurst`.
TODO: `RetailCreateWorldEntryBurst` (instance method, 391 packets across 3 families - needs per-family helpers);
`Post205FBurst` uses different families with no IIR sub-opcode (codemod N/A).

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

The `fixed32` family tag is a **BKDR-31 hash of the client interface/agent class name** (recovered by RE'ing
`AgeOfConan.exe` - see "Client reverse-engineering" below): the login `0D 13 CE 71 B1` = `hash("GameAgent")`
(`0xB171CE13`, little-endian) and `0D 47 C1 67 6C` = `hash("GameInterface")` (`0x6C67C147`); the `0x201C` spawn
family `0D 91 F7 87 8B` = `hash("GameCharInterface")` and its caller `hash("GameCharAgent")`. The constants and
their real names live in `Faolan.Core/Network/Conan/ConanEndpoints.cs`.

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

### Ability grants (`Send0x2052_GrantAbilities.cs`)

The player's castable attacks are delivered by the **`0x2052`** opcode (a top-level opcode on the spawn/`0x201C`
conversation - **not** a `0x2000` sub-opcode), one packet per ability. The old C++ scheme (`addAbility` opcode
`0x4d`) is dead; `0x2052` is the retail packet, decoded from the `strickenbla` combat capture (pkt #399/#401).
Body after the 16-bit opcode (big-endian u32s): `[slot][id][id][0][1][data0][data1..4][data1..4][constA][constB]
[constA][constB][1][0][u8 flag]`, where `constA=0xB290805C`/`constB=0x29E627CA` are fixed for every ability and
`slot` is the ability's template word (`Abilities.Slot`). The grant set is **data-driven from the captures**:
`CaptureAbilityImporter` harvests every 0x2052 grant into the `Abilities` table (id + Slot + identity quad), and
flags the ones seen in the CREATE-flow captures as `IsStartingAbility` - the universal kit every new character
gets (8: 769094/769095/769099 Normal Attack L/R/Front, Toggle Combat, Switch Weapon, Rest, + 2 utility). The
table also catalogs the higher-level/class abilities from gameplay captures (26 total, not granted at start).
`GrantStartingAbilities` sends `BuildAbilityGrantBody` (byte-for-byte locked by `AbilityGrantTests`) for each
starting ability after the world-burst replay, for every class. The cast itself is the already-working
`Interact`/`ApplySpell` combat path. (Spells are a separate grant not present in any capture - see the TODO.)

### World-burst replay + progressive DB-driven rebuild (`RetailLoginReplay.cs`)

A real login is ~244 packets (about half of them world build) with character-specific bodies, so the working
path **replays a captured world burst from the DB** and rewrites it for the logged-in character. Each map's burst
(every `0x2000` download from `MapChange` to the first `0x2020` poll) lives in the **`MapWorldBurst` /
`MapWorldBurstEntity`** tables, imported from the captures by `Faolan.Importer` (`MapWorldBurstExtractor`). On
existing-character login, if the character's map has a stored burst, `ReplayMapWorldBurst` sends it (re-addressed
from the burst's `CapturedInstance` to the live character) and `SendSpawnCheckResponse` answers the `0x2020`
polls; otherwise a hand-built fallback (no NPCs) runs. This **generalizes the old beach-only path to any map** -
the beach (4010) is just the burst whose source is the proven 71-packet blob (seeded into the DB by the importer;
the embedded resource was retired). What we do to each burst packet:

- **Instance re-address** (`RewriteBurstInstance`): the burst's captured instance is replaced with the live
  character instance everywhere - as the receiver/inventory varint and the raw entity handle/owner uint32. Both
  are 4 bytes for in-range ids (`0x02000000..0x0FFFFFFF`), so it is length-preserving; the CRC is recomputed.
- **Player spawn GENERATED from DB** (`TryRebuildPlayerSpawnFromDb` + `Spawn/`): the player's own `FA015807`
  is fully generated - position + name, identity (race/sex/level/vitals from `Character`/`CharacterMaxStats`),
  worn equipment (`PlayerSpawnEquipment`, all 17 retail slots mapped incl. Tokens=3 / OffHandWeapon=8 /
  RangedWeapon=20 / Ammo=21), the IIR looks (head mesh / voice / scale = editor values ÷ 2 from
  `CharacterBody`) and the whole scaffold frame (`PlayerSpawnScaffoldCodec`/`Rows`: weighted appearance
  entries from `CharacterBodyResource` × the `MorphTarget` registry, morph-slider entries from
  `CharacterBody`, feat-ID list, pair lists - byte-exact against all 37 captured player spawns). Rotation
  is kept verbatim (DB rotation rebuild stalled the client). See `docs/archive/player-spawn-generation-plan.md`.
- **Inventory (FullChar `0x737A6DF9`) GENERATED from the DB** (`Packets/0x737A6DF9_Test/`): the snapshot's
  protobuf root decodes as the recovered `ProtoFullCharacterIIR` (byte-exact round-trip on 37/39 captured
  snapshots) and its four containers (Equipped 101 / Bag 104 / Resource 112 / Quest 113) are built wholesale
  from the character's items (`FullCharSnapshotBuilder`: `CharacterEquipment` + persisted `CharacterInventory`
  bag rows; deterministic instance+slot entry ids). The tails get the LIVE level / current vitals / XP
  patched in (`BigboyTailsFile`); quest XML + access windows still ride from the capture. An item entry is
  built from a real DB `Item`: templates = `Item.Id`, identity = `Item.Data1..4`, archetype = `Item.Archetype`
  (`SnapshotItemBuilder`, byte-exact vs a captured entry). Items come from the one-time `p_items` base import
  plus `Faolan.Importer` capture harvesting; an item is **buildable iff `Archetype != 0`** (~177 today).
  Beach characters get seeded starter gear (`EnsureBeachStarterEquipment`); equip/unequip persists to
  `CharacterEquipment` + `CharacterInventory` bag rows (`EquipmentPersistence.cs` - unequip parks the item at
  the exact bag cell, multiple unequips coexist), so persisted gear renders in ANY slot on relogin. (The old
  "add/remove stalls" problem was a *fabricated* item identity - real-identity entries add/remove fine.)

GOTCHA - the send loop must be tight. Burst packets go out one raw `client.SendAsync(byte[])` after another;
the GameServer download is uncompressed and the client frames it per TCP read, so **any heavy CPU work between
sends (e.g. parsing/re-serializing the 9 KB inventory mid-loop, or PacketStream's synchronous CRC) reshapes
the segmentation and the client misframes and hangs on the loading screen.** Pre-build every packet (instance
rewrite, spawn rebuild, inventory rebuild) BEFORE the send loop, then send in a pure loop.

This replaces the NPC-less hand-built path for beach logins. The end goal (see `docs/archive/spawn-dissection-plan.md`)
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

The analyzer does **TCP reassembly** (orders by sequence, drops retransmits, buffers gaps) before
inflating - without it, retransmitted/out-of-order segments corrupt the single zlib GameServer download
stream and ~2/3 of the stream is lost.

It also detects GameServer downloads by **content, not port**: any server→client (7000-range) flow is offered
to the `0x80000005` zlib-marker detector, and a confirmed flow is relabeled GameServer
(`PacketCaptureReader.TransformPayload` + `PacketProtocolDetector.MarkGameServerEndpoint`). This matters
because the world GameServer is assigned a fresh port on every **playfield change** (and the same port number
can be a different service on another server IP), so port-based detection mislabeled and dropped those
secondary world streams. **Re-dump any capture made before these fixes** - older JSON dumps are truncated/mislabeled.

Verified against the Wireshark plugin + python-zlib across all 33 GS streams in the captures: 19 byte-identical,
11 where the plugin's C inflater fails partway and the analyzer is more complete, and 3 where some captures have
genuine small 8-byte sequence gaps (lost segments) - the analyzer correctly stops at the gap (the plugin's data
past such a gap is garbage, since zlib desyncs). So the analyzer is at least as complete as the plugin on every
stream, and strictly better on the long ones.

### GameServer download compression

The GameServer download stream is one continuous zlib stream, prefixed by a 9-byte header that starts with
the marker `0x80 00 00 05`. The **decompressed** stream also contains in-band 9-byte `0x80000005` markers
between packet groups (literal stream framing, not packet lengths) - the framer skips them.

### Client launch flags (`AgeOfConan.exe -flag …`)

Recovered by RE (UTF-16 flag table in `AgeOfConan.exe`; the client strips the leading `-` and matches the bare
name). The launcher runs e.g. `AgeOfConan.exe -noconsole -clienthash <md5> -key <hex>`. Full set:

- **Boolean:** `-noconsole` (no debug console), `-no3dlauncher` (skip the 3D launcher), `-novideo` (skip intro
  movies), `-nautotest` / `-openautomate` (automation hooks), `-32bit` (32-bit render path), `-mce` (Media Center).
- **Value:** `-username <name>` and `-password <pass>` (**auto-login**), `-charslot <n>` / `-charname <name>` /
  `-charpf <n>` (auto-enter a character/playfield), `-clienthash <md5>` (client-integrity hash; the only flag with
  its own `"Invalid clienthash arg"` validator), `-key <hex>` (8-byte companion to `-clienthash`),
  `-loginkey <…>`, `-logindata <…>`, `-statindex <n>`, `-processidpath <path>` (launcher PID handshake).
- **No `-server`/`-host`/`-port` flag exists** — the connection endpoint comes from config/registry/the patch, not
  the command line. For testing, `-username`/`-password`/`-charname` skip the login + character screens entirely.

**Launching directly (bypassing ConanPatcher).** Running `AgeOfConan.exe` with no flags bounces to
`ConanPatcher.exe`, because the client requires a valid fresh `-key`. Both gate flags were recovered by RE of
`ConanPatcher.exe`:
- `-clienthash` = **MD5 of `AgeOfConan.exe`** (lowercase hex; the patcher's `cHash.size()==32` check).
- `-key` = **TEA-encrypt(`t‖t`)** where `t` = `time(NULL)` (unix seconds), key
  `[0x5BCE568E, 0xFB2E8CBC, 0xA324E6D8, 0xF22BF865]`, delta `0x9E3779B9`, 32 rounds, printed `%08x%08x`. It's a
  launch-freshness token: decrypting yields `{t, t}` (equal halves) and the time must be recent.
  (`ConanPatcher.exe`: `sub_4bcf93` = `time()`, `sub_439140` = TEA.)

**`Faolan.ClientLauncher`** is a minimal stand-in for the patcher: it computes both flags and starts the client
(`Faolan.Core.Crypt.ConanLaunchToken` is the `-key` generator, reusing the shared `Tea` cipher; verified to
reproduce a captured launcher key). Run `dotnet run --project Faolan.ClientLauncher -- --dry-run` to see the
computed flags, or add `-u/-p/-c` for auto-login. For real password validation, also run
`tools/patch_aoc_login_key.py` against the running client. **Full guide (all flags, the `--universeaddr` server
redirect, the ConanPatcher RE, and the loopback-capture automated-testing workflow): `docs/client-launcher.md`.**

### Client-side logs (ground truth for "why did the client reject it")

The AoC client writes its own log - invaluable when the client silently stalls/disconnects:
`/mnt/btrfs/@games/debian-installation/steamapps/common/Age of Conan/AgeOfConan.log` (current session) and
`.../OldLogs/AgeOfConan<UTC-timestamp>.log` (rotated). Timestamps are `Z`/UTC (local = UTC+2). Look for
`stream bad`, `Dispatcher call ... failed`, `Playfield.Loading`, and `ResourceManager ... does not exist`
(the last are expected - the client is missing most RDB assets). (FIXED 40d4bc0 + de-magicked: the
`DispatchOverrideGameTweaks ... stream bad after 'overrides'` / `Dispatcher call 2085091171 (OverrideGameTweaks)
failed` disconnect is gone - the client now applies all 19 gametweaks; the body is built from the named
named `(name, value)` tweak list in `PlayerAgentListener` (Ox20EC handler) + `GameTweaksTests`.)

### Dissection workbench & tooling (`Captures/*.py`, `Faolan/GameServer/Spawn/`)

- `compare_log_to_dump.py` - decode our `logs.txt` GameServer output and a capture JSON into (conversation
  family, opcode) counts + chronological timeline, instance-independent. The primary diff tool.
- `dissect_spawn.py` - break an `FA015807` spawn into handle/position/rotation/name + a `diff` mode.
- NPC conversation extraction moved into the `Faolan.Importer` tool (`ConversationExtractor`), which writes the
  `ConversationNode`/`ConversationPacket` DB tables (replaced the old `extract_conversations.py`).
- `SpawnPacketBuilder` (+ `SpawnHandleTable`) and the inventory parser are unit-tested in
  `Faolan.PacketAnalyzer.Tests` (byte-exact round-trip is the acceptance gate before anything ships dynamic).

### Wireshark plugin (ground truth)

`Faolan.Wireshark/` builds an EPAN plugin (`faolan_gs_zlib.so`) that fully dissects the Conan protocol:
per-direction packet framing (with the in-band `80 00 00 05` compression transitions), zlib inflation,
RpcHeader endpoints (class ids resolved to the recovered client class names), raw opcodes, CRC
verification, and automatic following of the service-handoff addresses - open a capture and the whole
session decodes from the fixed UniverseAgent port (7010). Filter with `faolan`; per-opcode packet counts
are byte-exact against the analyzer JSON dumps on the reference captures, plus recovery the analyzer
can't do (compressed-stream packets across TCP segment boundaries). See `Faolan.Wireshark/README.md`.

```zsh
# build + install once
cmake -S Faolan.Wireshark -B Faolan.Wireshark/build && cmake --build Faolan.Wireshark/build -j \
  && cmake --build Faolan.Wireshark/build --target install-faolan-plugin

# analyzer-style JSON dump (one record per Conan/chat packet, jq-friendly)
Captures/tshark_dump.sh 'Captures/<capture>.pcapng'   # -> <capture>.tshark.json

# ad-hoc field extraction; the -o pref matters on captures with retransmissions
# (Wireshark hides retransmitted frames from plugins otherwise)
tshark -r '<capture>.pcapng' -o tcp.analyze_sequence_numbers:FALSE \
  -Y 'faolan.pkt.opcode' -T fields -e frame.number -e faolan.pkt.receiver_class -e faolan.pkt.opcode
```

Protocol note discovered while building it: the 9-byte `80 00 00 05` transition header's last byte is a
**compression flag** - `00` switches that direction to one continuous zlib stream, `01` keeps it
plaintext - and the marker appears at packet boundaries (the client sends a flag-`01` one mid-stream on
the GameServer connection). `ConanMessageFramer` only checks receive-chunk starts and assumes
compression, which happens to work for the s2c stream but silently discards the rest of a chunk
carrying the c2s marker (one ping packet per session in the reference captures).

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
- `0x2003` is overloaded BY CLASS (resolved via the method-index mapping): the 24-byte body is
  `GameAgent.Authenticate2` (the connection `Hello`: clientInstance + accountId + version); the 4-byte body is
  `GameCharAgent.SetTutorialStats` (a u32, usually 0 - Tortage tutorial stat tracking, no reply). The handler
  still branches on body length, which is equivalent in practice.
- `0x2000` is overloaded: `GameServer.AckAuthentication` and `UniverseAgent.SendPlayerAgentRealm` are both
  `0x2001`; the analyzer disambiguates by port.
- World build + spawn polling (`0x2020`/`0x2008`/`0x2076`/`0x2052`): see "Existing-character login" above.
  The static world objects must be sent before entity spawns or nothing renders. (`0x2052` is the ability-grant
  method - the player's starting melee attacks; see "Abilities" / `GrantStartingAbilities`.)
- `Captures/compare_log_to_dump.py` decodes our `logs.txt` and a capture JSON into (conversation family,
  opcode) counts + a chronological timeline, independent of the client instance - the primary tool for
  finding which world-entry packets we send wrong or omit. Default dump: the Strie login.

## Client reverse-engineering (opcode / message names)

The protocol's numeric ids were recovered by RE'ing the client `AgeOfConan.exe` (same exe as the `.proto` schema
and DH-login work). The full plan + findings: **`docs/archive/opcode-re-plan.md`**. Key results:

- **The id hash is BKDR-31** (`h = 0; h = h*31 + c` over the ASCII class name, == Java `String.hashCode`).
  Cracked by brute-forcing known anchors (18/18). Two id systems use it:
  - **`ProtoEndpoint.type`** (the conversation family tags) = `hash(interface/agent name)`:
    `GameAgent`/`GameInterface` (player+world), `GameCharAgent`/`GameCharInterface` (the `0x201C` spawn handshake),
    `GameCharAgentProject`/`GameCharInterfaceProject`, plus `Player`/`CSPlayer`/`Universe`/`Tradepost`.
  - **The 32-bit sub-opcodes inside `0x2000`** = `hash(IIR message-type name)`. The whole `0x2000` channel is the
    client's "IIR" (Incremental Information Replication) system; e.g. `0x96B8DC59`=`StatUpdateIIR`,
    `0x6B87E316`=`MagicBuffIIR` (what we call ApplySpell), `0xFA015807`=`SimpleCharFullUpdateIIR` (Spawn),
    `0x5D85BFC7`=`PlayfieldFIIR` (MapChange), `0x642CD3D6`=`BehaviorControlCenterIIR` (the action channel),
    `0xA36D3B74`=`ScryScriptIIR` (cutscene scripts), the `Knubot*IIR` family (NPC dialogue). The full decoded table
    across **all servers** is `docs/aoc-opcode-ids.md`; the named enum is `GameServer0X2000RespondsOpcodes`.
- **RPC method catalog** (`docs/aoc-client-rpc-methods.md`): 14 interfaces, 777 methods, recovered from the
  client's own log strings - method names, **param names in wire order** (from `stream bad after '<field>'`
  traces), and typed client->server signatures, with a Hungarian-prefix -> wire-type legend (`i_c`=ID32 handle,
  `i_n`=int, `b`=bool, `i_v`=vector). This is "what every server<->client method is and how its body is laid out".
- **Embedded protobuf descriptors**: exactly 16 IIR message types are protobuf (`Faolan.Core/Network/Protobuf/
  aoc-client-protos.proto` -> generated `AocProtocol.g.cs`); the rest (combat/quest/morph/move) are raw-stream.
- **16-bit RpcHeader opcodes** (`0x2052`, `0x2008`, ...) are framework-assigned method indices, **not** hashes -
  and they ARE mapped now (cracked 2026-06-12 during the guild work, no Ghidra needed): the opcode is simply
  **`0x2000` + the method's position in the recovered RPC catalog** (`docs/aoc-client-rpc-methods.json`), i.e.
  the client and server agree on the method order of each interface and dispatch by index. How it was verified:
  - **Hypothesis from a known anchor**: in the guild invite-accept capture, the one packet the player sent on
    clicking "accept" was CsPlayerAgent `0x201C` - and `CSPlayerAgent.client_to_server[0x1C]` is
    `AcceptGuildInvitation`. Every other upload in that capture matched its table name's expected behavior too
    (`0x201E`=`ClientRequestGuildInfo` fired per inspected player, `0x202F`=`GuildRequestMemberList` on opening
    the guild window, `0x2040`/`0x2041`=renown history/rankings on the renown tabs, `0x2000`=`Authenticate`).
  - **Bulk confirmation across classes**: a packet's RpcHeader names its RPC class (the BKDR-31 endpoint type),
    so every `(receiver class, opcode)` pair observed across all capture JSONs was tabulated and checked against
    that class's table. Independent semantic hits everywhere: `GameCharAgentProject[0x0B]` =
    `ToggleVanityArmorFilter` (our previously decoded "VanityToggle"), `GameCharAgent[0x1D]` =
    `SplitInventoryItem` (our "SplitStack"), `[0x44]`=`CloseLootBag` (our "CloseLoot"), `[0x6E]`=`AddShortcut`
    (our "MoveItemToBar"), `[0x85]`=`Ping` (our "LatencyCheck"), `TradepostAgent[0x0D/0x0E/0x10]` =
    ItemSearch/BuyItem/Close (the decoded tradepost methods).
  - **Disambiguation rule**: one GameServer connection multiplexes four RPC classes, so the same value can be
    different methods per class - the dispatch key is `(class id, opcode)`. The small tables top out early
    (TradepostAgent `0x2013`, project `0x200C`, GameAgent `0x200A`), so any GameServer opcode `>= 0x2014` is
    unambiguously GameCharAgent.
  - **The drift caveat**: this only holds for **client->server** tables. The **server->client** interface tables
    (CSPlayerInterface, GameInterface, TradepostInterface, PlayerAgent in both directions) have unnamed extra
    methods in the live build, so observed indices drift upward vs the recovered names (e.g. the observed Pong is
    `0x2009`, the table says `0x2003`) - downloads stay named by decoded content, never by table index.

  The complete per-class catalogs now live in `Faolan.Core/Network/Packets/Opcodes.cs` (`GameAgentOpcodes`,
  `GameCharAgentOpcodes` (169), `GameCharAgentProjectOpcodes`, the full 135-method `CsPlayerAgentOpcodes`,
  `TradepostProtocol.AgentMethod`), each annotated with collisions and drift warnings.
- Tooling (reproducible from the exe): `Captures/extract_rpc_catalog.py`, `decode_opcode_ids.py`,
  `reverse_hash_ids.py`, `decode_scryscript.py`, `decode_tradepost.py` (auction-house/mail RPC),
  `decode_guild.py` (the guild flow + the method-index verification).

## Roadmap / current focus

`docs/archive/spawn-dissection-plan.md` + `docs/archive/player-spawn-generation-plan.md` are the living plans for replacing
the verbatim replay with DB-driven world entry. Status: world + NPCs render for any captured map; the player's
own **spawn is fully generated** from DB rows (identity, vitals, equipment, scaffold frame, and the complete
editor **appearance**: morph sliders, chosen resources via the RDB `MorphTarget` registry, head/voice/size);
the **FullChar snapshot's containers are generated** from `CharacterEquipment` + `CharacterInventory` (live
level/vitals/XP patched into the tails); **equip/unequip/inventory moves persist** and render in any slot on
relogin; NPC **dialogue** works (coverage-bound); ~177 items buildable. A **live world simulation** now runs on
top (per-zone model + 10 Hz tick + interest management): NPC death→despawn/XP/level/loot, NPC aggro + fight-back,
player death→respawn at the closest known resurrection point, a quest-state engine with kill-objective tracking,
and cross-player spawn/move/despawn broadcast (see `docs/archive/server-architecture-roadmap.md`). The **auction
house ("Tradepost") + mail** protocol is now decoded byte-exact and the open/search replies are wired +
golden-tested — pinned awaiting an in-client render check (`docs/tradepost-protocol.md`). Remaining: the pinned
**RDB Lua quest/dialogue VM** (branching content), NPC **movement** (MoveTask grammar), the live loot item-grant,
NPCs reconstructed from the DB (vs verbatim burst spawns), and mounts.

## TODO

Concrete next steps, grouped by area. See `docs/` for the dissection writeups behind each.

### Stats & combat (`docs/player-stats.md`, `docs/archive/quests-and-combat.md`)
- [x] **Grant the starting ability kit on login**: the player is sent the universal starting abilities (the 8
      flagged `IsStartingAbility` - 3 Normal Attack directions + Toggle Combat / Switch Weapon / Rest / utility)
      via the retail **`0x2052`** packet, rebuilt DB-driven from the `Abilities` rows (`Slot` + identity quad).
      The kit is harvested from the CREATE-flow captures by `CaptureAbilityImporter`; the table also catalogs the
      higher-level/class abilities (26 total). See "Ability grants" below; enables the cast→damage→vitals test.
- [ ] **Grant actual spells** (vs abilities) - BLOCKED on data: the spell-grant packet is in NO capture (even a
      caster only gets the 8 abilities via 0x2052; `SpellBookIIR` 0x6BA10936 is a stub notification; the RE'd
      `DispatchLearnSpell(i_nSpellID)` is never exercised). Needs a fresh capture of a caster learning a spell
      (e.g. class 29's 165241). The C++ `addSpell` (0x09) is the dead old scheme.
- [x] **Level-up path** (2026-06-11): kill XP (`LevelingResolver`) bumps `Character.Level`, calls
      `RegenerateMaxStats()`, refills + raises live vitals, and pushes `Level`/`Exp`/`MaxExp` + vitals via
      `0x96B8DC59`. Real per-NPC kill XP isn't in the data (Archive `npcs.getExp` only 4 rows), so XP is
      level-scaled (`CombatResolver.ResolveKillXp`).
- [~] **Vitals on login**: server-generated vitals (`BuildVitals`) are now emitted in the world-entry sequence
      after the player spawns in the burst (`RetailLoginReplay.cs:578`), so the bars are populated from the live
      character's `CharacterMaxStats` on login. Remaining (in-client/RE-gated): visual confirmation that the bars
      render correctly (test live with `.vitals` / `.setstat`).
- [ ] Confirm **ApplySpell (`0x6B87E316`) magnitude** sign/scale, and whether damage is carried in the cast or
      only in the following vitals update. (Combat loop is implemented in `Faolan/GameServer/Combat/`:
      `CastSpell` → ApplySpell + damage + `0x96B8DC59` vitals update; damage formula is provisional.)
      **Blocked on a combat capture**: the gameplay captures contain ONLY self-cast buffs (one spell id, `3737143`
      = `0x00390637`, at body offset +13; no target HP change), so there is no clean ApplySpell→vitals damage pair
      to pin sign/scale against. The 80-byte "effect" body carries a `0x0190` (400) value at offset +36 that does
      not move health (buff). Needs a capture where an ApplySpell visibly drops a *target's* HP. The doc's
      `0x2710`/`0x02BC`/`0x03E8` magnitudes are from a different (unavailable) capture.
- [x] **Combat death/XP/loot + NPC fight-back + player death/respawn** (2026-06-11, `docs/archive/server-architecture-roadmap.md`):
      the death/despawn packet (`n3ToClientQuit` 0x04B82B2D) and fade (`n3ToClientFade` 0x66180B20) are decoded;
      NPC death → despawn + XP + level-up + loot-bag drop; NPCs aggro + swing back (vitals hit) on the tick; a
      killed player respawns at the closest known resurrection point. Real **NPC max HP** now comes from the
      captured spawn `CharacterStats.health` (not a placeholder). Remaining gating: the live loot item-grant
      (in-client/RE-gated) and cross-player visual confirmation (2-client test).
- [~] **Mounts**: the summon spell is `0x3BAD7D` (channeled ApplySpell `0x6B87E316`). `.mount` now broadcasts the
      cast **and spawns the mount entity** (the captured "Fading Black Horse" `0xFA015807` NpcSpawn, built byte-exact
      via `NpcSpawnBuilder` at the player's position, `SpawnMountForPlayer`) so the horse appears. Remaining (in-client
      gated): the player actually RIDING — the MountedRun (`MovingType 0x010A`) movement transition is the client's
      part once it binds the summoned mount.
- [x] **Fixed the wrong stat ids in `CapturedCharacterStats.cs`**: the vitals current/max labels were swapped on
      all three. Corrected to `HealthCurrent 0x01` / `HealthMax 0x1B`, `StaminaMax 0x1F8` / `StaminaCurrent 0x1F9`,
      `ManaMax 0x1FA` / `ManaCurrent 0x1FB` — verified across 90 capture records (the `current <= max` invariant
      holds 100%; the larger-valued id of each pair is the max). Matches the `001B`/`01F8`/`01FA` example in
      `docs/archive/quests-and-combat.md`.
- [ ] Determine whether **attributes** (Str/Int/Con/Wis/Dex) are sent at all or computed client-side from
      gear/buffs (their sheet values don't appear as plain stat records).
- [~] The high-volume combat entity-update sub-ops are now **named** (RE, `docs/aoc-opcode-ids.md`):
      `0xFEC4158A`=`DamageResultIIR`, `0xD751EBB4`=`StopIIR` (stop moving), `0x04B82B2D`=`n3ToClientQuitIIR`,
      `0x96C46740`=`MoveTaskIIR`, `0xF98E10B3`=`StatIIR`. Bodies still need raw-stream field decode (not protobuf).

### Conversations / NPC routing
- [x] **Wrong NPC dialogue routing.** Root cause: `(TypeId, Mode, Field3)` is NOT NPC-unique - `mode` is quest-
      state/context dependent (mode 1 is Casilda's create intro but Kalanthes's regular login dialog), so the
      merged DB node served the wrong NPC. Fixed by keying `ConversationNode` on `CapturedNpcHandle` too
      (migration `ConversationKeyByNpcHandle`); lookup prefers the exact clicked-handle node (handles are stable
      since scenes replay verbatim), with an args-only fallback for login and exact-only for create-world.
- [ ] **Casilda's create-world dialogue isn't in the DB.** The dikketest create capture's conversation packets
      aren't recoverable by the analyzer (truncated/compressed - the TCP-reassembly gap; the hand-built
      `SendNpcIntroConversation` came from the Wireshark plugin). So in create-world, clicking Casilda still falls
      through to the scripted Kalanthes intro. To finish: capture/extract Casilda's create-flow conversation
      (Wireshark plugin or a fresh capture) keyed by her handle, then reimport.
- [ ] Decode the NPC spawn's stable conversation/dialogue id (so handle→conversation survives across sessions,
      not just verbatim-replayed scenes).

### Quests (`docs/archive/quests-and-combat.md`, `protocol_quest_state`)
- [x] **Quest state decoded + engine built** (2026-06-11): `QuestIIR` 0x97E02314 (add/update), `QuestGoal`
      0x0774C8C1 (objective progress), `QuestFullUpdate` 0x63268258 / `QuestJournal` 0x79F8C7A1 (questId +
      `remoteformat` text refs - the client renders text from its RDB by id). Built: `CharacterQuest` table +
      `WorldQuest` byte-exact builders + restore-on-login + `.quest add|remove|list` + **kill-objective
      auto-tracking** (a matching NPC kill advances/completes the quest goal).
- [ ] **Quest CONTENT is the pinned RDB Lua quest VM** — accept-from-dialogue (the accept packet carries no
      quest id; the server grants from the quest-giver→quest definition), branching, reward/turn-in
      (`0xA36D3B74` QuestRewards). Needs a Lua interpreter + a player-tag system + TextStrings(TDC2) resolution
      (`protocol_conversation_routing`, `docs/archive/rdb-extraction-plan.md`).

### World entry & rendering (`docs/archive/spawn-dissection-plan.md`, `docs/archive/player-spawn-generation-plan.md`)
- [x] **Appearance** — fully cracked & generated (2026-06-10): morph sliders are zigzag-encoded editor
      values (spawn float = value/1000), the weighted entries are the chosen resources resolved via the
      client RDB customization registry (`MorphTarget`, wire tag = registry id − 1), and the IIR looks are
      the editor's head/voice/size ÷ 2. Bit-exact against every create capture.
- [x] **Add/remove inventory entries** — the FullChar containers are generated wholesale from
      `CharacterEquipment` + `CharacterInventory` rows; unequip parks items at their exact bag cell, equip
      consumes the row; persisted gear renders in any slot on relogin.
- [~] **Full DB snapshot incl. tails** — root containers generated, live level/vitals/XP patched into the
      tails; remaining: from-scratch quest-XML/access-window tails + the `[u32 X][u8 Y]` prefix (X = quest
      counter, best fit — belongs with the quest system).
- [~] **NPCs from the DB** instead of verbatim burst spawns — DB-built bodies are now the DEFAULT on the wire
      (`RetailLoginReplay.cs`, the `wasIdentical` gate removed): every NPC matched to an `NpcSpawns` row renders
      from that row (transform columns + re-encoded CharacterIIR via `NpcSpawnBuilder`), and the verbatim burst
      body is dropped. The burst handle is kept so the spawn stays paired with the burst's `0x96C46740` MoveTask
      registration. The from-scratch construct path (built spawn + built stationary MoveTask, fresh server handle)
      is already proven to render in-client SAME-SESSION (`FAOLAN_BUILT_NPC_TEST=1`, commit a7e6841); the open
      blocker is the CROSS-SESSION row, rejected by the client because the row's captured `Data` carries another
      session's scaffold state. Progress on that:
      - **NPC post-IIR scaffold grammar CRACKED** (workbench `Captures/dissect_npc_spawn.py`): header =
        `[u32 0][u32 firstRecordLen=len-36][u32 recordCount]`; the freshly-spawned form is a CONSTANT 41-byte
        minimal scaffold (the `0x3F1`-marked empty pair lists), encoded + tested as
        `NpcSpawnScaffold.BuildMinimalPostIir()`. Larger blocks concatenate session RUNTIME state (movement
        waypoints / buff timers), which a fresh spawn does NOT reproduce.
      - `NpcSpawnScaffold.WithMinimalPostIir(body)` swaps a body's post-IIR for that minimal form. Wired into the
        harness behind `FAOLAN_MINIMAL_SCAFFOLD=1` — the in-client experiment for the cross-session fix: run
        `FAOLAN_BUILT_NPC_TEST=1 FAOLAN_MINIMAL_SCAFFOLD=1 dotnet Faolan.dll` (from `Faolan/bin/Debug`, config
        `../../../faolan.json`); if the built NPC still renders, the minimal scaffold is client-acceptable.
      Remaining: (1) the **pre-IIR** (~227B) has NO constant form — only 35/227 bytes are stable even across fresh
      NPCs (the rest are per-instance handle/spatial values), so its minimal/stripped form is in-client-gated on the
      experiment above; (2) **server-owned session-handle identity** — allocate fresh handles and remap them
      burst-wide (MoveTask/registration), so the server owns NPC identity instead of inheriting the capture's.

### Auction house ("Tradepost") + mail (`docs/tradepost-protocol.md`) — ⛔ PINNED, awaiting in-client test
The AH is AoC's **Tradepost**, carried on the GameServer RPC layer (not a separate connection). The full wire
protocol is **decoded byte-exact** from the necrol80/assassinl23 captures (`Captures/decode_tradepost.py`); the
open + search replies are **wired and golden-tested**, pending an in-client render check. See `TODO.md` §9a.
- [x] **Protocol decoded** (`docs/tradepost-protocol.md`): the RPC envelope (receiver/sender `ProtoEndpoint`s +
      `0x20xx` method, which COLLIDES across classes so it's routed by class id), the method map (Open `0x2011` /
      ItemSearch `0x200D` / Buy `0x200E` / Close `0x2010` + mail ops; S→C OpenTradepost `0x2000` / SearchResults
      `0x2005` / MailMessage `0x2002`), the search **query** layout, the **result rows**
      (`[head][saleId][handle][seller][price][qty][itemId][ProtoAcgItem]`, sorted by price), and the **buy→mail**
      delivery (`DispatchMailMessage`: recipient = buyer, sender = EMPTY system mail, body = remoteformat
      `TradePostGUI_MailItemBought` with the item attachment). Both rows and the mail attachment reuse the same
      `ProtoAcgItem` our inventory/spawn builders emit.
- [x] **Routing + decode-logging** (`Faolan/GameServer/Tradepost/TradepostHandler`): inbound tradepost RPC is
      routed by class id (`TradepostProtocol.IsInbound`) and each method logged.
- [x] **Open + search replies wired** (`TradepostResponses`, golden-tested vs the captures): `DispatchOpenTradepost`
      (auctioneer access point, triggered by the auctioneer interaction — the `.ah` GM command supplies the
      player's position for now) and a PoC `DispatchTradepostSearchResults` replay so the listing grid renders.
- [ ] **In-client check (pinned):** `.ah` → does the AH window open + show the replayed listing? Then Step 3:
      DB-backed listings (`TradepostListing` + the existing `ProtoAcgItem` builder) and the `Mail`/`MailAttachment`
      model + buy→mail escrow/grant. NPC vendor/shop (`GameCharAgent` `OpenShop`/`Buy`/`Sell`) is queued after.

### Cleanup
- [x] **Fix the malformed `OverrideGameTweaks` packet** — DONE (40d4bc0; de-magicked into the named
      `GameTweaks` catalog + `GameTweaksTests`). Client applies all 19 gametweaks; the `stream bad after
      'overrides'` disconnect is gone.
- [x] **Analyzer "tail-drop" on ~3 GS streams — NOT A BUG (verified).** Re-checked by replaying the
      reassembler in python: those 3 captures (Strie-create/Strie-login `:7029`, faolanx-charcreation `:7018`)
      have genuine small **8-byte sequence gaps** (segments lost during capture, e.g. 7029 missing 8 bytes at
      offsets 28648/31292/32866). The analyzer correctly stops at the first gap rather than feeding
      discontinuous data to the stateful zlib inflater; the plugin inflates *past* the gap, but an 8-byte gap
      desyncs zlib so its extra ~7 KB is garbage (python errors `invalid stored block lengths` exactly at the
      gap). So the analyzer is the more-correct one here too. No fix needed.

## Notes

- The repository still contains hardcoded/verbatim protocol payloads (the `Retail*Burst.cs` create-flow files),
  but the server embeds NO game/spawn data: game data, world bursts and the player-spawn scaffold all live in
  the committed `faolan.db` ((re)built from `Captures/` by `Faolan.Importer`; the player spawn is generated
  from the character's rows - see docs/archive/player-spawn-generation-plan.md). `spawn-player-template.bin` is gone;
  `Captures/strie-login-worldburst.bin` remains only as a Faolan.Importer source file for the beach burst.
- Generating EF migrations needs the design-time factory in `Faolan/Helpers/DatabaseContextDesignTimeFactory.cs`
  (the runtime `ConfigureFaolanDatabase` registration - now a pooled `DbContextFactory` - can't be used by
  `dotnet ef`).
- Build: `dotnet build '/home/melvin/Development/C#/Project Faolan/ProjectFaolan.slnx' -v q`. Tests:
  `dotnet test Faolan.Tests` (the spawn/inventory round-trip tests are the acceptance gate before anything
  ships dynamic), plus `tools/client-smoke-test.sh` for the in-client gate.
