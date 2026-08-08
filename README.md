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
**Imported content** (the DB is the server's source of truth): 530 maps · 1,018 NPC types / 3,058 placements · 4,225 items · 20,848 spells · 265 abilities · 19,777 feats · 515 conversation nodes · 70 shops · 5,048 quests · 2,400 world objects · 853 patrol routes · 14 client trigger volumes
<!-- stats:end -->

**Content coverage** — of the NPCs/vendors we've actually interacted with in our capture dumps, how many are
imported (capture-relative: retail has no public dialogue/vendor list, so this measures completeness _of what
we've seen_, and names the gaps). Regenerate with
`dotnet run --project Faolan.Importer -- --report coverage --report-arg readme`:

<!-- coverage:start -->
- **NPC conversations** — 74/78 greeted dialogue NPCs imported (94.9%)
- **Vendor shops** — 48/48 captured vendors imported (100.0%)
- **RDB template catalog** (type 1000020, TEA-encrypted) — 4,448/77,631 decrypted, **5.7% of the total encrypted**. Keys held per category: 4,183 items, 265 abilities, 0 feats (feat keys absent from captures). (+20,417 plaintext catalog entries, no key needed.)
<!-- coverage:end -->

### Connection & session

- ✅ **Log in and play** — the full login handshake (any password is accepted until the client is key-patched)
- ✅ **Create a character** — hair, beard, body size, face markings and tattoos (#240, #288, #314)
- ✅ **The Tortage intro cutscene** — drowning, washing ashore, Kalanthes walking up and speaking his lines; the
  world it drops you into is built from the database, not replayed

### World & simulation

- ✅ **A living world, built from data** — the world, its objects and its NPCs are constructed from the database
  and streamed to you as you move, never replayed from a recording
- ✅ **Spawns are fully decoded** — player and NPC spawn streams typed end to end, no opaque regions; all 11,632
  captured spawns re-encode byte-exact (#475, #477)
- ✅ **NPC AI** — aggro and chase, leash and heal on a break, and 177 patrol routes; friendly NPCs never attack and
  show green names, hostiles red (#425)
- ✅ **Interactive objects** — doors and gates open when used; anything not yet interactive says so
- ✅ **Zone-line doors** — a doorway that changes your location teleports you, within a zone and across a loading
  screen; the costly ones ask you to confirm first. A few rarer ones only play the open animation (#341, #637)
- ✅ **Area-name banners** — crossing into a named district floats its title on screen; coverage grows with the
  captures (#377)
- ✅ **Instanced zones** — solo areas like the Tortage beach give each player their own copy
- ✅ **Sit on benches** — sitting down, switching seats and standing up (#355)
- 🟡 **Resource harvesting** — mining, chopping and picking yield the matching resource across all 141 nodes; the
  node empties and returns on zone reload. Missing: per-swing depletion, a real respawn timer, and Elm/Slate (#494)
- 🟡 **Ambient wildlife & spawns** — each zone's spawn groups are decoded from the client and a live spawner keeps
  them stocked and roaming. The creature type per group is server-only, so ~21% are auto-matched and the rest are
  hand-confirmed (#332, #363)
- 🟡 **Line-of-sight & terrain** — NPCs won't aggro through a hill or wall, and loot settles to the ground, off
  baked per-map terrain. Aggro gating pending an in-client confirm (#284)
- 🟡 **Damage schools** — every hit carries the element it was dealt with, numbered as the client numbers them,
  and falls and hazards carry their own marker. Physical melee is one school for everyone; retail picks it per
  weapon (#487)
- 🟡 **Knockback** — a blow can shove you along a path you did not walk. Distance, direction and resistance scaling
  rest on a single captured sample, and no attack triggers it yet — GM command only (#496)
- 🟡 **Environmental damage** — falls cost health scaled to your maximum and leave a bruise or broken bone; lava
  burns and kills across the client's 105 lava volumes. Death stacks a 30-minute penalty. Pending an in-client
  confirm (#495, #504)
- ✅ **Local travel** — wagoners move you for a fee, triggered from inside the NPC's own conversation: pick, pay,
  arrive; criminals are refused. The trip goes out on retail's own packets — the move-the-player action and the
  *Racer* buff — and three travellers who state their destination in dialogue text work too (#506, #514, #635,
  #636)

### Combat

- ✅ **Melee & spellcasting** — the full attack/cast cycle: weapon draw, cast bar, hits, damage and vitals, with a
  threat list driving who NPCs go after
- ✅ **Floating damage numbers & combat log** — retail's distinct melee and spell number styles, each tagged with
  its damage school
- ✅ **Interrupts** — moving mid-cast cancels the spell and shows the "Interrupted" message (#347)
- ✅ **Health, mana & stamina regen** — pools refill out of combat and stop when a fight starts, at rates driven by
  your attributes (#442)
- ✅ **Max health, mana & stamina** — per-class, per-level base from the client's table plus gear, feats and buffs;
  casters have no stamina bar (#450)
- ✅ **Death & resurrection** — the release screen lists the resurrection points you've discovered, nearest first,
  each reviving you at its own strength (#372, #375)
- ✅ **Discovering resurrection points** — walking near a new point pops retail's banner and adds it to your list,
  across all 538 imported points (#372)
- ✅ **Buffs & debuffs** — apply, tick, expire on a timer and right-click to cancel. Each damage-over-time spell
  ticks at its own measured rate; 53 of the 84 in the captures are measured, the rest use the median (#274, #370,
  #437, #524)
- ✅ **Shield of the Risen** — newcomer protection, re-applied on login and each zone, dropping when you act
  aggressively (#361)
- ✅ **NPC death animations** — a proper death on the killing blow, per creature type, with more being harvested
  (#362)
- 🟡 **NPCs swing combos** — a mob's weapon picks the combo it performs, at the rate retail does it (28% of melee
  swings, 14% of shots), and archers shoot on their own record rather than playing a sword swing. Dagger, polearm
  and bow mobs also swing their class family; two-handers and shields still swing generic, because which archetype
  those belong to is not in anything the client ships (#623, #675, #676)
- 🟡 **Outlaw resurrection points** — the client's 49 criminal destinations are imported, but revive still sends
  everyone to the normal point (#517, #503)
- 🟡 **Recalls** — Path of Asura returns you to your bind point, the Totem of Origins to your race's home city.
  Capture-verified for Stygian, the other three by the same rule (#206, #510)
- 🟡 **Damage scale** — NPC swing damage is measured from the captures and caster NPCs use the right school. Only
  levels 1-6 are well sampled; above that the curve is extrapolated (#401, #479)
- 🟡 **Gear-driven combat stats** — weapon damage rolls per swing, armour and Protection mitigate on retail's own
  formula, and crit ratings produce crits. The crit curve and the NPC side of mitigation stay provisional (#395,
  #401)
- 🟡 **Weapon-set swap** — switching loadouts changes which set's stats count and is relayed to nearby players, as
  is equipping and unequipping. Pending in-client verification (#400)
- 🟡 **Ground-targeted AoE** — ticks damage with floating numbers at the blast radius the client states for the
  spell (1,023 have one), a default otherwise; the per-tick cadence is still approximate (#349)
- 🟡 **Buff stat effects** — per-stat bonuses and penalties apply and come back off on expiry, cancel or death;
  flat ones add, percentage ones scale. Awaiting an in-client check of the values (#367, #435, #436, #450)
- 🟡 **Spellweaving** (necromancer and demonologist) — built and playable. The energy-bar drain is the last piece,
  and Priest of Mitra / Tempest of Set are unmapped for want of a capture (#141, #505)
- 🟡 **Directional combat** — an NPC opens its blue shield rose on engaging and shifts a guard between arcs on a
  timer, broadcast to everyone watching. Missing: an NPC's swing carries no arc, so a player's own rose is inert in
  PvE, and a viewer who arrives late sees no rose until the next shift (#20, #621, #622)
- 🟡 **Fatalities** — the roll, both fatality buffs and the necromancer's Frozen Hatred proc all work in-client;
  the finishing-move animation never plays (#140, #440)
- 🟡 **Spell cooldowns** — a cast still on cooldown is refused, line-scoped the way retail's is, so every rank of
  a spell shares one timer; 3,703 spells carry a base recast read off their own record. Pending in-client
  verification (#271)

### Character progression

- ✅ **Learn spells, abilities & combos** — granted on login and at each level-up, every rank learned and shown,
  for all twelve classes; combos stick across relogs
- ✅ **Attributes** — Strength/Constitution/Intelligence/Wisdom/Dexterity from the per-class, per-level base plus
  gear and buffs, so regen and mitigation get real inputs. Verified in-client at all eighty levels (#444)
- 🟡 **Feats** — 19,777 feats imported with trees, ranks and tooltips, 23,321 stat effects and a 5,501-edge
  prerequisite graph. Trained feats grant their stats, training enforces prerequisites and the point budget, and
  named specs each keep their own build. Missing: per-feat cost and tier, each class's tree layout, and the retail
  training trigger — so training runs off a GM command. The decoded feat modifiers are not imported yet (#251,
  #474, #593)
- 🟡 **Skill points** — the pool is a function of level, so it is stated on login rather than awarded. Five levels
  are captured and there is no formula for the rest, so those keep what the character already had (#507)
- 🟡 **Spell unlock levels** — 8,460 spells carry one, read from the client's spell and combo records; 592 come
  only from the retired community scrapes (#650)
- 🟡 **XP curve** — anchored on the retail thresholds the captures carry (levels 1-9, 23-25 and 80); the levels
  between anchors are interpolated (#297)
- 🟡 **Class spell / combo / ability catalogs** — still filling in; empty spell and item names are backfilled from
  the client's own string table. The archetype-shared combos and spells now reach all three classes of their
  archetype rather than being filed under one (#291, #296, #303, #300, #581)
- ⬜ Alternate Advancement / veteran / mastery (#150) · offline training (#143) · apprentice/mentor (#144)

### Items & economy

- ✅ **Inventory** — containers, equipping, moving items, and starting gear
- ✅ **The full economy** — vendors (the shop window opens on use), bank storage, the auction house (list and buy),
  and mail carrying money and item attachments
- ✅ **Item claims** — the account claim/redeem window, including infinite and multi-charge claims (#359)
- ✅ **Consumables** — right-click a potion, food or drink to use it and apply its effect
- ✅ **Creature loot** — killed enemies drop a bag or chest chosen per creature type, and a looted item lands in
  the slot you dragged it onto (#363, #462)
- ✅ **Inventory stays in sync** — a server-side change to your bags pushes both containers whole (#380)
- ✅ **Complete tooltips** — item, spell and ability descriptions are no longer clipped mid-word
- 🟡 **Gear stat bonuses** — 4,223 stat rows over 1,705 items decoded from their templates: +HP/+Mana, weapon
  damage, armour and Protection, crit ratings, Magic Damage, Combat Rating and regen all feed the fight. The
  per-hit formulas are provisional pending in-client tuning (#3, #395, #401, #441)
- 🟡 **Guild bank** — a member can withdraw money, rank-gated and balance-checked; the window itself, deposits
  and item storage are unhandled (#132, #654)
- ⬜ Tradeskills — gathering and crafting professions (#148) · gem socketing (#311)

### Social & guilds

- ✅ **Chat** — say, channels, system messages, and whispers
- ✅ **Guilds** — create, invite and accept, guild tag and renown, with the window appearing on create. A guild
  keeps the governing form you picked and the rank ladder is per form (#306, #469)
- ✅ **Groups (teams)** — invite with a message, accept, decline, leave, kick, leader promotion, and dissolve at
  one member; the `~Team` channel comes and goes with it (#101, #459)
- ✅ **/who and the guild browser** — character search answers from the players online, filtered on level, class,
  race, playfield and name and paged as the client asks; guild search filters the realm's guilds (#460)
- ✅ **Friends list** — add, remove, persistence across a relog, and presence pushed to everyone watching (#99)
- ✅ **Player-to-player trade** — request, the window, items and money both ways, rearranging and removing offers,
  and the exchange committing on the double accept; any change clears both accepts (#101)
- 🟡 Member kick / promote / demote (#104) · trade **cancel** ends the session but leaves the partner's window
  open, pending a capture (#427) · team loot rules are accepted but not applied (#101)

### Quests & dialogue

- ✅ **Quests** — kill / loot / talk / collect / reach objectives, and the journal
- ✅ **Gather quests work however you get the item** — a collect objective ticks when the item reaches your bags,
  from a corpse, a node, a vendor or a reward; 62 quests that were inert now run (#521)
- ✅ **"Go there" objectives complete on arrival** — typed from the client's own reference, so 11 more quests run;
  goals the client gives no map marker for stay inert (#641)
- ✅ **The client's quest text is readable** — eleven localized-text categories are encrypted and the key is sent
  per string; all 168 objective texts, the journal titles and summaries, and the NPC dialogue decrypt (#520)
- ✅ **NPC conversations** — branching dialogue driven by the client's dialogue engine; every branch any capture
  walked is reachable in-game (#642, #644)
- ✅ **NPCs greet you differently the second time** — a full welcome first, a terser line after (#645)
- ✅ **Dropping a quest sticks** — abandoning one removes it from the journal and confirms it the way retail does;
  the live J-panel refresh that rides along still needs a capture (#457, #482)

### Mounts & pets

- ✅ **Necromancer pets** — summon minions by type and have them follow; which spell summons which minion is
  imported content, so a newly decoded summon is a database row (#307, #640)
- 🟡 **Mounts** — working; importing more from captures
- ⬜ Mounted combat — the mount abilities are granted, but a lance charge needs a capture (#667)

### Group PvE content (not started)

- ⬜ Dungeons & instances — solo and group encounters, epic dungeons, raids (#309). Two pieces are decoded but not
  built: the difficulty an instance is entered at (#511), and group instances being owned by the team (#512)
- ⬜ Factions & reputation — the Khitai faction ranks, vendors and companions. The Factions window populates for
  imported characters; the catalog and the reputation changes from quests and kills are missing (#310, #315)

### Guild base & large-scale (not started)

- ⬜ Guild base — player-built guild cities and battlekeeps: the central keep, walls and gates, rank-gated
  structures, and hired NPC guards (#147)
- ⬜ Sieges — massed guild-vs-guild siege warfare fought over those keeps (#147)
- ⬜ PvP progression — open-world PvP, minigames, and the PvP-resource layer that feeds base building (#146)
- 🟡 **Criminal status** — killing a player makes you a criminal for five minutes; normal vendors and wagoners
  refuse you, smugglers still serve you. Missing the trigger (PvP combat is not modelled, so it runs off a GM
  command) and the murder-point ladder (#503)

### Tooling

- ✅ **Wireshark dissector** — full Conan-protocol decoding for capture analysis
- ✅ **Client launcher** — drives the game client unattended, straight into the world
- ✅ **Content-CDN mirror** — serves the client's streamed assets so it loads cleanly offline
- ✅ **In-process MCP server** — live database + session inspection for tooling
- ✅ **Client debug channels** — switches on the retail client's own logging, so it reports every stat it applies
  and what caused it (#443)
- ✅ **Web armory & 3D viewer** — browser character/equipment viewer plus GM tools
- ✅ **GM commands** — our own `.commands` plus the retail client's built-in ones: a GameMaster account flips the
  stock client into GM mode and its `/teleport`, `/cloak`, `/kill`, … route into the same command table (#449,
  [docs](docs/subsystems/gm-commands.md))
- 🟡 **Importer** — rebuilds the game database from captures + the client's asset files, plus read-only `--report`
  diagnostics
- 🟡 What the server should answer for each of the client's 133 GM commands is per-command work; most are not
  answered yet (#449)

### Wire fidelity

- ✅ **One codec per message** — every byte the server sends is built by a single codec with a uniform
  `Build`/`Parse` surface; 16 convention gates enforce it with no exclusions (#530)
- ✅ **Every codec round-trips its captured bytes** — each parses and rebuilds real corpus packets byte-exact,
  with an empty allowlist (#538, #543, #544, #545)
- ✅ **Behaviour commands fully decoded** — all 34 of the client's behaviour commands and every per-command
  trailer: 100% of the 67,299 captured records (#536, #549)

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

### Run it in Docker

`docker compose up` builds the server image and starts an all-in-one deployment — no .NET SDK, no clone layout
to get right:

```sh
docker compose up
```

Three images are defined, each with its `Dockerfile` **beside its project** but built with the repo **root** as
context (every build graph spans sibling projects):

| image | from | what it is |
| --- | --- | --- |
| `projectfaolan` | `Faolan/Dockerfile` | the game server — all-in-one by default, or `--role login` / `--role realm=<id>` |
| `projectfaolan-web` | `Faolan.Web/Dockerfile` | the armory / 3D character viewer / GM tools |
| `projectfaolan-cdn` | `Faolan.Web.Content/Dockerfile` | the store-and-forward mirror of the game's content CDN |

```sh
docker build -f Faolan/Dockerfile -t projectfaolan .
```

Only the server service is enabled in `docker-compose.yml`; the process-per-realm split, a Postgres backend, and
the web + CDN services are commented examples in the same file, each with the caveats that bite in a container.
Two are worth knowing up front:

- **`Server:PublicAddress` must be an address the client can reach** — the host's IP or a resolvable name, never
  `127.0.0.1`. The login tier advertises it (and each `Realm` row's `IpAddress`) and the client connects there
  from *outside* Docker. The same goes for any bind address: a service listening on `127.0.0.1` inside a
  container is on its own loopback, and a published port reaches nothing.
- **The image ships the seed, not a database.** `faolan.sql` is baked in and the server builds `faolan.db` from
  it into the mounted volume on first start, so a container never mutates a file from the repo and a clean deploy
  is an empty directory. Nothing is re-seeded on later boots.

## Configuration

Every server-side executable — the server, the importer, `Faolan.Web`, the `Faolan.Web.Content` CDN mirror,
and the `Faolan.Mcp` chat-bridge — shares **one** config model: **environment variables > command line >
`faolan.user.json` > `faolan.json` > built-in defaults**, with both files **optional** (a container can be
configured entirely by env vars, with no file baked into the image, and the server runs with neither). They live
at the repo root, and are found by searching up from the working directory — so a tool started from its own
project directory still uses the repo's files (`FAOLAN_CONFIG` / `FAOLAN_USER_CONFIG` override).

**Every setting is declared once**, in `FaolanSettings` (`Faolan.Core/Cli/FaolanSettings.cs`), as the
System.CommandLine option that puts it on a command line: its name (which *is* its configuration path), its
`--help` description, its **type** and its default. That one catalog is both the bottom layer above and the source
of each executable's `--help`, so a documented default cannot drift from the one the code uses, and a key nothing
declares cannot be silently read. Because the declaration is typed, a value the setting cannot hold is refused
**while parsing the command line** — `--Mcp:Port banana` and `--Database:Provider mysql` are errors that name the
option (the latter listing the engines that exist) rather than crashes at whatever startup first read them, a bool
setting is a flag (`--Mcp:Enabled` needs no value), and `--help` prints `<Postgres|Sqlite>` and `[default: 7099]`
from the same declaration. A malformed value in a *file* or the environment throws naming the key, since no parser
sees those. The committed `faolan.json` is that catalog written out — regenerate it with:

```sh
dotnet run --project Faolan -- --write-default-config   # every setting at its default; OVERWRITES
dotnet run --project Faolan -- --generate-login-key     # print a fresh login DH keypair
```

`--write-default-config` takes an optional path. `--generate-login-key` writes nothing at all: it prints both
halves of the pair — the private one to paste under `Login:PrivateKey` in `faolan.user.json` (a keypair is
per-deployment, and that file is gitignored), the derived public one because that is what every client has to be
re-patched with (`Faolan.ClientLauncher --patch-login`).

**`faolan.user.json` is gitignored, and that is its whole point.** `faolan.json` is committed and has to stay
machine-agnostic (no `/home/<user>/…` paths, no LAN addresses), so anything true only of *your* checkout goes in
the user file instead of being edited into the tracked one and accidentally committed. Same keys, same shapes; it
simply wins. Typical contents:

```jsonc
{
  // the client's RDB folder - part of the game INSTALL, so it has no committed default
  "Content": { "RdbDir": "/path/to/Age of Conan/RDB" },
  // a CDN mirror running on another box: PublicAddress is what the importer and the viewer dial.
  // A full URL - a path is appended to it, so a bare host would build a relative one
  "Cdn": { "PublicAddress": "http://192.168.1.5:7081" },
  // ...and anything else this checkout wants but a plain deployment should not pay for
  "Log": { "PacketBytes": true },
  "Mcp": { "Enabled": true }
}
```

Every key lives in a section - there are no root-level keys. The defaults below are the
catalog's — `--help` on any of the executables lists the same values for the subset it offers:

| Key | Meaning |
| --- | --- |
| `Database` `{ Provider, ConnectionString }` | `Sqlite` (default; `Data Source=faolan.db`) or `Postgres` + its connection string |
| `Server` `{ Address, UniverseAgentPort, PlayerAgentPort, WelcomeString }` | the address the login tier advertises to clients (must be reachable from the client - it may be a DNS name), its two listen ports (7010 is the fixed client entry point), and the login MOTD |
| `Content` `{ TerrainDir, RdbDir, ClientVersion }` | where the game data lives: the baked-terrain dir for ground-snap and line-of-sight, the client's RDB folder (optional - part of the game INSTALL, so it has no committed default), and the client build the protocol was decoded against |
| `Login` `{ PrivateKey, PublicKey, AllowUnpatchedClients }` | the login DH keypair, and whether a client the server cannot decrypt — an unpatched one, which proves only the account name — may log in (default: yes) |
| `Mcp` `{ Enabled, Port }` | the in-process MCP endpoint the game server hosts |
| `Cdn` `{ Store, ListenAddress, ListenPort, PublicAddress }` | the `Faolan.Web.Content` mirror's store dir + the interface it binds; `PublicAddress` is the address written into the patch config it serves back — **required by the mirror**, which will not start without it, since it is not the bind address and cannot be derived — **and the address every mirror CLIENT dials** — the importer's on-demand RDB fetches and `Faolan.Web`'s viewer, so a mirror on another box is this one key |
| `ChatBridge` `{ Enabled, PollMs, BacklogGraceMs }` | the Claude chat-bridge: whether it pushes in-game chat into the Claude session (default: yes), and its poll-loop tuning. It always dials the local `Mcp:Port` — that endpoint binds loopback, so its URL is derived rather than configured |
| `Log` `{ File, PacketBytes }` | log-file path; whether packet log lines include the raw byte dump |
| `Account` `{ Premium, PremiumCharSlots, FreeCharSlots }` | subscription state advertised to every account in the character list (default: premium, 10 slots / 4 free) |

**One name per setting, whatever the input method.** A key is spelled the same in `faolan.json`, in the
environment and on the command line — only the separator differs:

| method | how |
| --- | --- |
| `faolan.json` / `faolan.user.json` | nested: `"Database": { "ConnectionString": … }` |
| environment | `__` between the parts: `Database__ConnectionString=…`, `Mcp__Port=7098`, `Cdn__Store=/data` |
| command line | the config path itself: `--Database:ConnectionString "…"`, `--Cdn:Store /data` |

There are **no short aliases** — `--store`, `--ip`, `--port` and `--connectionstring` were a fourth spelling to keep in step and
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
dotnet run --project Faolan.ClientLauncher -- --console --patch-universeaddr -u faolan -p any -c <char>
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
| `Faolan.Web/`            | Blazor web app: public character/equipment 3D viewer + armory, plus admin/GM tools (live map radar, GM console, ban, mail, and three content editors — a generic `faolan.db` one plus purpose-built quest and spell screens) gated to GameMaster accounts. |
| `Faolan.Tests/`          | The unit/golden test suite.                                                                                                                                                            |
| `tools/`                 | The capture/RE toolchain: Python dissectors + the tshark dump script, Ghidra scripts, Frida scripts.                                                                                   |

## Documentation

- **`CLAUDE.md`** — architecture, build/run commands, the wire-protocol essentials, and working conventions
  (the orientation guide for anyone, human or AI, working in the codebase).
- **Gitea issues** (the `projectfaolan` repo) — the tracker of open work, grouped by `area:*` labels.
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
- A second **quest abandoned** in a live session → the `QuestLogList` refresh variant retail sends with it; two
  samples do not pin a grammar, and a malformed list takes the whole route down (#482)
- A **guild-city piece taking damage** (a siege, or any attack on a plot structure) → the destructible bodies are
  already decoded and placed, but nothing in the corpus ever shows one being damaged, so there is no destruction
  or construction grammar to reproduce (#498)
- **Level-up** captures across more classes/levels → fill the combo/spell/XP tables (#296, #297)
- A **dungeon run**, the **faction/reputation** UI, and an **item socket** action → the roadmap features
  (#309, #310, #311)

Contributing such captures is the single highest-leverage way to move the blocked items.
