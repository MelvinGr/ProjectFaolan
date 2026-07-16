# Project Faolan

Project Faolan is a server-protocol emulator for the MMORPG _Age of Conan_, written in C# (.NET 10). It
reimplements the game's network protocol — login, character creation, world entry, inventory, combat, chat,
NPCs and the auction house — by mirroring real packet captures byte-for-byte, so a game client can connect
to it and play.

It is a **protocol-research and preservation project**, not a finished game server. Packet captures are the
source of truth: where the emulator and a retail capture disagree, the emulator is wrong. Many subsystems are
decoded and working; others are partially implemented or gated on further reverse-engineering.

> **Disclaimer.** This is an independent, non-commercial research project for interoperability and game
> preservation. _Age of Conan_ is a trademark of Funcom; this project is not affiliated with or endorsed by
> Funcom. No game client, assets, or copyrighted content are distributed here.

## Feature status

Where things stand, by subsystem. This is a research/preservation project, so "works" means _reproduced
byte-for-byte against a retail capture and (usually) confirmed in-client_ — not production-hardened. Open work
is tracked as Gitea issues; the `#NNN` refs point there.

**Legend:** ✅ working · 🟡 partial / needs work · ⬜ not started (roadmap)

<!-- stats:start -->
**Imported content** (the DB is the server's source of truth): 520 maps · 586 NPC types / 1,086 placements · 3,188 items · 20,822 spells · 27 abilities · 242 conversation nodes · 29 shops · 28 quests · 1,053 world objects · 139 patrol routes
<!-- stats:end -->

**Content coverage** — of the NPCs/vendors we've actually interacted with in our capture dumps, how many are
imported (capture-relative: retail has no public dialogue/vendor list, so this measures completeness _of what
we've seen_, and names the gaps). Regenerate with
`dotnet run --project Faolan.Importer -- --report coverage --report-arg readme`:

<!-- coverage:start -->
- **NPC conversations** — 34/37 greeted dialogue NPCs imported (91.9%)
- **Vendor shops** — 21/21 captured vendors imported (100.0%)
- **RDB template catalog** (type 1000020, TEA-encrypted) — 3,178/77,631 decrypted, **4.1% of the total encrypted**. Keys held per category: 3,151 items, 27 abilities, 0 feats (feat keys absent from captures). (+20,417 plaintext catalog entries, no key needed.)
<!-- coverage:end -->

### Connection & session

- ✅ Login / auth — Diffie–Hellman + TEA-CBC handshake (any password is accepted until the client is key-patched)
- ✅ Character roster, selection, and existing-character world entry
- ✅ Character creation — the create-editor flow (appearance/morphs, race/sex gating, the Casilda tableau)
- 🟡 Intermittent wrong appearance on a new character (hair/mustache/size) — late appearance edits were not
  persisted; fixed, awaits in-client confirm (#314)
- 🟡 Create→world intro cutscene — plays, and Kalanthes speaks (stock-phrase response decoded); some scene
  actors are still verbatim capture bytes (blocked on clean per-type captures, #234)

### Realms & deployment

- 🟡 Multiple realms (game servers) — **process-per-realm**: `dotnet run --project Faolan` is all-in-one (dev),
  `--role login` runs the shared login/router tier, and `--role realm=<id>` runs one realm's isolated world (its own
  GameServer + chat + guilds, bound to that `Realm` row's `IpAddress`/ports). Guilds, auction house, mail, and
  character names are all `RealmId`-scoped; the client is routed to its selected character's realm. Server-side
  complete and fleet-verified (1 login + N realm processes on a shared DB); awaits an in-client 2-realm shakedown (#324)

### World & simulation

- ✅ World built from DB rows, not replayed — snapshot, player spawn, world objects, NPC interest-streaming
- ✅ NPC AI — aggro/chase, leash-back + heal on disengage, patrol routes; faction disposition (friendly/neutral
  NPCs never aggro and render green names, hostiles red)
- 🟡 Open-world ambient spawn layout — the authoritative per-playfield spawn groups (roam area + population
  count) decoded from the client RDB (type 1010506) into `NpcSpawnArea` (2,664 groups across 119 playfields).
  The creature TYPE isn't in the client (it's server-authoritative), so it's inferred from captures (~21% of
  groups: name / in-bounds vote / guarded id-propagation) then confirmed by hand — a dev nameplate label
  (`.togglenpcgroup`, shows `group | tier xcount` over each NPC via a SetNameIIR override) + the `.setnpcgroup` GM command assign
  the rest. A live **area-managed ambient spawner** now consumes these pools — per-pool respawn/refill timers
  keep each group at its population count, and members roam their area between spawn anchors (preferring a free
  anchor so they stop stacking); disabled by default until a group's creature type is confirmed (#332 done, #363)
- 🟡 NPC line-of-sight — NPCs don't aggro over a hill / across a drop / through a building wall, and loot bags +
  corpses snap to the ground, driven by a baked per-map terrain file (heightmap + PhysX collision mesh; the game
  server never touches the client RDB at runtime). The ground-snap is tolerance-gated: the heightmap is the OUTDOOR
  surface only, so an interior/cave kill keeps its death Y instead of flinging the bag up to open sky (#369). Aggro
  gating awaits in-client confirm (#284)
- ✅ Interactive world objects — doors/gates open on use; interacting with a prop with no behavior floats a
  "nothing to interact with here yet" bubble instead of a silent no-op
- ✅ Zone-line / teleport doors — using a door that changes location teleports the player (same-playfield and
  cross-playfield, in-client verified), destinations auto-imported from captured UseObject→teleport sequences;
  the ~52 un-harvested zone-lines still fall through to the open animation (#341) and the GameAction type ids are
  placeholders (#338)
- 🟡 Area-name banners — entering a map floats the on-screen area title (`GenericDisplayAreaName` via
  `FormatFeedbackIIR`), harvested from captures into `Map.AreaNameId` (4 maps); awaits in-client confirm. Interim
  shape: retail fires a banner per **sub-region** crossing and a playfield has many, so the single per-map column
  can't represent it (Old Tarantia alone has a cluster of 4 captured ids) — the real model is RDB region trigger
  volumes, still un-extracted (#348)
- 🟡 Sit on benches — the sit/stand/hold-state animations are decoded + built byte-exact, but the pose reverts
  to standing after the animation (does not yet hold in-client) (#355)
- 🟡 Full simulation fidelity — continuous AI heartbeat / generated (not replayed) behavior (#190, #225);
  client-side movement jitter (#272)
- ✅ Per-map playfield token — sourced per-map from `Map.PlayfieldToken` (seeded with a captured token per map;
  the tokens are session-minted instance ids pooled by region, so the seed is a representative value) (#305)
- ✅ Playfield instancing — world keyed by `(mapId, instanceId)`; a Solo map (e.g. the beach) gives each player
  their own instance (verified in-client with two clients) (#268)

### Combat

- ✅ Melee & spell-cast FSM, weapon draw, damage, vitals; threat / hate list
- ✅ Floating damage numbers + combat-log lines for player→NPC hits — melee via `DamageResultIIR`, spell via
  `StatDamageIIR` (retail's two distinct damage packets)
- ✅ Moving cancels an in-progress cast — the client's cancel is answered with retail's state-`0x0007` abort BCC,
  which drops the cast bar + plays the "Interrupted" text (#347)
- ✅ Death window + resurrect/respawn
- ✅ Buff application, damage-over-time, buff-expiry lifecycle (timers clear the icon at 0), and right-click cancel
  (server-driven icon removal). Self-buffs and hostile debuffs both verified in-client (#274, #370)
- ✅ Shield of the Risen — the top-of-screen protection buff, re-applied on login and every map change; dispels on
  the first aggressive action with the `LostGraceEffect` toast (#361)
- 🟡 Ground-targeted AoE (Deathly Blizzard) — ticks damage over its channel with floating numbers; the per-spell
  blast radius + tick cadence are still interim constants (not in the RDB spell record) (#349)
- 🟡 Buff stat effects — buffs are icon + timer only; nothing applies a stat delta. Damage-over-time is the one live
  effect (`Spells.DotDelta`, 34 spells); the other ~11,600 duration-bearing spells render an icon and do nothing.
  `Spells` has no stat-delta columns, so this needs an RDB decode + schema + apply/revert engine (#367)
- 🟡 Spell cooldowns — fully RE'd (68-type GameAction catalog recovered from a live client). **Base cooldowns are
  client-side**: retail sends nothing on a normal cast, the client reads the recast from its own RDB when the spell
  is granted — so we deliberately emit nothing either. `SpellRecastActionBuilder` is byte-exact and construct-only,
  for the server-OVERRIDE case (cooldown resets / AA reductions). Awaits an in-client check that the client runs the
  sweep unaided (#271)
- 🟡 Spellweaving (necromancer) — byte-exact builders; energy-bar drop still open (#141)
- 🟡 Combat shields — server-side broadcast exists; the client guard-arc render is not yet decoded (#20)
- ✅ NPC death animations — the death-pose MoveTask + death-behavior BCC play on kill (no more disappear /
  teleport-to-spawn / attack-while-dead for patrolling NPCs). The per-creature death anim is carried on
  `NpcTemplate.DeathBehaviorId` and verified in-client for humanoid (41877) and beast (215333); 43 types harvested,
  the remaining 543 fall back to the humanoid anim until harvested (#362)
- ⬜ Directional combat + defensive shield arcs ("real combat", #20) · Fatalities (#140)

### Character progression

- ✅ Learned-spell grant on login; level-up spell / ability / combo grants; starting abilities & combos. Every rank
  is learned and visible (retail's snapshot known-list carries R1..Rn); collapsing a family to its top rank is a
  client-side UI option, not something the server does
- ✅ Combos persist across relog — re-emitted on every world entry, not just on level-up (#299)
- 🟡 XP curve — provisional ramp; real thresholds only partially harvested (#297)
- 🟡 Class spell/combo catalog data — sparse (#296) · abilities panel completeness — mount/travel/class-inherent
  abilities are client-RDB-derived (#303) · a few spellbook entries render nameless — legit `0x200A` grants whose
  names live in the LDB string index, so blocked on #291 (#300)
- 🟡 Feat trees / talent system — feat **training + reset are wired to the live protocol**: training adds feats
  (`FeatIIR` 0x28DA68A0 state 4 → byte-exact ack) and reset/untrain clears them (`UntrainFeat` 0x2041 → world
  reload), driveable via the `.feat` GM command (list / train / reset). Still open: the tree structure, feat
  points + prerequisites, and the per-class tree definitions — the RDB feat catalog (type 1000020) is ~21% decoded
  plaintext (tree + rank + name), the rest TEA-encrypted, and the spell↔feat map level-gated casting needs is
  server-GDB-only, not in the RDB (#251, blocks #298)
- ⬜ Alternate Advancement / veteran / mastery (#150) · offline training (#143) · apprentice/mentor (#144)

### Items & economy

- ✅ Inventory (DB containers, equip, move, starting gear)
- ✅ Vendor shop buy/sell · bank storage · auction house (tradepost) list/buy · mail with money + attachments
  (taking an attachment now clears it: `Mail`'s primary key is the wire `MailId` itself, so the attachment FK can no
  longer mismatch the id every caller passes — the old surrogate row key silently deleted nothing and duplicated the
  item on relog, #368)
- ✅ Item claims / Redeem window (`CoreClaimIIR`) — the per-account claim list + redeem (Claim / Claim All),
  infinite vs. multi-charge (Double XP) claims, delivered to the backpack; item keys harvested from the claim
  list, rendered by key on relog (#359)
- ✅ Consumable use — right-click a potion/food/drink to apply its on-use buff + consume one, data-driven from
  `Items.OnUseSpell` (harvested from captures), no-op if the buff is already active
- ✅ Creature loot drops — a killed NPC drops a lootable container, its variant chosen per-type from
  `NpcTemplate.LootContainerTemplateId` (a bag by default, or a Large Chest — both ship as the same
  `LootBagFullUpdateIIR`, differing only by model / overhead name / bounds; the raid-chest variant awaits a
  captured template) (#363)
- ✅ Shop-OPEN trigger — interacting with a vendor opens its shop window
- 🟡 Max-stat aggregation from gear (a ring's +HP fills the bar) — item attribute modifiers imported + summed
  (`ItemStat` + `MaxStatAggregator`); the vital/HP field + wire scale still need a reference tooltip (#3)
- 🟡 Guild bank — bodies uncaptured (#132)
- ⬜ Tradeskills: gathering nodes + crafting professions (#148)
- ⬜ Gems / item socketing — gem slots that grant attribute bonuses (#311)

### Social & guilds

- ✅ Chat — local say, channels, system, whisper (plus a live "Claude" MCP channel)
- ✅ Guild create, invite/accept, tag, renown
- ✅ Guild-create no-relog window (`DispatchGuildCreated 0x2044`) — the guild window + tag appear without a relog (#306)
- 🟡 Member kick/promote/demote fan-out — bodies uncaptured (#104)
- ⬜ Friends list + presence (#99, #100) · player↔player trade, quest sharing, team quests (#101)

### Quests & dialogue

- ✅ Quest state model (kill / loot / talk goals), journal, the Casilda Tortage intro chain (kill→loot→free→follow)
- ✅ NPC conversations — Knubot stage machine, routed by NPC name
- 🟡 Escort-arrival completion (#262) · turn-in / reward dialog content (#260) · Lua quest-VM live wiring
- ⬜ Tortage day/night destiny phasing (#149)

### Mounts & pets

- ✅ Necromancer pets/minions — `.pet`, type-based spawn + follow
- 🟡 Mounts — Work but need more dumps to import
- ⬜ Mounted combat (#142)

### Group PvE content (not started)

- ⬜ Dungeons & instances — solo and group encounters, epic dungeons, raids (instanced zones + encounter
  scripting; #309)
- ⬜ Factions & reputation — the Khitai / _Rise of the Godslayer_ system: faction ranks, faction vendors, and
  faction companions (#310). The client's **Factions window already populates** for imported characters: the
  per-faction standings are stored + sent, riding the player-spawn scaffold's first ending pair list (faction id →
  signed standing) (#315). What's missing is the semantic layer — a faction catalog and standing _changes_ from
  quests/kills (#310)

### Guild base & large-scale (not started)

- ⬜ Guild base — player-built **guild cities** and **battlekeeps** in the Border Kingdoms: the central keep, walls
  and gates, rank-gated structures, and hired NPC guards (#147)
- ⬜ Sieges — massed guild-vs-guild siege warfare (siege weapons, destructible walls) fought over those keeps (#147)
- ⬜ PvP progression — open-world PvP, PvP minigames, and the PvP-resource / consequence layer that feeds base
  building (#146)

### Tooling

- 🟡 Importer (captures + client RDB → `faolan.db`)
- ✅ Wireshark dissector
- ✅ Client Launcher ·
- ✅ In-process MCP server
- ✅ Content-CDN mirror

## Build

```sh
dotnet build ProjectFaolan.slnx
```

## Run the server

The server loads `faolan.json` and `faolan.db` from the current working directory, and both live at the repo
root — so run it **from the repo root** (running from `bin/Debug` fails with "faolan.json not found"):

```sh
dotnet run --project Faolan
```

It starts every protocol service (UniverseAgent on the fixed port **7010**, plus the PlayerAgent,
CsPlayerAgent, ChatServer and GameServer listeners the client is handed off to in turn), migrates the
database schema, and hosts an in-process MCP endpoint on `http://127.0.0.1:7099/mcp`.

For a **multi-realm** deployment (#324), split the process by role — one login/router tier and one game host
per realm (each bound to its `Realm` row's `IpAddress`/ports), all against the shared DB:

```sh
dotnet run --project Faolan -- --role login          # the shared login/router tier (UniverseAgent + PlayerAgent)
dotnet run --project Faolan -- --role realm=2        # one realm's game host (GameServer + realm-local chat + guilds)
dotnet run --project Faolan -- --role realm=3        # another realm, on that Realm row's ports
```

`--role` is repeatable, so a single process can compose roles — e.g. `--role login --role realm=2` runs the
login tier and realm 2 together. At most one `realm` role per process (co-hosting realms would share chat/session
state across them); run one process per realm for isolation.

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

See `docs/tooling/client-launcher.md` for the full flag set and the loopback-capture testing workflow.

## Rebuild the game database

`faolan.db` (SQLite) is committed to the repo and is the single source of truth for game data; the server only
migrates and reads it. To rebuild its content from the packet captures and client RDB:

```sh
dotnet run --project Faolan.Importer -- --skip characters    # module-based; --list to see modules, --dry-run to preview
```

## World terrain files (line-of-sight)

NPC line-of-sight + ground-snap (#284) read a baked per-map terrain file from `Terrain/` — the game server never
touches the client RDB at runtime. These files are large (~555 MB) and **not committed**, so generate them once
from the client RDB (they're optional: without them the server still runs and every terrain query degrades open):

```sh
dotnet run --project Faolan.Importer -- --report terrain-export   # writes Terrain/<mapId>.terrain (one per map)
```

## Project layout

| Project                  | Purpose                                                                                                                                                                                |
| ------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
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
- **[`docs/protocol/`](docs/protocol/README.md)** — the verified wire-protocol reference: framing, opcodes,
  message layouts and encodings, where every stated fact carries a verification tag a test resolves and the
  machine-derivable pages are generated from the code. Start at its `README.md`.
- **`docs/`** — the rest of the deep-dives, by kind: `subsystems/` (how the server implements each domain),
  `rdb/` (client asset formats), `tooling/` (launcher, capture workflow, importer), `re/` (reverse-engineering
  methodology + raw decode artifacts).
- **`Faolan.Wireshark/README.md`** — building and using the dissector plugin.

## Network captures (the project's fuel)

Packet captures — recordings of a client talking to the game servers — are the primary source of truth. The
emulator mirrors that traffic byte-for-byte, so almost every subsystem is decoded from a capture first and only
then rebuilt from structured data: `decode_*.py` dissector → byte-exact golden test → constructed builder. Where
a capture and the emulator disagree, the emulator is wrong.

This is also the **main bottleneck**. A large share of the 🟡/⬜ items above are blocked _not on code but on
lacking a capture_ of the specific interaction — the wire format cannot be reproduced without seeing it once.
That is especially true of **server-authored** records (the server writes them, so they can't be recovered by
reverse-engineering the client alone).

**How a capture is used:** a `.pcapng` of a client↔server session is TCP-reassembled and dumped to the canonical
`*.tshark.json` via `Captures/scripts/tshark_dump.sh`, which the importer and the Python dissectors read. Loopback
captures against the local server (no root needed) and retail-session captures are both used. Full workflow in
`CLAUDE.md`.

**Captures that would directly unblock currently-blocked features:**

- A **spell cast of a known-cooldown spell** → the `SetSpellRecastTime` GameAction (#271)
- **Guild-bank** deposit/withdraw, and **guild** member kick/promote/demote (#132, #104)
- A **progressed quest journal** (non-empty objective/progress lists) → the opaque `WTHD` block (#304)
- **Clean per-type** NPC/character captures (not bloated session logins) → retire the verbatim spawn scaffolds
  (#185, and thereby #304 / #234)
- A **necromancer pet summon in the open world** (#307), a **friend** add/remove + come-online (#99), a
  **player↔player trade** (#101)
- **Level-up** captures across more classes/levels → fill the combo/spell/XP tables (#296, #297)
- A **dungeon run**, the **faction/reputation** UI, and an **item socket** action → the roadmap features
  (#309, #310, #311)

Contributing such captures is the single highest-leverage way to move the blocked items.
