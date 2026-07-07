# Project Faolan

Project Faolan is a server-protocol emulator for the MMORPG *Age of Conan*, written in C# (.NET 10). It
reimplements the game's network protocol — login, character creation, world entry, inventory, combat, chat,
NPCs and the auction house — by mirroring real packet captures byte-for-byte, so a game client can connect
to it and play.

It is a **protocol-research and preservation project**, not a finished game server. Packet captures are the
source of truth: where the emulator and a retail capture disagree, the emulator is wrong. Many subsystems are
decoded and working; others are partially implemented or gated on further reverse-engineering (tracked as
Gitea issues on `melvin/projectfaolan`, grouped by `area:*` labels).

> **Disclaimer.** This is an independent, non-commercial research project for interoperability and game
> preservation. *Age of Conan* is a trademark of Funcom; this project is not affiliated with or endorsed by
> Funcom. No game client, assets, or copyrighted content are distributed here.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/) (the whole solution targets `net10.0`)
- An *Age of Conan* game client, if you want to connect end-to-end (not included)

## Build

```sh
dotnet build ProjectFaolan.slnx
```

## Run the server

The server loads `faolan.json` and `faolan.db` from the current working directory, and both live at the repo
root — so run it **from the repo root** (running from `bin/Debug` fails with "faolan.json not found"):

```sh
dotnet Faolan/bin/Debug/Faolan.dll
```

It starts every protocol service (UniverseAgent on the fixed port **7010**, plus the per-session PlayerAgent,
CsPlayerAgent, AgentServer and GameServer), migrates the database schema, and hosts an in-process MCP endpoint
on `http://127.0.0.1:7099/mcp`.

## Test

The test suite uses byte-exact "golden" round-trips against captured packets as its acceptance gates:

```sh
dotnet test Faolan.Tests
dotnet test Faolan.Tests --filter GuildPacketTests        # a single class
```

## Connect a game client

`Faolan.ClientLauncher` launches the game client unattended (it stands in for the patcher: computes the
launch token, auto-logs-in, and can drive straight to the world):

```sh
dotnet run --project Faolan.ClientLauncher -- --console --universeaddr 127.0.0.1:7010 -u faolan -p any -c <char>
```

See `docs/client-launcher.md` for the full flag set and the loopback-capture testing workflow.

## Rebuild the game database

`faolan.db` (SQLite) is committed to the repo and is the single source of truth for game data; the server only
migrates and reads it. To rebuild its content from the packet captures and client RDB:

```sh
dotnet run --project Faolan.Importer -- --skip characters    # module-based; --list to see modules, --dry-run to preview
```

## Project layout

| Project                  | Purpose                                                                                                                                                                                |
|--------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Faolan/`                | The server host — runs every protocol listener as a hosted service.                                                                                                                    |
| `Faolan.Core/`           | Shared base types, protobuf, crypto, and enums, with the layered `Faolan.Core.Database`, `Faolan.Core.Network`, and `Faolan.Core.Rdb` projects on top.                                 |
| `Faolan.Importer/`       | CLI that (re)builds `faolan.db` from packet captures + the client RDB.                                                                                                                 |
| `Faolan.Mcp/`            | In-process MCP server exposing DB queries + live-session inspection to Claude.                                                                                                         |
| `Faolan.ClientLauncher/` | Launches the game client unattended (patcher stand-in).                                                                                                                                |
| `Faolan.Web.Content/`    | Store-and-forward proxy for the client's content CDN (fixes the loading hang).                                                                                                         |
| `Faolan.Wireshark/`      | A Wireshark/tshark C plugin that fully dissects the Conan protocol (the canonical capture tool).                                                                                       |
| `Faolan.Web/`            | Blazor web app: public character/equipment 3D viewer + armory, plus admin/GM tools (live map radar, GM console, ban, mail, a generic `faolan.db` editor) gated to GameMaster accounts. |
| `Faolan.Tests/`          | The unit/golden test suite.                                                                                                                                                            |
| `Captures/`              | Reference packet traces + Python dissectors used for protocol comparison.                                                                                                              |

## Documentation

- **`CLAUDE.md`** — architecture, build/run commands, the wire-protocol essentials, and working conventions
  (the orientation guide for anyone, human or AI, working in the codebase).
- **Gitea issues** (`melvin/projectfaolan`) — the tracker of open work, grouped by `area:*` labels.
- **`docs/`** — per-subsystem deep-dives (protocol decoding, reverse-engineering methodology, capture workflow);
  `docs/archive/` holds resolved plans and handoffs.
- **`Faolan.Wireshark/README.md`** — building and using the dissector plugin.

## Feature status

Where things stand, by subsystem. This is a research/preservation project, so "works" means *reproduced
byte-for-byte against a retail capture and (usually) confirmed in-client* — not production-hardened. Open work
is tracked as Gitea issues; the `#NNN` refs point there.

**Legend:** ✅ working · 🟡 partial / needs work · ⬜ not started (roadmap)

### Connection & session
- ✅ Login / auth — Diffie–Hellman + TEA-CBC handshake (any password is accepted until the client is key-patched)
- ✅ Service handoff — `UniverseAgent → PlayerAgent → GameServer`
- ✅ Character roster, selection, and existing-character world entry
- ✅ Character creation — the create-editor flow (appearance/morphs, race/sex gating, the Casilda tableau)
- 🟡 Create→world intro cutscene — plays, and Kalanthes speaks (stock-phrase response decoded); some scene
  actors are still verbatim capture bytes (blocked on clean per-type captures, #234 / #185)

### World & simulation
- ✅ World built from DB rows, not replayed — snapshot, player spawn, world objects, NPC interest-streaming
- ✅ NPC AI — aggro/chase, leash-back + heal on disengage, patrol routes
- 🟡 Full simulation fidelity — continuous AI heartbeat / generated (not replayed) behavior (#190, #225);
  client-side movement jitter (#272)
- 🟡 Per-map playfield token — one consistent token works everywhere; the *authentic* per-instance id is
  session-dynamic runtime state and not reproducible (#305)
- 🟡 Playfield instancing — world keyed by `(mapId, instanceId)`; no per-map Solo instance yet (#268)

### Combat
- ✅ Melee attack FSM, weapon draw, damage, vitals; spell-cast FSM; threat / hate list
- ✅ Death window + resurrect/respawn
- ✅ Buff application, damage-over-time, and buff-expiry lifecycle (timers)
- 🟡 Debuff icon-clear at 0 + non-health effect application — built, awaits in-client confirm (#274)
- 🟡 Spell cooldowns — client-side semantics fully RE'd; the server-authored builder is capture-blocked (#271)
- 🟡 Spellweaving (necromancer) — byte-exact builders; energy-bar drop still open (#141)
- 🟡 Combat shields — server-side broadcast exists; the client guard-arc render is not yet decoded (#20)
- ⬜ Directional combat + defensive shield arcs ("real combat", #20) · Fatalities (#140)

### Character progression
- ✅ Learned-spell grant on login; level-up spell / ability / combo grants; starting abilities & combos
- 🟡 Combos persist across relog — fixed, awaits in-client confirm (#299)
- 🟡 XP curve — provisional ramp; real thresholds only partially harvested (#297)
- 🟡 Class spell/combo catalog data — sparse (#296) · abilities panel completeness — mount/travel/class-inherent
  abilities are client-RDB-derived (#303) · a few spellbook entries render nameless (granted variant id, #300)
- ⬜ Feat trees / talent system — the spell↔feat map is server-GDB-only, not in the RDB (#251, blocks #298)
- ⬜ Alternate Advancement / veteran / mastery (#150) · offline training (#143) · apprentice/mentor (#144) ·
  ranger tracking (#145)

### Items & economy
- ✅ Inventory (DB containers, equip, move, starting gear)
- ✅ Vendor shop buy/sell · bank storage · auction house (tradepost) list/buy · mail with money + attachments
- 🟡 Shop-OPEN trigger — decoded flow implemented, awaits in-client confirm (#134)
- 🟡 Max-stat aggregation from gear (a ring's +HP fills the bar) — grammar decoded, one disambiguation left (#3)
- 🟡 Data-driven item-use categorization — currently a per-id map (#207) · guild bank — bodies uncaptured (#132)
- ⬜ Tradeskills: gathering nodes + crafting professions (#148) · worn-only "bag-poison" item rendering (#14)
- ⬜ Gems / item socketing — gem slots that grant attribute bonuses (#311)

### Social & guilds
- ✅ Chat — local say, channels, system, whisper (plus a live "Claude" MCP channel)
- ✅ Guild create, invite/accept, tag, renown
- 🟡 Guild-create no-relog window (`DispatchGuildCreated 0x2044`) (#306) · member kick/promote/demote fan-out (#104)
- ⬜ Friends list + presence (#99, #100) · player↔player trade, quest sharing, team quests (#101)

### Quests & dialogue
- ✅ Quest state model (kill / loot / talk goals), journal, the Casilda Tortage intro chain (kill→loot→free→follow)
- ✅ NPC conversations — Knubot stage machine, routed by NPC name
- 🟡 Escort-arrival completion (#262) · turn-in / reward dialog content (#260) · Lua quest-VM live wiring
- ⬜ Tortage day/night destiny phasing (#149)

### Mounts & pets
- ✅ Necromancer pets/minions — `.pet`, type-based spawn + follow
- 🟡 Mounts — a mount is a player-cast summon spell; needs the cast FSM wired to the summon (#26)
- ⬜ Mounted combat (#142)

### Appearance & rendering (Faolan.Web)
- ✅ 3D character/equipment viewer + armory; RDB mesh / texture / skeleton / animation extraction; morphs & auto-dress
- 🟡 Authentic RDB animations play in the viewer — awaits a visual eyeball (#292) · body atlas gutter dilation
  (#294) · MIXD detail/AO map into the material (#293) · animation mesh-stretch fix (#302)
- 🟡 Client-side render bugs (our wire is correct): create-editor face tattoo (#240), min-height floating hair (#288)
- 🟡 Polished localized appearance names (hair, …) — the LDB id→index map is EXE-gated (#291)
- ⬜ RDB write-back: inline CDN resources + optional texture upscale/replace (#295, parked)

### Group PvE content (not started)
- ⬜ Dungeons & instances — solo and group encounters, epic dungeons, raids (instanced zones + encounter
  scripting; #309)
- ⬜ Factions & reputation — the Khitai / *Rise of the Godslayer* system: faction ranks, faction vendors, and
  faction companions (#310)

### Guild base & large-scale (not started)
- ⬜ Guild base — player-built **guild cities** and **battlekeeps** in the Border Kingdoms: the central keep, walls
  and gates, rank-gated structures, and hired NPC guards (#147)
- ⬜ Sieges — massed guild-vs-guild siege warfare (siege weapons, destructible walls) fought over those keeps (#147)
- ⬜ PvP progression — open-world PvP, PvP minigames, and the PvP-resource / consequence layer that feeds base
  building (#146)

### Tooling
- ✅ Importer (captures + client RDB → `faolan.db`) · Wireshark dissector · unattended ClientLauncher ·
  in-process MCP server · content-CDN mirror
- 🟡 Automated in-client verification harness — `Faolan.ClientHook` injection built; the assertion layer is
  partial (#195)

## Network captures (the project's fuel)

Packet captures — recordings of a client talking to the game servers — are the primary source of truth. The
emulator mirrors that traffic byte-for-byte, so almost every subsystem is decoded from a capture first and only
then rebuilt from structured data: `decode_*.py` dissector → byte-exact golden test → constructed builder. Where
a capture and the emulator disagree, the emulator is wrong.

This is also the **main bottleneck**. A large share of the 🟡/⬜ items above are blocked *not on code but on
lacking a capture* of the specific interaction — the wire format cannot be reproduced without seeing it once.
That is especially true of **server-authored** records (the server writes them, so they can't be recovered by
reverse-engineering the client alone).

**How a capture is used:** a `.pcapng` of a client↔server session is TCP-reassembled and dumped to the canonical
`*.tshark.json` via `Captures/tshark_dump.sh`, which the importer and the Python dissectors read. Loopback
captures against the local server (no root needed) and retail-session captures are both used. Full workflow in
`CLAUDE.md`.

**Captures that would directly unblock currently-blocked features:**
- A **spell cast of a known-cooldown spell** → the `SetSpellRecastTime` GameAction (#271)
- **Guild-bank** deposit/withdraw, and **guild** member kick/promote/demote + the create window (#132, #104, #306)
- A **progressed quest journal** (non-empty objective/progress lists) → the opaque `WTHD` block (#304)
- **Clean per-type** NPC/character captures (not bloated session logins) → retire the verbatim spawn scaffolds
  (#185, and thereby #304 / #234)
- A **necromancer pet summon in the open world** (#307), a **friend** add/remove + come-online (#99), a
  **player↔player trade** (#101)
- **Level-up** captures across more classes/levels → fill the combo/spell/XP tables (#296, #297)
- A **dungeon run**, the **faction/reputation** UI, and an **item socket** action → the roadmap features
  (#309, #310, #311)

Contributing such captures is the single highest-leverage way to move the blocked items.
