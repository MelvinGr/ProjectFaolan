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

What you can actually do, by subsystem. This is a research/preservation project, so "works" means _reproduced
byte-for-byte against a retail capture and (usually) confirmed in a real game client_ — not production-hardened.
Open work is tracked as Gitea issues; the `#NNN` refs point to the relevant issue.

**Legend:** ✅ working · 🟡 partial / needs work · ⬜ not started (roadmap)

<!-- stats:start -->
**Imported content** (the DB is the server's source of truth): 530 maps · 586 NPC types / 1,086 placements · 3,245 items · 20,822 spells · 27 abilities · 242 conversation nodes · 29 shops · 2,036 quests · 1,053 world objects · 139 patrol routes
<!-- stats:end -->

**Content coverage** — of the NPCs/vendors we've actually interacted with in our capture dumps, how many are
imported (capture-relative: retail has no public dialogue/vendor list, so this measures completeness _of what
we've seen_, and names the gaps). Regenerate with
`dotnet run --project Faolan.Importer -- --report coverage --report-arg readme`:

<!-- coverage:start -->
- **NPC conversations** — 34/37 greeted dialogue NPCs imported (91.9%)
- **Vendor shops** — 21/21 captured vendors imported (100.0%)
- **RDB template catalog** (type 1000020, TEA-encrypted) — 3,234/77,631 decrypted, **4.2% of the total encrypted**. Keys held per category: 3,207 items, 27 abilities, 0 feats (feat keys absent from captures). (+20,417 plaintext catalog entries, no key needed.)
<!-- coverage:end -->

### Connection & session

- ✅ **Log in and play** — the full login handshake (encrypted; any password is accepted until the client is
  key-patched), character roster and selection, and world entry with your existing characters
- ✅ **Create a character** — the appearance editor (face morphs, race/sex options) and the Tortage opening tableau
- 🟡 New characters occasionally import the wrong look (hair/mustache/size) — fixed, pending an in-client confirm (#314)
- 🟡 The create→world intro cutscene plays and Kalanthes speaks; a few background actors still render from raw
  capture data (#234)

### Realms & deployment

- 🟡 **Run multiple realms** — one shared login tier fronts any number of independent realm servers, each with its
  own world, chat, and guilds, all on a shared database; guilds, the auction house, mail, and character names are
  realm-scoped, and you're routed to your selected character's realm. Server-side complete and fleet-tested;
  pending a live two-realm shakedown (#324)

### World & simulation

- ✅ **A living world, built from data** — the world, its objects, and its NPCs are all constructed from the
  database and streamed to you as you move, never replayed from a recording
- ✅ **NPC AI** — enemies aggro and give chase, leash back and heal when you break away, and walk patrol routes;
  friendly and neutral NPCs never attack and show green names, hostiles red
- ✅ **Interactive objects** — doors and gates open when used; anything not yet interactive tells you so instead of
  silently doing nothing
- ✅ **Zone-line doors** — a doorway that changes your location teleports you, both within a zone and across a
  loading screen, with destinations learned from captures (a handful of rarer ones still just play the open
  animation, #341)
- ✅ **Area-name banners** — crossing into a named district floats its title on screen (Old Tarantia alone has
  six); coverage grows as more of the map is captured (#377)
- ✅ **Instanced zones** — solo areas like the Tortage beach give each player their own copy, verified with two
  clients side by side
- 🟡 **Ambient wildlife & spawns** — each zone's spawn groups (roam area + population) are decoded from the client,
  and a live spawner keeps every group stocked, roaming, and refilling as you hunt them. On by default; because the
  creature type per group is server-only, ~21% are auto-matched from captures and the rest are hand-confirmed with
  in-game tools (#332, #363)
- 🟡 **Line-of-sight & terrain** — NPCs won't aggro over a hill or through a wall, and loot and corpses settle to
  the ground, driven by baked per-map terrain; the aggro gating is pending an in-client confirm (#284)
- 🟡 **Sit on benches** — the sit animation plays; making the pose hold is wired and pending an in-client confirm (#355)
- 🟡 **Fuller simulation** — a continuous AI heartbeat and smoother movement are in progress (#190, #225, #272)

### Combat

- ✅ **Melee & spellcasting** — the full attack/cast cycle: weapon draw, cast bar, hits, damage, and vitals, with a
  threat/hate list driving who NPCs go after
- ✅ **Floating damage numbers & combat log** for your hits, matching retail's distinct melee and spell number styles
- ✅ **Interrupts** — moving mid-cast cancels the spell and shows the "Interrupted" message, exactly like retail (#347)
- ✅ **Death & resurrection** — the release screen lists the resurrection points you've discovered, nearest first,
  and each revives you at its own strength (10/50/100%) rather than always full (#372, #375)
- ✅ **Discovering resurrection points** — walking near a new point pops retail's "point discovered" banner and adds
  it to your list in one frame; works for all **538** imported points, verified in a real client (#372)
- ✅ **Buffs & debuffs** — apply, tick (damage-over-time), expire on a timer, and right-click to cancel; self-buffs
  and enemy debuffs both verified in-client (#274, #370)
- ✅ **Shield of the Risen** — the newcomer protection buff, re-applied on login and each zone, dropping the moment
  you act aggressively (#361)
- ✅ **NPC death animations** — enemies play a proper death on the killing blow instead of vanishing or teleporting
  back to spawn; verified in-client per creature type, with more being harvested (#362)
- 🟡 **Ground-targeted AoE** (e.g. Deathly Blizzard) — ticks damage with floating numbers; the per-spell radius and
  cadence are still approximate (#349)
- 🟡 **Buff stat effects** — buffs show as icon + timer today; applying their actual stat bonuses needs a data
  decode still in progress (damage-over-time already works) (#367)
- 🟡 **Spell cooldowns** — fully reverse-engineered; the client runs the recast timer from its own data (retail
  sends nothing), pending an in-client confirm of the server-override case (#271)
- 🟡 **Spellweaving** (necromancer) — built and playable; the energy-bar drain is the last piece (#141)
- 🟡 **Combat shields** — the guard-arc render is not yet decoded (#20)
- ⬜ **Real directional combat** with defensive shield arcs (#20) · **combos** — the client already drives the whole
  combo chain; the finisher's bonus damage needs server-side data not present in the client (#139) · **fatalities**
  — the finishing-move animations are catalogued, but their trigger needs a capture we don't yet have (#140)

### Character progression

- ✅ **Learn spells, abilities & combos** — granted on login and at each level-up, with every rank learned and
  shown (collapsing a family to its top rank is a client-side UI choice)
- ✅ Combos stick across relogs, not just at level-up
- 🟡 **Feat trees** — training, resetting, the untrain confirmation, and the feat-point budget all work against the
  live protocol (driven by a GM command for now); what's missing is the feat *catalog* itself — which feats exist,
  their tiers and prerequisites, per-feat costs, and each class's tree layout (#251)
- 🟡 XP curve is a provisional ramp; the real thresholds are partly harvested (#297)
- 🟡 Class spell / combo / ability catalogs are still filling in, and a few spellbook entries render nameless
  (#296, #303, #300)
- ⬜ Alternate Advancement / veteran / mastery (#150) · offline training (#143) · apprentice/mentor (#144)

### Items & economy

- ✅ **Inventory** — containers, equipping, moving items, and starting gear
- ✅ **Item quality** — every item's rarity from Mundane to Legendary, so tooltips and name colours match retail (#397)
- ✅ **The full economy** — buy and sell at vendors (the shop window opens on use), bank storage, the auction house
  (list and buy), and mail carrying money and item attachments
- ✅ **Item claims** — the account claim/redeem window (Claim, Claim All), including infinite and multi-charge
  claims like Double XP, delivered straight to your bag (#359)
- ✅ **Consumables** — right-click a potion, food, or drink to use it and apply its effect
- ✅ **Creature loot** — killed enemies drop a lootable bag or chest, chosen per creature type (#363)
- 🟡 **Gear stat bonuses** — a ring's +HP filling your bar: modifiers are imported and summed; the final wire scale
  needs a reference tooltip (#3)
- 🟡 Guild bank (#132)
- ⬜ Tradeskills — gathering and crafting professions (#148) · gem socketing for attribute bonuses (#311)

### Social & guilds

- ✅ **Chat** — say, channels, system messages, and whispers
- ✅ **Guilds** — create, invite and accept, guild tag, and renown, with the guild window appearing instantly on
  create (no relog) (#306)
- 🟡 Member kick / promote / demote (#104)
- ⬜ Friends list & presence (#99, #100) · player-to-player trade, quest sharing, team quests (#101)

### Quests & dialogue

- ✅ **Quests** — kill / loot / talk objectives, the journal, and the Casilda Tortage intro chain end to end
  (kill → loot → free → follow)
- ✅ **NPC conversations** — branching dialogue driven by the client's dialogue engine
- 🟡 Escort completion (#262) · turn-in and reward dialogs (#260) · Lua quest scripting
- ⬜ Tortage day/night destiny phasing (#149)

### Mounts & pets

- ✅ **Necromancer pets** — summon minions by type and have them follow
- 🟡 **Mounts** — working; importing more from captures
- ⬜ Mounted combat (#142)

### Group PvE content (not started)

- ⬜ Dungeons & instances — solo and group encounters, epic dungeons, raids (instanced zones + encounter
  scripting; #309)
- ⬜ Factions & reputation — the Khitai / _Rise of the Godslayer_ system: faction ranks, faction vendors, and
  faction companions. The in-game Factions window already populates with standings for imported characters; what's
  missing is the catalog and the reputation _changes_ from quests and kills (#310, #315)

### Guild base & large-scale (not started)

- ⬜ Guild base — player-built **guild cities** and **battlekeeps** in the Border Kingdoms: the central keep, walls
  and gates, rank-gated structures, and hired NPC guards (#147)
- ⬜ Sieges — massed guild-vs-guild siege warfare (siege weapons, destructible walls) fought over those keeps (#147)
- ⬜ PvP progression — open-world PvP, PvP minigames, and the PvP-resource / consequence layer that feeds base
  building (#146)

### Tooling

- ✅ **Wireshark dissector** — full Conan-protocol decoding for capture analysis
- ✅ **Client launcher** — drives the game client unattended, straight into the world
- ✅ **Content-CDN mirror** — serves the client's streamed assets so it loads cleanly offline
- ✅ **In-process MCP server** — live database + session inspection for tooling
- ✅ **Web armory & 3D viewer** — browser character/equipment viewer plus GM tools
- 🟡 **Importer** — rebuilds the game database from captures + the client's asset files

## Build

```sh
dotnet build ProjectFaolan.slnx
```

If you intend to commit changes to `faolan.db`, enable the repo's hooks **once per clone** (git does not share
them, and without this the `faolan.sql` mirror below silently goes stale):

```sh
git config core.hooksPath .githooks
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

### `faolan.db` and `faolan.sql`

The game data lives in **`faolan.db`** (SQLite, built by the importer, committed). Alongside it, **`faolan.sql`**
is a plain-text `.dump` of the same database, regenerated automatically by the `.githooks/pre-commit` hook
whenever `faolan.db` is committed — so a data change is **reviewable in a diff** instead of showing up as
`Bin 8839168 -> 8839168 bytes`. It is generated: never hand-edit it, and don't bother regenerating it yourself
(the hook dumps the *staged* blob, so it always matches the commit).

Both are deliberately interim. The end state (#379) is a real database with the schema + seed data kept as SQL in
the repo and the binary dropped — running the two in parallel now means that migration is a deletion rather than
a port.

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
`*.tshark.json` via `tools/capture/tshark_dump.sh`, which the importer and the Python dissectors read. Loopback
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
- A **combo landing on a live target** and a **melee-class fatality** on a killing blow → the combo-finisher bonus
  damage and the fatality trigger wire (both server-authored, so un-recoverable from the client alone; #139, #140)
- **Level-up** captures across more classes/levels → fill the combo/spell/XP tables (#296, #297)
- A **dungeon run**, the **faction/reputation** UI, and an **item socket** action → the roadmap features
  (#309, #310, #311)

Contributing such captures is the single highest-leverage way to move the blocked items.
