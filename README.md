# Project Faolan

Project Faolan is a server-protocol emulator for the MMORPG _Age of Conan_, written in C# (.NET 10). It
reimplements the game's network protocol — login, character creation, world entry, inventory, combat, chat,
NPCs and the auction house — by mirroring real packet captures byte-for-byte, so a game client can connect
to it and play.

It is a **protocol-research and preservation project**, not a finished game server. Packet captures are the
source of truth: where the emulator and a retail capture disagree, the emulator is wrong. Many subsystems are
decoded and working; others are partially implemented or gated on further reverse-engineering.

> **Disclaimer.** This is an independent, non-commercial research project for interoperability and game
> preservation. _Age of Conan_ is a trademark of Funcom; this project is not affiliated with or endorsed by Funcom.

## Feature status

What you can actually do, by subsystem. This is a research/preservation project, so "works" means _reproduced
byte-for-byte against a retail capture and (usually) confirmed in a real game client_ — not production-hardened.
Open work is tracked as Gitea issues; the `#NNN` refs point to the relevant issue.

**Legend:** ✅ working · 🟡 partial / needs work · ⬜ not started (roadmap)

<!-- stats:start -->
**Imported content** (the DB is the server's source of truth): 530 maps · 591 NPC types / 1,108 placements · 3,322 items · 20,848 spells · 265 abilities · 19,777 feats · 242 conversation nodes · 29 shops · 2,036 quests · 1,126 world objects · 177 patrol routes
<!-- stats:end -->

**Content coverage** — of the NPCs/vendors we've actually interacted with in our capture dumps, how many are
imported (capture-relative: retail has no public dialogue/vendor list, so this measures completeness _of what
we've seen_, and names the gaps). Regenerate with
`dotnet run --project Faolan.Importer -- --report coverage --report-arg readme`:

<!-- coverage:start -->
- **NPC conversations** — 34/37 greeted dialogue NPCs imported (91.9%)
- **Vendor shops** — 21/21 captured vendors imported (100.0%)
- **RDB template catalog** (type 1000020, TEA-encrypted) — 3,549/77,631 decrypted, **4.6% of the total encrypted**. Keys held per category: 3,284 items, 265 abilities, 0 feats (feat keys absent from captures). (+20,417 plaintext catalog entries, no key needed.)
<!-- coverage:end -->

### Connection & session

- ✅ **Log in and play** — the full login handshake (encrypted, single-use challenge per login; any password is
  accepted until the client is key-patched), character roster and selection, and world entry
- ✅ **Your own character sheet** — level, vitals, attributes, wallet, kills and deaths all come from your character
  rather than from the captured one the login snapshot was built out of; its last opaque regions are decoded, and
  every stat in them is named (#393, #447)
- ✅ **Create a character** — the appearance editor and the Tortage opening tableau, for every one of the twelve
  classes: each starts with its own class base pools, starting kit and feats, and walks into the world without a
  relog. The editor offers the client's full option set per race and sex — heads, hair, colours, tattoos, beards,
  piercings, face morphs and voices — including the **Khitan** race (#469)
- ✅ **Appearance renders as chosen** — hair, beard, body size, face markings and tattoos, confirmed in-client
  (#240, #288, #314)
- ✅ **The Tortage intro cutscene** — drowning, washing ashore, Kalanthes walking up and speaking his lines, all
  in-client; the world it drops you into is constructed from the database like any other login, not replayed
- 🟡 The tutorial tail after it (combat tutorial → approach Casilda) is not wired yet (#234, #261, #262)

### Realms & deployment

- 🟡 **Run multiple realms** — one shared login tier fronts any number of independent realm servers, each with its
  own world, chat, and guilds, all on a shared database; guilds, the auction house, mail, and character names are
  realm-scoped, and you're routed to your selected character's realm. Server-side complete and fleet-tested;
  pending a live two-realm shakedown (#324)

### World & simulation

- ✅ **A living world, built from data** — the world, its objects, and its NPCs are all constructed from the
  database and streamed to you as you move, never replayed from a recording
- ✅ **Spawns are fully decoded** — the player and NPC spawn streams are typed end to end with no opaque regions
  left, and every one of the 11,632 captured spawns re-encodes byte-exact; a buffed humanoid no longer spawns bald
  (#475, #477)
- ✅ **NPC AI** — enemies aggro and give chase, leash back and heal when you break away, and walk patrol routes
  (177 of them, harvested across the whole corpus — #425); friendly and neutral NPCs never attack and show green
  names, hostiles red
- ✅ **Interactive objects** — doors and gates open when used; anything not yet interactive tells you so instead of
  silently doing nothing
- 🟡 **Resource harvesting** — mining a rock, chopping a tree or picking a shrub yields a stack of the matching
  resource, across all 141 nodes already in the world; the node empties and disappears, and comes back on zone
  reload. Missing: the per-swing depletion (retail's nodes are shared, so the captures can't show it), a real
  respawn timer, and Elm/Slate, whose resource items aren't in the client data we have (#494)
- 🟡 **Boss nameplates** — elite and boss NPCs carry the rank that draws the skull marker beside their name (56 of
  them, King Conan and Keaira among them); pending an in-client confirm (#508)
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
- 🟡 **Environmental damage** — the world can hurt you now, where before every point of damage came from a
  creature. **Falls** cost health scaled to your own maximum and leave a bruise or a broken bone depending on how
  far you dropped; **lava** burns and kills, using the client's own 105 lava volumes across 22 playfields rather
  than a guess at where the flows are. Dying stacks a 30-minute death penalty, and a second death stacks another
  on top. Pending an in-client confirm (#495, #504)
- 🟡 **Local travel** — wagoners move you for a fee: pick a destination, pay, confirm, and arrive at its entrance,
  either across a loading screen or in place within the map. Criminals are refused, as the client's own rules say.
  The retail trigger (the pick inside the NPC's conversation) is not wired yet, so it runs from a GM command
  (#506, #514)
- ✅ **Sit on benches** — sitting down, switching seats and standing up, verified in a real client (#355)
- 🟡 **Fuller simulation** — a continuous AI heartbeat and smoother movement are in progress (#190, #225, #272)

### Combat

- ✅ **Melee & spellcasting** — the full attack/cast cycle: weapon draw, cast bar, hits, damage, and vitals, with a
  threat/hate list driving who NPCs go after
- ✅ **Floating damage numbers & combat log** for your hits, matching retail's distinct melee and spell number styles,
  each tagged with the correct damage school (physical, fire, cold, …) the way retail sends it
- ✅ **Interrupts** — moving mid-cast cancels the spell and shows the "Interrupted" message, exactly like retail (#347)
- ✅ **Health, mana & stamina regen** — pools refill out of combat on retail's own regen channel and stop the moment
  a fight starts; the rates come from your attributes and match retail's captured per-second deltas (#442)
- ✅ **Max health, mana & stamina** — per-class, per-level base curve from the client's own table, plus gear, feats
  and any buff that raises a pool. Verified against every captured level-up; casters have no stamina bar (#450)
- ✅ **Death & resurrection** — the release screen lists the resurrection points you've discovered, nearest first,
  and each revives you at its own strength (10/50/100%) rather than always full (#372, #375)
- ✅ **Discovering resurrection points** — walking near a new point pops retail's "point discovered" banner and adds
  it to your list in one frame; works for all **538** imported points, verified in a real client (#372)
- 🟡 **Recalls** — the Path of Asura spell returns you to where you are bound, while the Totem of Origins returns
  you to your race's home city; these are two different destinations and used to be one. Capture-verified for
  Stygian, the other three races by the same rule (#206, #510)
- ✅ **Buffs & debuffs** — apply, tick (damage-over-time), expire on a timer, and right-click to cancel; self-buffs
  and enemy debuffs both verified in-client. Each damage-over-time spell ticks at its own measured rate, 0.5 to 11
  seconds, instead of one 2-second constant (#274, #370, #437)
- 🟡 **Damage scale** — NPC swing damage is measured from the captures (it had been ~15x too weak) and caster NPCs
  land their spells with the right school. Only levels 1-6 are well sampled; above that the curve is extrapolated
  and wants an in-client pass (#401, #479)
- ✅ **Shield of the Risen** — the newcomer protection buff, re-applied on login and each zone, dropping the moment
  you act aggressively (#361)
- ✅ **NPC death animations** — enemies play a proper death on the killing blow instead of vanishing or teleporting
  back to spawn; verified in-client per creature type, with more being harvested (#362)
- 🟡 **Gear-driven combat stats** — weapon damage rolls per swing, worn armour and magical Protection mitigate
  incoming hits on retail's own mitigation formula and coefficients, and Critical Rating / Critical Damage Rating
  produce crits. The crit curve and the NPC side of mitigation stay provisional — retail decides both server-side
  and never tells the client (#395, #401)
- 🟡 **Weapon-set swap** — switching loadouts changes which set's stats count and is relayed to nearby players, as is
  equipping and unequipping gear. Pending in-client verification (#400)
- 🟡 **Ground-targeted AoE** (e.g. Deathly Blizzard) — ticks damage with floating numbers; the per-spell radius and
  cadence are still approximate (#349)
- 🟡 **Buff stat effects** — a buff's per-stat bonuses and penalties apply to your live stats and come back off when
  it expires, is cancelled or you die; flat ones add, percentage ones scale, and one that raises a max pool moves
  the pool so the bar fills to it. Awaiting an in-client check of the applied values (#367, #435, #436, #450)
- 🟡 **Spell cooldowns** — fully reverse-engineered; the client runs the recast timer from its own data (retail
  sends nothing), pending an in-client confirm of the server-override case (#271)
- 🟡 **Spellweaving** (necromancer and demonologist) — built and playable; the toggle turned out to be a different
  key per class, so demonologists could not weave at all. The energy-bar drain is the last piece, and Priest of
  Mitra / Tempest of Set are unmapped because no capture shows either weaving (#141, #505)
- 🟡 **Combat shields** — the guard-arc render is not yet decoded (#20)
- 🟡 **Fatalities** — the roll (5% at level 1 to 30% at 80), both fatality buffs, the necromancer's Frozen Hatred
  proc and its pet all work in-client (#140); the finishing-move *animation* never plays and the trigger for it is
  client-side (#440)
- ⬜ **Real directional combat** with defensive shield arcs (#20) · **combos** — the client already drives the whole
  combo chain; the finisher's bonus damage needs server-side data not present in the client (#139)

### Character progression

- ✅ **Learn spells, abilities & combos** — granted on login and at each level-up, with every rank learned and
  shown; all twelve classes now get their level-1 kit (four were getting none)
- ✅ Combos stick across relogs, not just at level-up
- 🟡 **Feats** — the catalog is imported from the client: **19,777 feats** with trees, ranks and tooltips, 23,321
  stat effects, a 5,501-edge prerequisite graph, and the wire↔catalog id map that joins a character's trained feats
  to it. Trained feats grant their stats, training enforces prerequisites and the real point budget (79 at level
  80), and a character's several named specs are each kept with their own build. Missing: per-feat cost and tier and
  each class's tree layout (server-side data the client is never sent), and the retail training trigger — so
  training runs off a GM command for now (#251, #467, #474)
- ✅ **Attributes** — Strength/Constitution/Intelligence/Wisdom/Dexterity from the per-class, per-level base plus
  gear and buffs, so the formulas that read them (regen, mitigation) get real inputs. Retail never sends these; the
  base was decoded from the client and checked in-client for all twelve classes at all eighty levels (#444)
- 🟡 XP curve is a provisional ramp; the real thresholds are partly harvested (#297)
- 🟡 **Skill points** — the pool is a pure function of level, not an award, so it is stated on login rather than
  granted. Only five levels have been captured, and the curve has no formula to fill the rest in, so the other
  levels keep whatever the character already had rather than showing an invented number (#507)
- 🟡 Class spell / combo / ability catalogs are still filling in; empty spell and item names were backfilled from the
  client's own English string table (LDB), so far fewer entries render nameless (#291, #296, #303, #300)
- ⬜ Alternate Advancement / veteran / mastery (#150) · offline training (#143) · apprentice/mentor (#144)

### Items & economy

- ✅ **Inventory** — containers, equipping, moving items, and starting gear
- ✅ **Item quality** — every item's rarity from Mundane to Legendary, so tooltips and name colours match retail (#397)
- ✅ **The full economy** — buy and sell at vendors (the shop window opens on use), bank storage, the auction house
  (list and buy), and mail carrying money and item attachments
- ✅ **Item claims** — the account claim/redeem window (Claim, Claim All), including infinite and multi-charge
  claims like Double XP, delivered straight to your bag (#359)
- ✅ **Consumables** — right-click a potion, food, or drink to use it and apply its effect
- ✅ **Creature loot** — killed enemies drop a lootable bag or chest, chosen per creature type (#363), and a looted
  item lands in the backpack slot you dragged it onto (#462)
- ✅ **Inventory stays in sync** — a server-side change to your bags (a quest taking the slave manacles off) pushes
  both containers whole, the way retail does (#380)
- ✅ **Complete tooltips** — item, spell and ability descriptions are no longer clipped at 512 characters mid-word
- 🟡 **Gear stat bonuses** — each item's whole tooltip stat list is decoded from its template (4,223 stat rows over
  1,705 items, matching the client's own values): +HP/+Mana, weapon min–max damage, armour and Protection, the crit
  ratings, Magic Damage, Combat Rating and the regen stats all feed the fight. The per-hit damage/mitigation/crit
  formulas are provisional pending in-client tuning (#3, #395, #401, #441)
- 🟡 Guild bank (#132)
- ⬜ Tradeskills — gathering and crafting professions (#148) · gem socketing for attribute bonuses (#311)

### Social & guilds

- ✅ **Chat** — say, channels, system messages, and whispers
- ✅ **Guilds** — create, invite and accept, guild tag, and renown, with the guild window appearing instantly on
  create (no relog) (#306). A new guild keeps the governing form you picked, and the rank ladder is per form, as the
  client's own tables have it (#469)
- ✅ **Groups (teams)** — invite (with the inviter's message, #459), accept, decline, leave, kick, leader promotion
  on the leader leaving, and the team dissolving at one member; the `~Team` chat channel appears and disappears
  with it (#101)
- ✅ **/who and the guild browser** — the character search answers from the players actually online, filtered on
  level range, class, race, playfield and name and paged as the client asks; guild search filters the realm's
  guilds. Players are reported at their real level, in search results, team rosters and invites alike (#460)
- ✅ **Friends list** — add, remove, persistence across a relog, and presence: your entry is pushed to everyone
  watching when you log in or out, which is what makes the list render at all (the client hides offline friends)
  (#99)
- ✅ **Player-to-player trade** — request, the window, items and money both ways, rearranging and removing offers,
  and the exchange committing on the double accept: both inventories and both wallets, with any change to either
  offer clearing both accepts so nobody can agree to one table and receive another (#101)
- 🟡 Member kick / promote / demote (#104) · trade **cancel** ends the session but does not close the partner's
  window — the s2c side of cancel/decline/remove needs a capture (#427) · team loot rules are accepted but not
  applied (#101)
- ⬜ Ignore list (opcodes not yet identified) · quest sharing and team quests (#101) · Claude as a virtual friend
  (#100)

### Quests & dialogue

- ✅ **Quests** — kill / loot / talk objectives, the journal, and the Casilda Tortage intro chain end to end
  (kill → loot → free → follow)
- ✅ **NPC conversations** — branching dialogue driven by the client's dialogue engine
- ✅ **Dropping a quest sticks** — abandoning one removes it from the journal and confirms it the way retail does,
  instead of the quest returning on the next login; the live J-panel refresh that rides along still needs a
  capture (#457, #482)
- 🟡 Escort completion (#262) · turn-in and reward dialogs (#260) · Lua quest scripting
- ✅ Day and night Tortage are two ordinary playfields (4020 / 4022), both imported — there is no phasing mechanism
  to build, and the lighting is the client's own (#149)

### Mounts & pets

- ✅ **Necromancer pets** — summon minions by type and have them follow; which spell summons which minion is
  imported content, not code, so a newly decoded summon is a database row
- 🟡 **Mounts** — working; importing more from captures
- ⬜ Mounted combat (#142)

### Group PvE content (not started)

- ⬜ Dungeons & instances — solo and group encounters, epic dungeons, raids (instanced zones + encounter
  scripting; #309). Two pieces are now scoped from captures: instances are entered at a chosen **difficulty**
  (normal vs epic) and can refuse you with a reason (#511), and a group instance is owned by the **team** rather
  than per character (#512)
- ⬜ Factions & reputation — the Khitai / _Rise of the Godslayer_ system: faction ranks, faction vendors, and
  faction companions. The in-game Factions window already populates with standings for imported characters; what's
  missing is the catalog and the reputation _changes_ from quests and kills (#310, #315)

### Guild base & large-scale (not started)

- ⬜ Guild base — player-built **guild cities** and **battlekeeps** in the Border Kingdoms: the central keep, walls
  and gates, rank-gated structures, and hired NPC guards (#147)
- ⬜ Sieges — massed guild-vs-guild siege warfare (siege weapons, destructible walls) fought over those keeps (#147)
- 🟡 **Criminal status** — killing a player makes you a criminal for five minutes, and while you are one normal
  vendors refuse to trade with you; smugglers at outlaw camps still will. The rules come from the client's own
  spell text. Missing the trigger, since player-vs-player combat is not modelled — so it runs off a GM command —
  plus the murder-point ladder up to Murderer (#503)
- ⬜ PvP progression — open-world PvP, PvP minigames, and the PvP-resource / consequence layer that feeds base
  building (#146)

### Tooling

- ✅ **Wireshark dissector** — full Conan-protocol decoding for capture analysis
- ✅ **Client launcher** — drives the game client unattended, straight into the world
- ✅ **Content-CDN mirror** — serves the client's streamed assets so it loads cleanly offline
- ✅ **In-process MCP server** — live database + session inspection for tooling
- ✅ **Client debug channels** — switches on the retail client's OWN logging (`--debug-channels`), so it reports
  every stat it applies and what caused it, instead of us inferring from screenshots (#443)
- ✅ **Web armory & 3D viewer** — browser character/equipment viewer plus GM tools
- ✅ **GM commands** — our own `.commands`, plus the retail client's built-in ones: a GameMaster account flips the
  stock client into GM mode and its `/teleport`, `/cloak`, `/kill`, `/setname`, … now route into the same server
  command table, so both spellings work. `.setstat` measures how the client scales a stat for display. Flying is the
  client's own `/luke` and needs no server support (#449, [docs](docs/subsystems/gm-commands.md))
- 🟡 What the server should send back for each of the client's 133 GM commands is per-command work; most are not
  answered yet (#449)
- 🟡 **Importer** — rebuilds the game database from captures + the client's asset files, plus read-only `--report`
  diagnostics (NPC damage curve, item stat-region census, content coverage)

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
is the same data as plain text, regenerated automatically by the `.githooks/pre-commit` hook whenever `faolan.db`
is committed — so a data change is **reviewable in a diff** instead of showing up as `Bin 8839168 -> 8839168
bytes`. It is generated: never hand-edit it, and don't bother regenerating it yourself (the hook dumps the
*staged* blob, so it always matches the commit).

It is written from the EF model rather than by `sqlite3 .dump`, so it loads into SQLite _and_ PostgreSQL, and it
doubles as the **seed**: a database with no game content is filled from it at startup (delete `faolan.db` and the
server rebuilds it), while a populated one is never touched. Characters are excluded — per-developer play state.

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

## Configuration

Every server-side executable — the server, the importer, `Faolan.Web`, the `Faolan.Web.Content` CDN mirror,
and the `Faolan.Mcp` chat-bridge — shares **one** config model: **environment variables > command line >
`faolan.json`**, with `faolan.json` **optional** (a container can be configured entirely by env vars, with no
file baked into the image). It lives at the repo root, and is found by searching up from the working directory —
so a tool started from its own project directory still uses the repo's file (`FAOLAN_CONFIG` overrides).

Related keys are grouped into sections; the remaining shared keys stay flat:

| Key | Meaning |
| --- | --- |
| `Database` `{ Provider, ConnectionString }` | `Sqlite` (default; `Data Source=faolan.db`) or `Postgres` + its connection string |
| `ServerIpAddress` | the address the login tier advertises to clients (must be reachable from the client) |
| `UniverseAgentPort` / `PlayerAgentPort` | login-tier listen ports (7010 is the fixed client entry point) |
| `WelcomeString` / `TerrainDir` | login MOTD; baked-terrain dir for line-of-sight (optional) |
| `Login` `{ PrivateKey, PublicKey, RetailPublicKey, AllowUnpatchedClients }` | the login DH keypair, and whether a client the server cannot decrypt — an unpatched one, which proves only the account name — may log in (default: yes) |
| `Mcp` `{ Enabled, Port, Url }` | the in-process MCP endpoint, and the chat-bridge's target URL |
| `CdnMirror` `{ Store, Ip, Port }` | the `Faolan.Web.Content` mirror's store dir + bind address |
| `ChatBridge` `{ PollMs, BacklogGraceMs }` | the Claude chat-bridge poll-loop tuning |
| `Log` `{ File, PacketBytes }` | log-file path; whether packet log lines include the raw byte dump |
| `Account` `{ Premium, PremiumCharSlots, FreeCharSlots }` | subscription state advertised to every account in the character list (default: premium, 10 slots / 4 free) |

**One name per setting, whatever the input method.** A key is spelled the same in `faolan.json`, in the
environment and on the command line — only the separator differs:

| method | how |
| --- | --- |
| `faolan.json` | nested: `"Database": { "ConnectionString": … }` |
| environment | `__` between the parts: `Database__ConnectionString=…`, `Mcp__Port=7098`, `CdnMirror__Store=/data` |
| command line | the config path itself: `--Database:ConnectionString "…"`, `--CdnMirror:Store /data` |

Flat keys are just their own name in all three (`ServerIpAddress=…` / `--ServerIpAddress …`). There are **no
short aliases** — `--store`, `--ip`, `--port` and `--connectionstring` were a fourth spelling to keep in step and
are gone. `Faolan.ClientLauncher` is the one exception: its flags are client-launch parameters, not server
config, so it has its own arguments (see below).

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
| ------------------------ |----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Faolan/`                | The server host — runs every protocol listener as a hosted service.                                                                                                                    |
| `Faolan.Core/`           | Shared base types, protobuf, crypto, and enums, with the layered `Faolan.Core.Database`, `Faolan.Core.Network`, and `Faolan.Core.Rdb` projects on top.                                 |
| `Faolan.Importer/`       | CLI that (re)builds `faolan.db` from packet captures + the client RDB.                                                                                                                 |
| `Faolan.Mcp/`            | In-process MCP server exposing DB queries + live-session inspection to Claude.                                                                                                         |
| `Faolan.ClientLauncher/` | Launches the game client unattended (patcher stand-in).                                                                                                                                |
| `Faolan.Web.Content/`    | Store-and-forward proxy for the client's content CDN (fixes the loading hang).                                                                                                         |
| `Faolan.Wireshark/`      | A Wireshark/tshark C plugin that fully dissects the Conan protocol (the canonical capture tool).                                                                                       |
| `Faolan.Web/`            | Blazor web app: public character/equipment 3D viewer + armory, plus admin/GM tools (live map radar, GM console, ban, mail, a generic `faolan.db` editor) gated to GameMaster accounts. |
| `Faolan.Tests/`          | The unit/golden test suite.                                                                                                                                                            |
| `tools/`                 | The capture/RE toolchain: Python dissectors + the tshark dump script, Ghidra scripts, Frida scripts.                                                                                   |

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

- **High-level and caster-NPC combat**, with the client's own `/logcombat on` running alongside the pcap → the
  damage curves are only sampled to level 6, and the log names what the packets cannot: crits and absorbs
  (#479, #401)
- A **spell cast of a known-cooldown spell** → the `SetSpellRecastTime` GameAction (#271)
- **Guild-bank** deposit/withdraw, and **guild** member kick/promote/demote (#132, #104)
- A **necromancer pet summon in the open world** (#307), and the **s2c side of a trade cancel / decline /
  remove-item** — the c2s half is handled, but cancelling currently leaves the partner's window open (#427)
- A **combo landing on a live target** → the combo-finisher's bonus damage, which retail decides server-side and so
  cannot be recovered from the client alone (#139)
- A second **quest abandoned** in a live session → the `QuestLogList` refresh variant retail sends with it; two
  samples do not pin a grammar, and a malformed list takes the whole route down (#482)
- **Level-up** captures across more classes/levels → fill the combo/spell/XP tables (#296, #297)
- A **dungeon run**, the **faction/reputation** UI, and an **item socket** action → the roadmap features
  (#309, #310, #311)

Contributing such captures is the single highest-leverage way to move the blocked items.
