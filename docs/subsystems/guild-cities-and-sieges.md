# Guild cities, Battlekeeps and sieges

No siege or city message is sent, and none can be: not one occurs anywhere in the capture corpus. What this page
records is the **ruleset** — and the reason that is worth writing down is that retail states almost all of it
**in its own words**, in ordinary user-facing LDB text: the preconditions, the timers, the party sizes and the
win condition. The decision layer built from it is in [What is implemented](#what-is-implemented) below.

Six sweeps of this subsystem before this one all searched the **wire** and the **RDB data tables**, and correctly
concluded that the corpus contains zero siege traffic and the tables carry no ownership or build state. The LDB
was the source none of them had read, and it is where the specification lives.

⚠️ **This is text, not protocol.** Every rule below is retail describing its own game to a player. It pins what
the server must *decide*; it says nothing about which bytes carry the decision, and no capture in the corpus
performs any of it.

## The precondition chain

The gates form one chain, and each link is quoted rather than inferred:

| # | gate | retail's words | LDB |
|---:|---|---|---|
| 1 | a guild needs **24+ members** to build a city | *"Only available to guilds with 24 or more members."* | 50020/606 |
| 2 | the guild must **book a city spot** | *"Your guild does not have a city spot. Book a spot to build a city…"* | 100/176329231 |
| 3 | the **Keep** must be built first | *"This building must be built to claim an area as yours, and without it, no other buildings can be raised."* | 100/215909 |
| 4 | a city is what starts **Guild Renown** | *"Your guild must have a guild city to begin earning Guild Renown."* | 50020/921 |
| 5 | the city must reach **Tier 3** to fight | *"Congratulations! Your guild city is now Tier 3. You may apply for Massive PvP battles."* | 100/87649219 |
| 6 | …which is also the Battlekeep gate | *"To build or capture a battlekeep in the Border Kingdoms, your guild must first possess a Tier 3 city keep."* | 10017/38 |
| 7 | **one Battlekeep per guild** | *"Your guild may only own one battlekeep."* / *"Your guild already owns a battlekeep. You may not fight for another one."* | 100/7997017, 100/164371129 |

City-spot booking has its own refusal set, which is what a bookable finite resource looks like: *"Booking failed.
There are no more free city spots."*, *"…Your guild already has a city spot."*, *"Booking succeeded. Your guild
has been assigned a city spot."* (100/20200, 20201, 129749027).

⭐ Gate 4 is what ties this page to the **renown ladder** already decoded from RDB 1000521 — 57 rewards over
renown levels 1–20, each with its own description
([client-data-tables.md](../rdb/client-data-tables.md)). The ladder is the *content* of a guild city; the chain
above is how a guild becomes eligible for it.

## The vulnerability window

| rule | value |
|---|---|
| window length | **2 hours** |
| default placement | *"a few days after any previous assault"* |
| when it may be moved | only in the **15 minutes** after a city is conquered or an assault repelled |
| who may move it | the controlling guild leader — permission bit `0x800 CanChangeVulnerabilityWindow` |
| how often | limited — *"There are no more changes left to the vulnerability window."* |

[LDB 50020/691, 100/14454, 100/245141412; the permission bit is the guild rank ladder's, see
[client-data-tables.md](../rdb/client-data-tables.md) § 1000518]

⭐ The permission bit and the window text corroborate each other from two unrelated sources: `0x800` was
recovered from the client's guild-management view (#799) with no siege context, and the window text names exactly
that right (*"Can Change Vulnerability Window"*, LDB 100/109915575).

## The battle

* **48 players per side**, and *"this is not limited to guild members. Guild leaders can also organize outside
  allies or hire mercenary players to fight for them."* (50020/693)
* The invader establishes a **Siege Camp** at one of several marked sites around the Battlekeep — right-click the
  marker stone, and with the necessary **construction materials and blueprints** build siege weapons and
  temporary structures. The defender right-clicks the Battlekeep itself to build anti-siege weaponry. (50020/693)
* ⚠️ **One weapon per camp**: *"This siege camp already has an active siege weapon!"* (50018/116771712), and a
  camp may only be raised while the target is vulnerable: *"You can only build a siege camp while the city is
  vulnerable."* (50018/116765824)
* **Resurrection is asymmetric** — *"fallen defenders will resurrect at the defenders' Battlekeep. Invading
  army-members will resurrect at the playfield's common resurrection point"*, so attackers pay a travel cost the
  defenders do not. (50020/693)
* **Scoring**: points for destroying opponents, enemy structures and siege weapons. (50020/694)
* ⭐ **Win condition**: *"an invading army must destroy the defending army's Keep — the defensive heart of the
  Battlekeep — within the 2 hour vulnerability window."* (50020/694)
* **During the battle, repair and construction are both refused**: *"You are not allowed to repair during siege
  battle."* / *"You are not allowed to build during siege battle."* (100/14456, 100/14457)

Structures carry their own siege roles: a wall *"offer[s] good defense during sieges. Standing on top of a wall
during a siege allows for line of sight outside the village"*, and a wall tower is where *"a ballista is
constructed on top of the wall towers to attack enemy trebuchets and catapults"* (100/219175, 100/219741).

## The eight Battlekeep sites

LDB category 270 names them, in one contiguous id run (3833647–3833677):

| | | | |
|---|---|---|---|
| Amut'Anun Siege Camp | Gardak Siege Camp | Burnheim Siege Camp | Sturmgarde Siege Camp |
| Oakenhome Siege Camp | Stonehammer Siege Camp | Caelnach Siege Camp | Fort Bane Siege Camp |

⛔ **None of the eight resolves against anything we hold** — no `Maps` row, no `MapRegions` row. They are Border
Kingdoms content that no capture in the corpus visits.

What we *do* hold is the three playfields they sit in — `Border Kingdoms, Cimmerian End` (11040), `Border
Kingdoms, Aquilonian End` (12070) and `Border to Kush` (13050) — which is the same set RDB 1000509 files under
PvP ruleset 6 and the same set the Massive-PvP board's `areaId` column carries. ⭐ The LDB agrees independently:
*"This is the area ID of the battlekeep."* (10002/9303006), so an `areaId` is a **playfield id**, from a third
source.

## The economy behind it

*"PvP resource nodes are available in the Border Kingdoms, Cimmerian End. These resources are used to build and
repair Battlekeeps… PvP resources have to be converted into building materials by architects before being used
to construct Battlekeeps."* — and harvesting one may flag you on the region map for competitors to rob
(50020/845).

⚠️ That is the concrete form of this subsystem's dependency on tradeskills: the Architect profession is not a
nice-to-have prerequisite, it is the only stated route from a harvested PvP resource to a building material.

Queue position for the right to attack is itself PvP-earned: *"there will be a queue of guilds waiting for the
opportunity to conquer a city. Guilds can manipulate this queue by earning PvP rewards elsewhere in Hyboria,
through minigames like Annihilation"* (50020/691) — which is the link to the minigame tier.

## What is implemented

⚠️ **The decision layer only, and nothing that touches the wire.** Not one siege or city message occurs in the
corpus, so every body that would carry these outcomes is unknown and none is sent. What is encoded is what the
server has to *decide*:

* **`GuildCityRules`** — the precondition chain (`CanBookCitySpot`, `CanBuild`, `CanApplyForBattlekeep`), the
  window arithmetic (`IsVulnerable`, `CanMoveVulnerabilityWindow`) and the two battle-time rules
  (`CanRaiseSiegeCamp`, `CanRepairOrBuildDuringBattle`). Every constant is a quoted string, cited in place.
* **`GuildCityRefusal`** — one member per real client refusal, each carrying the LDB id it comes from. So when
  the message that delivers one is finally captured, there is already a decision to attach it to.
* **`GuildCityState`** — the guild snapshot the rules read, passed in rather than fetched, because nothing
  persists a city yet. When something does, that is the shape it has to produce.

Gated by `GuildCityRulesTests`, which asserts the quoted numbers and — more usefully — the **boundaries**: 23 vs
24 members, tier 2 vs 3, the half-open two-hour window, and minute 15 vs 16 of the move grace.

⚠️ Two values are deliberately **not** hard-coded, because retail states the rule and not the number: how many
free city spots a realm has, and how many window changes a guild gets (*"There are no more changes left…"* names
the cap without giving it). Both are caller-supplied. `[V0: how many window changes a guild gets.]`

## What exists in our data today

* The **renown ladder** (RDB 1000521, 57 rows, levels 1–20) — decoded, not wired.
* The **governing forms and rank ladder** (RDB 1000517 / 1000518) — decoded and shipped, including the
  `CanChangeVulnerabilityWindow` bit this page needs.
* The **three Border Kingdoms playfields** — in `Maps`.
* City **plots as destructibles** (`Location Object - Walls/Towers/…`) — the captured world-side trace.

Open work tracked in Gitea #147.
