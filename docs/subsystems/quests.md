# Quests — goals, triggers and progression

How the server runs a quest: what a goal is, which server event advances it, and how a stage hands over to the
next. The wire format of the journal/objective packets is elsewhere — [`../protocol/`](../protocol/README.md)
and `QuestObjectiveRegion` / `QuestProgress` — and the per-chain content notes live in
[`tortage-intro-quest-ids.md`](tortage-intro-quest-ids.md).

## The model

| table | holds |
|---|---|
| `QuestDefinition` | one quest: journal/snapshot fields, `NextQuestId` (the chain successor), its `Goals` |
| `QuestGoalDefinition` | one objective: its type, its target, `RequiredCount`, the map marker |
| `CharacterQuest` / `CharacterQuestGoal` | per-player state: which quests are active/completed, and per-goal progress |

A quest completes when **every** goal reaches its `RequiredCount`; completing a stage supersedes it with
`NextQuestId`. `QuestProgressionService` owns all of that, and every trigger funnels through its one `Advance`.

## Goal types and what fires them

`QuestGoalType` selects which server event matches a goal; the wire output is identical for all of them.

| type | fired by | matched on |
|---|---|---|
| `Kill` | `CombatService` on an NPC death | the target **template**, `TargetName` as fallback |
| `LootFromNpc` | `LootService` when an item leaves a corpse bag | the bag's source NPC (template, name as fallback) **and** the item id |
| `TalkToNpc` | `ConversationService` on the NPC greeting | the target **template**, `TargetName` as fallback. **Solves** the goal (jumps to required) rather than +1, so a "deliver N to X" hand-in completes in one talk |
| `Escort` | the `UseObject` handler, on the object that plays the escortee's farewell | `TrailingRefId` — the escortee's template, resolved from `ConversationNode.TriggerWorldObjectId`, see below |
| `ReachArea` | the move handler, per accepted move | the goal's **region** if it has one, else a 40-unit radius around its map marker |
| `CollectItem` | **any item reaching the inventory** | `TrailingRefId == item template id` |
| `UseItem` | `UseInventoryItem`, on the right-click that applies the item's own effect | `TrailingRefId == item template id` |
| `UseWorldObject` | the `UseDynel` handler, on the **begin** phase of a world-object use | `TargetWorldObjectId == the used object`, plus `QuestItemId` **held** if it names one |

⚠️ **`UseItem` is not `CollectItem` with a different name, and the difference is load-bearing.** A collect is
satisfied by *holding* the item; a use, by *consuming* it. The one shipped `Use` goal — *"Use the Vial to Capture
Gas"* (11252, item 4349109 `Po-Sha Potency Vial`) — is an item the quest giver **hands over**, so counting its
arrival would complete the objective before the player did anything. `UntypedByArrival` in the importer excludes
the kind from the arrival rule for exactly that reason; this is the trigger on the other side of that exclusion
(#522).

### The goal carries its own KIND tag

`QuestGoalDefinition.Separator` — a captured field long treated as framing — is the goal's **verb**. It is the
client's own statement of what the objective asks for, as opposed to `GoalType`, which is our server-trigger
wiring. It was decoded against the goals whose semantics were independently established from captures, and each
value is now confirmed against a **second, independent source** — the goal's own objective text, readable
since #520. `--report ldb-quest-text` prints the two side by side for all 168 goals; the "verbs" column below is
that text's leading verb, counted:

| `Separator` | kind | goals | verbs in the text | evidence |
|---:|---|---:|---|---|
| `5` | `Craft` | 9 | **make** (9/9) | *"Make Visionwine"*, *"Make Ale of Auspice"* (quests 13141/13143/13145) |
| `9` | `RegionScoped` | 3 | enter, overcome, locate | goal 1448 → `Trappers Haven`; only 1 of the 3 is a reach |
| `10` | `Use` | 1 | use | *"Use the Vial to Capture Gas"* (11252) |
| `12` | `Kill` | 30 | kill, beat, cleanse, destroy, slaughter | Saddur, Scavenger Tuut (targets are `npc_template` / monster-category refs) |
| `20` | `Deliver` | 13 | deliver, give, bring | goal 3497 — *"Deliver 3 stone bricks to Turach"* |
| `22` | `Generic` | 60 | ~19 different verbs | the catch-all; carries a map pin but no single semantics |
| `23` | `Collect` | 44 | get, collect, gather, recover, remove, search, steal | goal 3495, and every large count (*Poisonous Gland* ×20, *Shadeleaf* ×10, a ×50) |
| `24` | `Investigate` | 1 | investigate | *"Investigate the Ancient Gateway"* (8278) |
| `25` | `DestroyObject` | 9 | destroy | *"Destroy Bowl of Set"* (×7 in quest 13473), *"Destroy the Wolves' Saddles"* (count 2) |

⚠️ **There is no kind `0`.** This table used to open with one — `Untargeted`, 9 goals, "about the *reference*,
not the verb, hence the spread" across speak/talk/free/get/give/return/unlock. It was the **un-harvested
default** (#548): those goals are exactly the ones the corpus only ever full-updates, and the importer's
full-update path never set `Separator` at all. The wire sends `0` in neither source — across all 158 snapshot
goals and all 111 full-update goals the tag takes only the values above — and once the full-update's tag is
lifted, not one shipped row carries `0`. The nine scattered into `Generic`, `Collect` and `Deliver`, where
their verbs read as those kinds' do.

The clincher that it describes the **verb and not the target**: quest 12371's two goals reference the *same*
item id with the *same* count and still differ — `23` for "collect 3 quarry bricks", `20` for "deliver 3 to
Turach". Values seen in the corpus only; the gaps are unmapped rather than absent. The enum is `QuestGoalKind`.

⚠️ The cross-check **corrected three readings** that had been taken from the target reference rather than the
verb, which is exactly what it is for:

- `5` was *"acquire one named item"* — read off the item names (*Visionwine*, *Musewater*). The verb is **Make**,
  so these are crafting objectives.
- `9` was *"reach a named region"*. Two of its three goals are not reaches (*"Overcome the Emperor's Manifested
  Nightmares"*, *"Locate the Red-Hand Leader"*), so the region **scopes** the objective as often as it is one.
- `22` was *"reach a point"*. Its 52 goals span ~19 verbs including vanquish, talk, escort and kill — the marker
  is real, the "reach" is not. It is the **generic** kind.

And it found `10`, `24` and `25` — three values the enum did not have at all.

### A goal's target is a TYPED LDB reference

`TrailingCategory` says what kind of thing `TrailingRefId` points at — the client's own category table names them
(see [`../rdb/ldb-string-index.md`](../rdb/ldb-string-index.md)):

| category | `TrailingRefId` is | goals |
|---|---|---:|
| `50200` `item.name` | an item template id | 74 |
| `51000` `npc_template.name` | an NPC template id | 24 |
| `51100` `categorytree.description` | a category | 6 |
| `51200` `bounded_areas.bounded_area` | a **region name id** | 3 |

All 107 resolve. That reference is the target, so `TargetName` is mostly a label: kill goals already match on the
template id and only fall back to the name.

### …and every goal type now matches on a template first

`QuestGoalDefinition.TargetTemplateId` is the id half of the target, resolved at import
(`QuestGoalWiring.ResolveGoalTargetTemplates`) so that the talk and loot paths match the way the kill path always has.
A display name is **ambiguous in the world** — `Saddur` is two templates and `Halen van Dummy` is 38 — and that
was a live defect, not a theoretical one: the quest-gate map keyed on the string, so flagging the Tortage Island
Saddur (9500) also stamped `GateQuestIds` + `NoRespawn` onto the **Stygian Galley** Saddur (2993), hiding an NPC
on the intro playfield from every player not carrying a Tortage Island quest they cannot have yet.

Three sources, strongest first:

| # | source | goals |
|---|---|---:|
| 1 | the goal's own `51000` reference — the client states the template | 6 |
| 2 | `TargetName` matching exactly one `NpcTemplates` row | 271 |
| 3 | `TargetName` + the goal's own `MapId`, when several rows carry the name | 1 |

Rule 3 is the interesting one. `MapId` cannot be a general match key — it is a **marker pin** and is NULL on 809
of the 864 goals that name a target — but where it *is* set it separates same-named templates cleanly, because
they stand on different maps. Goal 7134 *"Find the gate key"* names `Saddur`, carries no reference, and both
Saddurs are candidates until its `MapId` 4010 picks the Tortage Island one. Its sibling goal 3490 resolves to the
**same** template by rule 1, which is the cross-check that the map reading is right.

Nothing is guessed: an ambiguity the three rules cannot settle is left NULL and reported, because a wrong template
matches the wrong creature silently while a missing one just falls back to the name. One goal is in that state —
4771 *"Talk to Alyssa"*, two Alyssas and no map on the goal to separate them.

⚠️ **`TargetName` is not deprecated by this and does not wither away.** 586 goals keep it as their only key,
because they name NPCs on maps that have not been imported yet and an FK cannot hold an id with no row — that
includes 18 whose own `51000` reference we can read perfectly well. It is also the only key a *"kill N of
&lt;group&gt;"* goal can use, since a group has no single template. The rule only assigns an id when the name
resolves **unambiguously**, so it can never narrow a group match.

The asymmetry in `QuestProgress.TargetsNpc` is the point: a goal that HAS a template is matched on the id **alone**
and never gets a second chance on the string. Falling through to a name compare is exactly how the galley Saddur
got caught.

**The `51000` half fills `TargetName` mechanically** (`RdbQuestGoalTargetImporter`), and which source it takes
from is load-bearing. `LDB[51000]` is keyed by template id, so the goal names its target twice — as an id and as a
plaintext string — and the two id spaces line up: all 1,018 `NpcTemplates` have an `LDB[51000]` entry and 991
agree exactly with the name our captures recorded. The **27 that differ are all rows captured under the shared
placeholder `Halen van Dummy`**, where the LDB carries the real name.

`TargetName` is a *match key*, compared against the live entity's name — which is the captured one. So the
**catalog wins wherever it exists**, and the LDB fills only the templates we do not hold (18 of the 24 goals,
inert until their map is imported). Writing `Beggar` where the wire says `Halen van Dummy` would look resolved
and silently never match.

The other three categories are deliberately not written into it: an item name in a column the kill matcher reads
as an NPC name makes it hunt a mob called *Stolen Goods*; `51100` resolves to editor **group** names (`Watch`,
`Undeads`, `Posha's men`) matching no NPC; and `51200` is a region, whose geometry is already `RegionId`.

`--report quest-goal-targets` is the evidence path — every goal's trailing reference resolved, with the
per-category agreement rate against the catalogs the runtime actually matches against.

**The goal's own objective TEXT is readable** (as of #520). Every goal's `ObjectiveCategory` is `50304`
(`quest_goal_templates.goal_name`) with `ObjectiveRefId == GoalId`. That category is one of eleven **encrypted**
LDB categories, and the key is the `key=` attribute the server sends alongside the reference — stored on the goal
as `ObjectiveKey`. See [`../rdb/ldb-string-index.md`](../rdb/ldb-string-index.md) for the cipher.

So the client's own words are now a source for goal wiring, alongside the kind tag and the captures. They agree:
goal 1448 is *"Enter the jungle"* (`RegionScoped`), 3490 *"Kill slavemaster Saddur"* (`Kill`), 3495 *"Get 3 stone
bricks"* (`Collect`), 3497 *"Deliver 3 stone bricks to Turach"* (`Deliver`).

### …and the same sentence is in PLAINTEXT, for 43× more goals

⚠️ That encrypted category is the **expensive** copy. The client's quest catalog (RDB 1000104) carries the same
sentence as `QuestGoal.name`, in plaintext, and it is already a column on every row — `QuestGoalDefinition.Name`:

| source | goals | cost |
|---|---:|---|
| LDB `50304`, encrypted | 170 | a per-string TEA key only a **capture** yields, plus an `Content:RdbDir` |
| `QuestGoal.name`, plaintext → `Name` | **7,299** | none |

`RdbQuestTextImporter` had been reporting **7,147 goals "unreadable"**. They were not unreadable; they were being
read from the wrong copy. Reading `Name` instead types **753** goals `TalkToNpc` — 201 naming an NPC we hold, 53
of those with captured dialogue, so playable now. The patterns and the reading live on `QuestObjectiveText` and
both importers share them, so the two cannot drift.

⚠️ **A DELIVERY is only typed when the sentence resolves BOTH ends**, and the deliveries are the larger set —
*Give* 656, *Hand* 504, *Return* 385, *Deliver* 309, against *Talk* 473 / *Speak* 242 / *Report* 225. A delivery
hands an item **over**, so it must be gated on the player holding it; typing one `TalkToNpc` with nothing to gate
on completes the quest on a bare greeting with the goods still in the shop.

So `QuestProgress.IsDelivery` reads **two shapes**, because the evidence arrives two ways:

| | how the item is named |
|---|---|
| **captured** | `Separator` = `Deliver` + a `50200` trailing reference — the 170 goals a capture has shown |
| **resolved** | `TalkToNpc` + `QuestItemId` — the item read out of the goal's own sentence and written to the server-side column |

The resolved shape is why the catalog goals are reachable at all: they carry no kind tag and no reference, only
words. *"Give the Crate of Steel to Turach"* → `Items` 31055, and the goal is then gated exactly as tightly as a
captured `Deliver`. **Nine** qualify; five of their recipients have captured dialogue, so those are playable now.
The 1,846 whose recipient or item does not resolve stay `None`.

⚠️ It must be `TalkToNpc` **+** `QuestItemId`, never the column alone — a `LootFromNpc` goal names its item in the
same column, and gating a loot goal on already carrying the item makes it uncompletable.

⚠️ The hand-over phrase must be the **whole noun phrase** between the verb and the closing *"to &lt;recipient&gt;"*,
matched exactly. Accepting any catalog name that merely appears in the sentence resolves 15 goals instead of 9
and about 40% are junk: *"Give the Veteran's Sword to Daorgan"*, *"…Bow to Adha"* and *"…Shield to Sian"* all
collapse onto one item named *Veteran*, and *"Give Ingots of Blue Silver to Jocund"* resolves to *Silver*.

**The two sources agree wherever both exist**, which is what makes the sentence trustworthy: across the 12
captured `Deliver` goals the phrase resolves 6 and matches the wire's item id on **all 6, with zero
disagreements**. The other 6 name something the catalog does not hold under that spelling (*"3 stone bricks"*,
*"the Remedy"*, *"the Letter"*), and the two *Cimmerian Furs* goals resolve to a name **two** items share — which
the wire separates and the rule refuses, as it refuses every ambiguous name. A goal whose wire record already
names the item is left alone: the reference is direct evidence where the sentence is inferred.

`SeedContentTests.DeliveryObjectives_AreOnlyTypedWhenTheHandInCanBeGated` holds the line.

### Kill goals name their creature; collect goals cannot name their item

A catalog-only **kill** goal has `TrailingRefId` 0 and no `TargetName`, so `MatchingKillGoal` can match it by
neither id nor name. Its sentence names the creature, and that name is exactly what the matcher's fallback branch
wants — it is compared against the **live entity's name**, which is what a *"kill N of &lt;group&gt;"* goal needs
anyway, since a group has no single template id. **45** goals qualify and **43** have live spawns.

⚠️ The name must resolve to **exactly one** `NpcTemplates` row. That is the safety property, not politeness about
coverage: an unresolvable name would be stored and never match anything — inert dressed up as wired — and a name
several templates share does not say which creature the quest meant (three distinct *Scavenger Lackey* templates
is the known case). Measured against the 24 goals whose target the wire established, the extraction reproduces 17
strings exactly and, of those that also resolve to a template, is right **4 times and wrong none**. The seven it
cannot reproduce all yield nothing rather than a guess: a **role** (*"Kill the Chief"* → Tribal Chief Rak'tunab),
a **title** the prose adds or drops (*"Kill slavemaster Saddur"* → Saddur, *"Kill Zhu-la"* → Heiromonk Zhu-La),
and one editorial rename (*"Destroy Po-Sha's Source of Corruption"* → Ravenous Fiend).

⚠️ **`GatesTargetVisibility` is never set by the rule.** These are population mobs, and gating one hides every
same-named NPC from every player not on the quest and forces `NoRespawn`.

**The COLLECT half is deliberately NOT wired, and the reason is worth keeping.** The same extraction resolves 33
un-typed collect goals, and against the 42 the wire already answers it agrees 9 times and **disagrees once** —
goal 5795 *"Get Wolf Pelts"*. The wire wants item **347507**, which our catalog does not hold; the only item named
*Wolf Pelt* is **3206185**, a different template. An item NAME belongs to several template ids across the real
game and we hold an arbitrary subset, so resolving a collect goal's item from prose points at the wrong id with
no way to tell — and a collect goal gated on an item the player can never obtain is silently uncompletable. The
wire reference is the only reliable id for an item. (See the `same_name_different_number_footgun` note.)

### The catalog is the ONLY source for a goal's required AMOUNT

⚠️ `RequiredCount` otherwise comes only from the wire goal record, so a goal no capture has shown sits at **0** —
and the runtime reads 0 as **one** (`Math.Max(RequiredCount, 1)`). **58** shipped goals were in that state, 28 of
them asking for more than one: *"Hand in 20 Yew"* completed on the first log, silently, with the journal showing
1/1. The sentence states it, in numerals or the words *one*..*ten*, and `QuestObjectiveText.StatedCount` reads it.

⚠️ Scoped to **catalog-only** goals (`Separator` null) plus `RequiredCount == 0`, and that guard is what makes
writing a wire field safe: `RequiredCount` is written straight back out in the byte-exact @609 objective region.
Measured — none of the 58 rides the login snapshot, and none belongs to a quest with *any* wire goal, so no region
we build can contain one. The two goals the wire *does* carry (3495 *"Get 3 stone bricks"*, 3497 *"Deliver 3 stone
bricks to Turach"*) both say 3 and were both captured as 3, which is what makes the reading credible; the guard is
what makes it safe. `SeedContentTests.GoalsThatStateAnAmount_CarryIt` checks both halves.

### Wiring rules run in order of EVIDENCE STRENGTH

Every wiring step is bootstrap-only, so **whoever types a goal first wins and the rest skip it**. That makes the
order in `QuestGoalWiring.WireQuests` load-bearing rather than cosmetic — it has produced wrong triggers twice:

1. **a human decided** — the `Seed*` steps: editorial calls (`GatesTargetVisibility`, the Nadini stand-in) and
   everything no source states (`QuestItemId`, chain successors)
2. **the goal's own VERB** — its plaintext objective sentence, then the wire kind tag
3. **its TYPED REFERENCE** — *"the trailing ref is an item, so this is a gather"*
4. **its MAP MARKER** — the weakest: a pin sits on whatever the objective is *about*, which is as readily the NPC
   to talk to as the place to walk to

Both regressions were the order being wrong. #522: the marker rule beat the sentence, turning seven talk goals
into walk-ups and closing Casilda's escort the instant it began. #704: the sentence rule beat the Nadini
override, whose whole point is that she has **no imported dialogue**, so a greeting can never fire and only a
reach can complete the goal.

#### …and a marker can be a PLACEHOLDER, which is weaker still

⭐ A reach goal's whole target IS its pin, so an unset pin makes it solvable by standing in the corner of the map.
Two coordinates in the shipped data are not places: **`(50, 50)` appears on NINE different playfields** (6110, 6120,
6121, 6122, 6130, 6212, 6310, 6311, 6312) and **`(1, 1)` on three** (2095, 5201, 6400). Every *other* repeated marker
is confined to one map — `(781, 348)` ×6 all on 3044, `(913, 718)` ×2 both on 4020 — which is what a genuinely shared
location looks like. A place cannot be on nine playfields at once.

Two goals had been typed `ReachArea` off one: 11446 *"Win the Battle of Blood Ravine"* (5201 @ 1/1) and 11951
*"Extinguish the Pods"* (6212 @ 50/50). Neither is a reach in the first place — both come from kind `22` `Generic`,
the catch-all whose 52 goals span nineteen verbs — so the marker was the only thing typing them, and it was not a
marker. Both are `None` now.

`QuestProgress.HasReachablePin` is the rule, and it is asked in **two** places: the importer's typing passes, so a
placeholder never types a reach again, and `QuestProgressionService.LoadReachGoals`, so a row that gets there anyway
is not tracked. A goal carrying the client's real trigger REGION (#521) is exempt — the volume is the target then,
and the marker is only a map pin.

## Collect goals do not care how the item arrives

A gather objective is satisfied by **holding** the item. The client's data says which item and how many, and
says nothing about the source — the same objective can be a mob drop, a world pickup, a vendor purchase or a
reward.

So the trigger is the **grant**, not the source: `GrantItemToBackpack` is the single choke point every item
passes through on its way into a player's inventory, and it calls
`QuestProgressionService.AdvanceCollectGoals`. A granted stack advances by its whole count.

That deliberately replaces per-source wiring. The alternative was to type each goal `LootFromNpc` vs
`CollectItem` by guessing from the item's name — the set mixes mob drops (*Mangy Yeti Pelt*, *Hyena Testicles*),
world pickups (*Quarry Stonebrick*) and consumables (*Pink Salt*, *Visionwine*) — and a wrong guess makes the
quest silently un-completable, which reads in-game as an engine bug.

The quest-pickup node path (a `QuestItemEffect` dynel) therefore only **resolves** which item to hand over
(`ResolveCollectPickupItem`) and lets the grant do the counting; advancing in both places double-counted.

⚠️ An item the `Items` catalog does not hold cannot be granted at all (`GrantItemToBackpack` needs the row), so
those objectives still cannot complete — **51 of the 74** referenced items are uncatalogued. They ship in the RDB
but their templates are `RDBH`-encrypted with a per-item key only a capture yields, so this is a catalog-coverage
gap, not a quest bug.

## A goal update has three shapes, and the QUEST picks which one

What a goal move puts on the wire is decided by the state of the **quest**, not by the goal or by what triggered
it. There are exactly three shapes across the corpus:

| the goal | what retail sends |
|---|---|
| moved, still short of its count | `QuestGoal(Progress)` |
| met, and the quest still has an unfinished goal | `QuestGoal(Satisfied)`, `QuestGoal(Advanced)` |
| met, and that was the last one | `QuestGoal(Satisfied)`, `QuestIIR` 2 (active), `QuestIIR` 4 (completed) |

Two things follow that are easy to get wrong in either direction.

**`Advanced` closes the goal being left behind — it does not open the next one.** It rides in the *same frame*
as that goal's own `Satisfied`; the successor's `Satisfied` lands whenever the successor is actually solved,
which in the corpus is 34 s to 8 minutes later. All ten sightings are that pair, on four quests:
12371 g3495→g3497, 10238 g1448→g3490, 12369 g7134→g3492 and 10287 g400→g7363.

**A `QuestIIR` means the quest changed state, so it appears only on the third shape.** The fourteen captured
`Progress`/`Advanced` frames carry none — an active-refresh after every objective tick is a packet retail does
not send.

The completing packet names the goal that **just** completed. On a single-goal quest that is also the quest's
head goal, which is why naming `QuestDefinition.GoalId` looked right for so long; on a multi-goal quest it is
not (12371 closes on 3497 while its head goal is 3495).

### The verb decides the trigger — obtaining an item is not always doing the objective

A goal's `Separator` is its VERB (`QuestGoalKind`), and its trailing reference is often an ITEM. Typing every
item-referencing goal `CollectItem` is wrong for five of those verbs, because the objective is not satisfied by
the item arriving in the backpack:

| kind | goals | why a collect is wrong |
|---|---:|---|
| `20` `Deliver` | 13 | the item is handed OVER — obtaining it starts the objective |
| `5` `Craft` | 9 | *"Make Visionwine"* — a bought Visionwine is not a made one |
| `25` `DestroyObject` | 9 | destroying an object does not put it in your bag, so it can only ever be wrong |
| `10` `Use` | 1 | *"Use the Vial to Capture Gas"* |
| `24` `Investigate` | 1 | typed `ReachArea` instead, below |

⚠️ These twenty were recorded on Gitea #522 as *"inert — nothing advances them"*. **They were not.** Each carried
a real `TrailingRefId` and was typed `CollectItem`, so each would advance the moment its item reached the
backpack. Most were harmless only because the item is absent from our catalog — coverage, not design — but
`Use the Vial to Capture Gas` was live: its vial (4349109) IS in the catalog, so picking it up completed the
objective without using it. `TypeItemGoalsFromTrailingCategory` now refuses those verbs *and* resets a row a
previous run mistyped, so they read as un-wired (visibly stuck) rather than silently completing.

**`Investigate` is the one of the four that could be typed.** Goal 8278 *"Investigate the Ancient Gateway"*
(quest 15051) carries a real map marker (2090 @ 928/1011), so it is a reach. ⚠️ Retail may want an *interaction*
with the gateway rather than proximity; we hold no world object at that marker to interact with — map 2090's
`WorldObject` rows are all worn NPC gear — so a reach is the only trigger the data supports, and it errs toward
completing on something a player cannot do without going there. `Craft`, `Use` and `DestroyObject` still have no
trigger: `DestroyObject`'s nine goals sit on maps 3034/3044/3045/6130, for which we hold **no world objects at
all**, so it is blocked on coverage rather than on the trigger.

### "No trigger" is a value — `QuestGoalType.None`, and `Kill` is not the default

⚠️ **`QuestGoalType.Kill` used to be enum value `0`, so it was also the un-typed default.** A goal nobody had
wired claimed to be a kill goal, and **7,148** catalog goals — no verb, no target, no trigger — sat there. Every
guard in `QuestProgress.MatchingKillGoal` is a patch over one way that misfired:

- **`Partial` goals excluded** — half-filled rows at the default matched by id on `TrailingRefId 0`;
- **a template id of `0` is "unknown"** — the one caller passes `WorldEntity.NpcTemplateId`, a non-nullable
  `uint` that is `0` for anything with no template, so killing such a creature advanced an arbitrary goal of
  **every** open quest;
- **and the twenty above**, which the fix for the mistyping had to park *somewhere* — and "somewhere" was `Kill`.

So the default is now **`None`** — no server trigger, the goal is stored, rendered and counted and nothing
advances it. That is the same correction #548 made to `Separator`'s kind `0`, and it is a rename rather than a
re-typing: `0` always meant "nobody has typed this". `Kill` moved to `6`, which costs one column on ~30 rows
instead of rewriting 7,148 that were never kills.

A goal is a kill goal because the **client's verb** says so (`Separator` = `QuestGoalKind.Kill`), which
`QuestGoalWiring.TypeKillGoalsFromKind` is what applies. All 30 are typed, and they split two ways — the verb decides,
not whether the target resolves:

| target reference | goals | matched by |
|---|---:|---|
| `51000` `npc_template.name` | 24 | `TrailingRefId` against the killed entity's template id |
| `51100` `categorytree.description` | 6 | nothing yet — the id is an editor **group** (*"Kill Atzel's Watch"*, *"Kill Undeads in Xibaluku"*) resolving to no NPC we hold |

⚠️ Those six are also why `MatchingKillGoal` matches by id **only when the goal's own typed reference is an NPC
template** (category `51000`, or `0` for a hand-authored goal with no typed reference). A category id runs
246,944 .. 10,367,287 — a different id space — and comparing one to a template id is safe only until an id
collides, which is a matter of NPC coverage rather than design. `SeedContentTests
.KillGoals_AreTypedFromTheClientsOwnVerb` gates both halves.

`None` is also the honest type for a verb we cannot yet trigger. `Craft` (9), `Use` (1) and `DestroyObject` (9)
land there rather than being bent into the nearest trigger that would fire early.

### Goal ORDER is `Phase`, not `Sequence`

⚠️ A goal row carries two numbers that both look like an index, and only one of them orders anything.

- **`Sequence` is a WIRE value** — the goal's ordinal `N`, written straight back out by `QuestObjectiveRegions`
  and byte-exact verified against captures. Five shipped quests carry `N` = **1 on every goal** (14020, 16004,
  16183, 16888, 17350) because their objectives are **parallel** — *"collect all seven faction documents"*,
  *"kill all three"*, *"use all five stones on the standing stone"* — and retail has no order to state.
  Renumbering them 1..n to make them sortable rewrites bytes retail sent;
  `CompletedQuestJournalGenerationTests` fails on it immediately, and
  `SeedContentTests.ParallelGoalQuests_KeepTheirCapturedOrdinal` pins the five so it cannot be tried again.
- **`Phase` is the play order** — `QuestGoal.phase` from the client's own quest catalog (RDB 1000104). It is also
  the *only* order available for the **7,147 of 7,317** goals that came from that catalog and were never on the
  wire, so carry no `Sequence` at all.

The corpus settles which is which: retail's `Advanced` hand-off is sent only on a goal that has a **successor**,
and in every captured multi-goal quest the goal that takes it is the lower **phase**. They disagree on exactly
one shipped quest — 12369 "The Acheronian Gate", where g7134 is phase 1 but `Sequence` 2 — and it is g7134 that
takes the `Advanced`. `QuestProgress.InPlayOrder` is how the runtime asks; nothing else may sort goals.

### Goals recovered from a second full-update record, and what makes one PARTIAL

`QuestGoalDefinition.Partial` marks a goal recovered from the second 50304 record of its `QuestFullUpdate` -
identity and objective text only, with no verb, target, count or marker block. It is what
`AllGoalsComplete`, `MatchingKillGoal` and `QuestObjectiveRegion.Build` skip on, so a partial goal is INVISIBLE
in-client: its quest presents as if it had one fewer objective.

Two rows were in that state, both on quests granted and completed between snapshots so no login ever HELD them.
One is now typed:

- **10287 g7363 "Ask Tina about Nadini"** — typed from its own objective TEXT. The rule that already wires
  "Talk/Speak/Report to X" did not recognise `Ask X about Y`, because the name follows the verb instead of a
  to/with. Over all 7,317 catalog goals exactly THREE objectives begin with "Ask", so the shape is unambiguous;
  the goal is now `TalkToNpc` on **Tina** (NpcTemplate 2805, who has captured dialogue), count 1, and the quest
  presents with both of its objectives.
- **12369 g7134 "Find the gate key"** — now typed, and the fix turned out to be a *swap* rather than a new
  inference. The key **is** `Acheronian Gate Key` (item 193186), and the seed already knew that — it had simply
  hung it on the **wrong goal**. `SeedShipwreckedCityChain` wired g3492 as `LootFromNpc "Saddur"` for 193186 and
  dismissed g7134 as *"retail's 2nd goal 'unlock the gate' wasn't imported"*, but the imported names say the
  opposite: **3492 IS "Unlock the Acheronian gate" and 7134 IS "Find the gate key"**.

  What misled it is the marker. It read 3492's `(524, 755)` as Saddur's spot — Saddur spawns at `(521, 757)` —
  but the world object **"Acheronian Gate" (975369919) stands at `(523.4, 754.7)`, ~3 u away**, so the marker
  cannot separate them and the goal's own NAME is what settles it. (g7134's marker `(574, 944)` lands on
  "Acheronian Chest" `(573.6, 946.7)`, the key's other end.) So:

  - **g7134** = `LootFromNpc` **Saddur** → 193186, count 1 — the objective the capture actually records
    (Saddur dies fr11942 → corpse bag fr11947 → key added fr12023).
  - **g3492** = `UseWorldObject` on the **Acheronian Gate** (975369919), with the key (193186) required in hand.

  ⚠️ **g3492 was `ReachArea` at its own marker until #527, and that was wrong in play, not merely approximate.**
  The gate object *stands on* the marker — which is what made the reach look right — so the objective completed
  the moment the player walked up to it: before the key was looted, and without the gate ever being touched.
  Reported in-client 2026-08-16 as "auto-completes on proximity". Retail's order settles it, and there is no
  ambiguity in it (`Hoxxling_mage-heraldofxotli_login_gameplay`): fr12193 the player *selects* the gate dynel,
  fr12196 sends the **use**, fr12204 g3492 ticks 1/1, fr12205 the quest completes.

  ⭐ The general lesson, since the same marker reasoning is available for every goal: **"the marker is where the
  target is" makes a reach look right for any objective whose target is a thing you must interact with.** A reach
  is the correct type only when *arriving* is the objective — stage III's g3498 "Enter the City of Tortage"
  genuinely is one. Where the target is a door, a gate or a lever, the trigger is the use.

  The key is required but **not consumed**: `QuestProgress.RequiredHeldItemId` is deliberately separate from
  `DeliveryItemId`, because a hand-in gives its item away and an unlock does not. Retail does remove the key one
  frame after the quest completes (fr12206, quest container `0x71` slot 0) — but that lands *after* the quest's
  own completion, so it reads as the quest clearing its container rather than the gate eating the key. Which of
  the two it is is open, tracked in Gitea #843.

  No goal is `Partial` any more, which `SeedContentTests.NoQuestGoal_IsStillPartial` now keeps true.

*Sources:* `xxx_mage-necromancer_login_play2_tortage_city_first-quests` fr4247/4815/4872/5488,
`Hoxxling_mage-heraldofxotli_login_gameplay2` fr4310/4367/4447/5102 and `…_login_gameplay` fr3633/3634/11942,
`xxx_mage-{demo,necromancer}_login_play*`, `loot-drag-1538` fr2843. Gated by `QuestGoalHandoffTests`.

## Turn-in: the text goes out first, the state follows it

There is **no separate turn-in or reward node** to look for. Retail's turn-in screen *is* the in-progress
variant, and what makes it a turn-in is an inline `{resolvegoal <4CC>}` directive in its own text. So the
completion is a consequence of the line the player was just shown, and it is sent in that order:

```
KnubotOpenChatWindow
KnubotAppendText   "{camera size=5 type=1 tilt=5}…{resolvegoal BJGY}{delay 1}"
QuestGoal  5745 -> Satisfied
QuestIIR   13480 state 2 (active)
QuestJournal
QuestIIR   13480 state 4 (completed)
QuestFullUpdate    "Casilda"        ← the next stage, granted in the same burst
KnubotAnswerList                    ← ~1.2 s later, the {delay 1}
```

Two captures record this independently, on different characters and different playfields —
`Stihetfv_…_compeltefirstquest` at t=57.3 and `Blablabla_…_questcomplete` at t=373.3 — with the same record
order both times. The **grant** side has the same shape (`…questcomplete` t=334.5: `AppendText` →
`MainQuestUpdate` → `QuestFullUpdate`), so *text precedes state* is the rule for the whole dialogue rather
than a completion quirk.

That ordering is load-bearing, not cosmetic. `ConversationService` picks the screen variant by evaluating the
player's **live** quest state, so completing before serving flips the quest to `Completed` and the pick then
matches the `HasCompletedQuest` variant — handing the player the post-quest *"already done"* line instead of
the turn-in line. `TryTurnInQuests` and `AdvanceTalkGoals` therefore both run after `ServeScreen`.

⚠️ **Not every quest ends at a giver.** In the same captures, 13479 (*The Scavenger*) and 13563 (*The
Scavenger's Key*) complete on the **kill** and the **loot** with no dialogue at all — goal satisfied, `QuestIIR`
2 → 4, and the next stage granted in the same burst. A chain stage supersedes where its goal is met; only the
talk-goal stage (13480) completes in a conversation.

## The marker over a giver's head: silver is "not yet", never "in progress"

`DispatchSetQuestMarker` carries one state byte, and `QuestMarkerResolver` decides it: `0x00` none, `0x01`
silver `!`, `0x02` gold `!`, `0x04` gold `?`.

`Blablatest_mage-necromancer-create_casilda_quest_first_stage` walks Casilda's whole chain on a fresh
character and pins four of the five states on one NPC (handle `0x315cc`):

| t | what just happened | marker |
|---|---|---|
| — | 13479 *The Scavenger* unclaimed | `0x02` gold `!` |
| 191.7 → 191.9 | `QuestFullUpdate` grants 13479 | `0x00` **none** |
| 213.8 | Scavenger Tuut killed; 13479 completes, 13563 granted | `0x00` |
| 223.3 | key looted; 13480 makes *talk to Casilda* the goal | `0x04` gold `?` |
| 230.1 | 13480 handed in, 12407 granted | `0x00` |

That walk reproduces on six other characters (`Blablabla_soldier-guardian_…`, `Hoxxling_…_login_gameplay`,
`Strie_rogue-assassin_login_randomActions`, `xxx_mage-demo_login_play_beach…`, `xxx_…_play1_beach…`,
`Ranochar_…_get-casilda-quest`), and across all 275 captures Casilda is `0x00` ×178 / `0x02` ×170 / `0x04` ×21
and **never** `0x01`.

⭐ **The marker describes the NEXT quest this NPC can GRANT you — and a giver is not one quest.** Turach hands
out two, Laranga two, Cramaleico two. So the state is a fold over all of a giver's grants:

- one of theirs is held with its objective done → `0x04`, turn it in here;
- some grant is still outstanding (neither held nor completed) → `0x02` if its line has reached it, `0x01` if not;
- nothing outstanding at all → **no marker**. The giver is spent.

That last clause is what a carried quest hits, and it is the whole difference between Casilda and Alyssa.
Casilda grants exactly **one** quest, so accepting it leaves her with nothing to offer and she goes blank.
Alyssa sits on a four-step line ending in *"Report back to Alyssa"* and goes **silver eight frames after
granting hers** (`Hoxxling_…_login_gameplay2`: grant at frame 9603, `0x01` at frame 9611). Both are "carrying
the giver's quest"; the marker differs because what is *left to grant* differs.

⚠️ **Silver is therefore not simply "your level is too low"**, which is how the community documents the grey
`!`, **nor simply "the chain has not reached them"**. It is *"they have one you cannot take yet"*, and the
captures do not separate the two reasons — Alyssa goes gold → silver within 58 seconds, and a level cannot
drop. What is still unexplained: Turach and Laranga stay silver even past their last harvested grant, which
fits an unharvested further quest but is not proven. Tracked in Gitea #848.

⛔ **The quest's `MapId` does not gate the marker.** It is the objective's marker *pin* — where the quest sends
you — not where its giver stands, and the two differ for **15 of the 28 giver quests** in the shipped data:
Turach and Laranga stand on 4020 while their quests pin 4010, Alyssa on 4020 against 10800, and likewise Caor,
Oracle Vateesa, Hadrathus, Kaen, Cadarn, Borcha, Tanner Chael, Serving Wench, Arias and Valkyrie Hylda. The
captures show retail marking those NPCs, so gating on it suppressed markers that should be sent. The gate came
from #244, where it doubled as the thing stopping a same-*named* NPC world-wide from wearing the marker
(`Halen van Dummy` is the internal name of 27 unrelated templates); that job belongs to the template-keyed
lookup since #646.

Gated by `QuestMarkerStateTests`; the line-not-reached half by `QuestMarkerLineTests`.

## The Completed tab's body text is LDB slot 50303, and retail left half of it unwritten

The Completed tab renders a *Region → Area → quest* tree, and each completed quest carries exactly **two**
strings — the pair the completed-quest journal region ships (`CompletedQuestJournalRegionCodec`):

| record | category | slot | what it is |
|---|---|---|---|
| `rec[0]` | 50301 | `Title` | the quest name — *"The Scavenger"* |
| `rec[1]` | 50303 | `ListSubLine` | the **past-tense retrospective** shown under it |

The 50303 text is written specifically as completed-tab prose and reads nothing like the active-quest
description: 50302 `Summary` is present tense (*"I must take the key from the scavenger I fought…"*) while 50303
is past tense (*"As my journey started on the beach of Tortage, I met the enslaved woman Casilda. I freed her
from her chains and captor, and escorted her to the city of Tortage."*).

⚠️ **A completed quest showing only its title is very often correct, not a bug.** Of the **1,974** quests
carrying a 50303 slot, **1,064 (54%) are unfilled developer placeholders** in the client's own LDB:

| value | quests |
|---|---|
| `<write the task solved text here>` | 1,021 |
| `<write the mission solved text here>` | 35 |
| `<write the task description here>` | 5 |
| real prose | 910 |

The Casilda destiny chain is a clean illustration, and the reason this reads as a server bug when it is not:
its three intermediate steps — 13479 *The Scavenger*, 13563 *The Scavenger's Key*, 13480 *Freeing the Captured*
— are **all placeholders**, while the stage that concludes the chain, 12407 *Casilda*, has the real paragraph
quoted above. So the tab looks empty for exactly as long as the player is inside the chain.

**Our bytes are retail's.** Dumped from `Hakotep_hox_login_gameplay0` frame 76 and rebuilt from the database,
both records match key-for-key on all three quests, and `CompletedQuestJournalGenerationTests
.CapturedRegions_RebuildFromTheDatabase_ByteExact` holds that over the whole corpus. The remoteformat `key` is
**not** a request token either: it appears only in the four s2c snapshot packets of that session and is never
sent back, so the client resolves 50301/50303 locally out of its own LDB. There is no reply for the server to
owe.

`--report ldb-quest-text <questId>` prints every slot for a quest, which is how to settle "should there be text
here" before treating a blank as a defect.

## ReachArea: the marker is a pin, the region is the trigger

A `ReachArea` goal carries a map marker, and the marker is what the client draws — not what retail triggers on.
Where the goal has a `51200` reference the real volume is recoverable and imported as a `MapRegion`
(see [`../rdb/bounded-areas-1000053.md`](../rdb/bounded-areas-1000053.md)); the runtime then tests that polygon
prism. Goals without one keep the 40-unit marker radius.

Worked example — the beach starter, quest 10238 goal 1448: `TrailingRefId 180` → `LDB[51200][180]` =
`"Trappers Haven"` → a 7-vertex region on Tortage Island containing the goal's own marker.

⚠️ A `51200` reference does **not** by itself make a goal a reach objective. Goal 11712 carries one with
`RequiredCount 3`, so its region *scopes* the objective rather than being it — confirmed by its now-readable
text, *"Overcome the Emperor's Manifested Nightmares"*, which is a defeat objective inside that room.

## Wiring is bootstrap-only

The harvested goal carries the wire fields; the *server trigger* fields (`GoalType`, `TargetName`,
`TargetTemplateId`, `QuestItemId`, `NextQuestId`) are applied afterwards by the `quest-wiring` importer module, and every step there
skips a goal that is already wired. So a hand edit — or the `/admin/quests` editor in Faolan.Web — survives a
re-import, while a freshly harvested quest still bootstraps.

That editor shows a quest and all its goals on one screen, with the wiring fields editable and three things
alongside them that are *not* ours to set: the goal's **kind tag** (the client's own verb for the objective —
the evidence `GoalType` should follow, so a disagreement between the two is visible rather than hidden), the
name of its **trigger region**, and the **conversations** that grant the quest or gate a screen on holding it.
That last one is why it is worth showing: a quest that nothing grants is unreachable in game, and nothing about
the goal rows says so.

`TypeItemGoalsFromTrailingCategory` is the general rule rather than a per-quest seed: an unwired goal whose
trailing reference is an item, and whose kind tag is **not** `Deliver`, becomes `CollectItem`. It replaced 62
goals sitting at the `Kill` default with no target — inert fetch quests that could never complete. They were
harmless as well as useless: NPC template ids run 38–22,982 and these item refs 110,891–4,675,657, so no kill
could ever have matched one.

`TypeDeliverGoalsFromRecipient` is the same shape for the 12 **`Deliver`** goals (kind `20`). Their recipient is
not on the row, but it *is* in the objective text — *"Deliver a Supply Crate to Croiss at the refugee camp"*,
*"Give the Letter to Sien on the Lacheish Plains"* — so they type as `TalkToNpc` at that NPC. The talk trigger
solves a goal in one interaction, so "deliver N" completes without the count being involved. (Typing them
`CollectItem` instead would be wrong: the quest would complete the moment the player picks the item up, skipping
the delivery — a silent chain bug, where inert is at least visibly stuck.)

What gates the rest is the **WORLD, and specifically per-map spawn coverage**: only **2 of 12** recipients are
NPCs we hold — Turach (City of Tortage) and Croiss (Conarch Village). The other 10 stand on maps that have a
`Maps` row and **zero `NpcSpawns`**: Khopshef Province (3020), Kheshatta (3030), Lacheish Plains (1040), Wild
Lands of Zelata (2080), Chosain Province (6300), Gateway to Khitai (6001). So the wiring step gates on the
recipient being in `NpcTemplate` — a goal wired to an NPC that does not exist can never complete, which is no
better than inert and worse in that it *looks* wired. They light up on their own when those maps are imported.

⚠️ Do not match those names loosely. `Amun` (quest 12766's recipient, in Khopshef) substring-matches the
unrelated **`Amunarton`** — a Conall's Valley quest *giver*, and the giver of 14615 whose own recipient is
Shabaka. A `LIKE` match reads as a hit and wires the goal to the wrong NPC on the wrong continent.

⚠️ Two goals are deliberately **excluded** from the rule: 11880 and 11881 carry a **rank precondition** in their
text (*"Give Note of Recommendation to Itsing When You Have Become an Acolyte (Rank 1)"*), so a bare talk trigger
would complete them early. They need the rank gate first.

### A gated kill target is an editorial call, never a rule — and it is TWO calls

Some kill targets are one-time story NPCs: they are streamed only to a player holding the quest active, and they
do not respawn once killed. Saddur and Scavenger Tuut are the two, and retail solo-instances both.

⚠️ **Those are two independent properties and they are two columns** (#708). Visibility is
`GatesTargetVisibility` → `WorldEntity.GateQuestIds`, fed by `DatabaseRepository.GetQuestGatedNpcMap`; staying
dead is `TargetStaysDead` → `WorldEntity.NoRespawn`, fed by `DatabaseRepository.GetStoryKillTargets`. Both of
today's targets want both, which is exactly why one column answered both questions for so long — and the weld
could not express the first case that wants only one: a quest-only target on a **repeatable or retryable** quest
is gated and has to come back. Taking the gate forced `NoRespawn`; dropping it made the NPC visible to everyone.

⚠️ They are also read differently, which is the second reason they are not one thing. The visibility map
deliberately reveals a gated NPC to **every** quest that targets it (#574 — killing Tuut completes the only quest
that was revealing him, so his looted-for key became unobtainable); `GetStoryKillTargets` is a property of the
target and does no such widening.

Membership is set by hand. It used to be *derived* from "a `Kill` goal that has a `TargetName`", which was true of
the only two goals ever wired and false in general — it forced `NoRespawn`, which caps a goal at however many
spawns exist. Filling the target names mechanically therefore turned quest 12057 (*kill 9 Vanir Polluters*,
8 spawns in the world) into a permanently uncompletable quest, stuck at 8/9 with nothing left alive.

The map is keyed by **template** (#521). It was keyed by name until that bit: the flag on the Tortage Island
Saddur also gated the Stygian Galley one. `SeedContentTests.EveryGatedGoal_ResolvesToATemplate` holds the line,
because an unresolved flagged goal is not a *narrower* gate but **no gate at all** — the NPC silently stops being
hidden and starts respawning, and nothing in-client distinguishes that from "gated, and you qualify".

Whether a target is a unique story NPC or a population mob is not stated anywhere in the client data we can read,
so it is a column and not a rule. `SeedContentTests.GatedKillTargets_HaveEnoughSpawnsToFinishTheGoal` asserts the
property that makes setting one safe, rather than the current membership — and it reads `TargetStaysDead`, since
that is the flag that makes the kill count a hard cap: a gated target that respawns can always be killed again.

### A goal can spring an AMBUSH

`NpcAmbush` is the third trigger shape, after quest-gated **visibility** above (the NPC is there, only some players
see it) and the proximity **lure** (`NpcEncounter` — the NPC is there and turns into something else). An ambusher
is not in the world at all: it appears when a goal advances, beside the player, and attacks.

Decoded from `xxx_mage-necromancer_login_play2_tortage_city_first-quests`: picking up the first Quarry Stonebrick
for quest 12371 (*Get 3 stone bricks*) grants the item at t=315.6, and a **Bat Demon** arrives at t=318.6 — 3.6 u
from the brick, with the player already on its hate list. The trigger *effects* are immediate and the *body*
follows ~3 s later, which is why `DelaySeconds` is a column and not a detail. It is the only genuine mid-session
NPC spawn in that capture; everything else arrives in the world-entry stream.

- **Fires on the goal's FIRST advance only.** That session picks up all three bricks and gets exactly one Bat
  Demon. Because the gate reads *persisted* progress, a relog does not re-arm it.
- **An ambusher gets no placement row at all.** `NpcSpawns` used to hold one Bat Demon on map 4020 — a harvest of
  that sprung ambush, 10.2 u from where retail spawned it — which left a Bat Demon standing in the quarry
  permanently, for every player, regardless of quest state. See the rule below.

### A trigger-spawned type is never a world placement

Two kinds of creature are put in play by a *script* rather than by a placement, and they share one rule:

| kind | trigger | table |
|---|---|---|
| ambusher | a quest goal advances | `NpcAmbush` |
| encounter **boss** | a proximity lure swaps into it | `NpcEncounter.BossNpcTemplateId` |

A capture that caught either one caught the **trigger firing**, so harvesting it as a placement is the summoned-pet
failure one level over. `ImportOps.BuildBurstTruthSpawns` excludes both from the burst-truth set exactly as it
excludes summoned pets, and for the reason already written beside that exclusion: *a trigger spawn is not a world
placement, and a type with no `NpcSpawn` row is an established shape.* The **type** still survives via
`observedTypes` — that is what the trigger spawns from. `ZonePopulationLoader.EnsureZoneNpcs` skips them too, as the
second net for a database built before the rule.

⚠️ This was an **accident** before it was a rule. The Bat Demon really did stand in the quarry; the encounter boss
escaped only because she spawns on the exact spot her lure vacated and the position dedup dropped her (#501) —
which would have stopped protecting her the moment a capture caught the swap a few units off.
`SeedContentTests.NoTriggerSpawnedType_HasAWorldPlacement` is the gate.
- **Anchored on the player**, not the interacted object: retail measures from the brick, we measure from the
  player standing at it, which is the same spot to within the interaction range and needs no object identity to
  reach the progression engine.
- **No authored hate list.** The capture shows the Bat Demon arriving with `HateListUpdateIIR` already naming the
  player; ours spawns hostile and the simulation's own aggro pass picks the player up on the next tick.

Like `NpcEncounter`, the rows are **hand-authored** — nothing on the wire states a trigger or a delay — and gated
by `QuestAmbushTests`.

### A delivery checks that the player is carrying the goods

A hand-in advances on the **talk**, so without a precondition it completes by walking up to the recipient
empty-handed — wired, but never actually played. `QuestProgress.CanHandIn` gates it, and
`QuestProgressionService` supplies the count from the inventory.

The gate keys on the goal's **captured data**, not on our wiring: kind tag `Deliver` **and** a trailing reference
into `50200` `item.name` (so `TrailingRefId` is the item template id). It cannot key on `GoalType`, because a
hand-in *is* typed `TalkToNpc` — that is what triggers it — so `GoalType` cannot tell a delivery from an ordinary
talk goal. An ordinary talk (freeing Casilda) and a collect goal carrying the *same* item reference are both
correctly left ungated; only the kind tag separates the last pair.

The full count is required — "deliver 3 stone bricks" is not satisfied by carrying one — summed across slots,
since a stack can be split. A failed hand-in says so on-screen rather than silently doing nothing: the player is
on the right quest talking to the right NPC, so silence reads as a broken quest.

**And it consumes.** A completed hand-in takes the goods: `ConsumeDeliveryItems` → `TakeItemsFromInventory`,
which is the inverse of `GrantItemToBackpack` and runs off the same port. Two things it gets right that a
single full stack would hide — a stack **larger** than the goal asks for is decremented rather than cleared (hand
in 3 of 5 bricks and 2 stay yours), and a stack **split** across cells or containers is spanned, stopping the
moment the count is met. The arithmetic is the pure `InventoryTake.Plan`, gated by
`QuestTests.InventoryTake_TakesExactlyWhatIsAskedFor_AcrossSplitStacksAndContainers`.

It runs **after** the re-fire guard (`current >= required`), so talking to the recipient a second time cannot take
a second set. It is deliberately not backpack-only: a quest item lives in the Quest container (`0x71`), which
`RemoveItemFromBackpack` cannot reach — the same span the delivery check sums over.

⚠️ This is a **design decision, not a decoded fact**. Nothing in the corpus shows the removal; retail completes
the delivery without a captured `DispatchRemoveItem` in the surrounding frames. It is taken because a delivery
the player walks away from still holding is not a delivery. If a capture ever shows otherwise, `ConsumeDeliveryItems`
is the one place to change.

### The escort ends at the city DOOR, and the farewell follows the completion

Decoded from `xxx_mage-necromancer_login_play1_beach-to-enter-tortage_city` (and identical in the `mage-demo`
twin). The Tortage escort (12407, goal 3544, Casilda) does **not** end on a conversation or a zone change — it
ends when the player **opens the city door**:

| frame | dir | packet | what |
|---|---|---|---|
| 11355 | c2s | `LookAtIIR` (SelectDeselect) | the player selects the door |
| 11357 | c2s | `BehaviorControlCenterIIR` | the player acts on it — "opens" it |
| 11363 | s2c | `BehaviorControlCenterIIR` | server acks the action |
| **11365** | s2c | `QuestGoalIIR` quest `0x3077`=12407, goal `0x0DD8`=3544, 1/1 | **the escort goal completes** |
| 11365 | s2c | `QuestIIR` state `2` then `4` | Active → Completed |
| 11366 | s2c | `MainQuestUpdateIIR` | journal refresh |
| 11370 | s2c | `SetPosIIR` | Casilda is repositioned |
| **11395** | s2c | `KnubotOpenChatWindowIIR` + `DispatchSetDialogueCamera` + `KnubotAppendTextIIR` + `KnubotAnswerListIIR` | **the farewell conversation opens** |
| 11401/11413/11418 | c2s | `KnubotAnswerIIR` | the player's three picks |
| 11419 | s2c | `KnubotCloseChatWindowIIR` | she is done |

⚠️ **Order matters: the quest completes BEFORE the conversation, not because of it.** The door is the trigger; the
farewell is a separate, server-initiated beat that follows.

**That is how the escort is wired.** `GameServerListener.Inbound`'s `BehaviorId.UseDynel` case asks which NPCs
this object opens a server-initiated conversation for, completes their `Escort` goals, and then opens the farewell
(`ConversationService.TryOpenTriggeredConversation`) — in the capture's order.

⚠️ **The use that plays the farewell does NOT teleport; the player uses the door again to go through.** Retail
behaves the same way: in both beach-to-city captures the door-use begin phase is followed by the escort completing
and the window opening, and then 1,200+ further packets with **no playfield handoff at all**. The farewell also
opens with `{hidegoodbye}`, which pins the player until it is done — teleporting mid-dialogue would strand the
window on a map the NPC is not on. It is gated on the escort being *in progress*, so it plays once and the zone
line behaves normally on every later use. Which door is **data-driven, not hard-coded**: the object that plays the escortee's
farewell is by definition where the escort was leading, so `ConversationNode.TriggerWorldObjectId` supplies it and
the goal needs no new column. Gated by `SeedContentTests.EscortGoal_HasAWorldObjectThatCanCompleteIt`, which
checks the escort → escortee → trigger → `WorldObject` chain still holds in the shipped data.

The conversation (Casilda, `npcname` = `<localized id="9411" category="51000">`, her template id and goal 3544's
`TrailingRefId`) reads:

```
NPC     {camera player size=5 emphasis=1}{hidegoodbye}Friend?
player  Another gate...
NPC     {camera size=3 emphasis=1}{social scout}At least this one seems to be unlocked. On the other side, the
        city awaits us. We're close enough for me to make my own way from here. … but speak with me inside the
        city, and I'm sure we can come to...some kind of arrangement. {resolvegoal NKBO}
player  I'll see you inside the city walls.
NPC     Just remember to say nothing about escaping your captors or killing any slavers. … {resolvegoal NKBO}
        {gameactiononnpc SendScryEvent EventString RunToTortage}{showgoodbye}
player  Goodbye!
NPC     We'll meet again.
```

`{hidegoodbye}` opens it (the player cannot walk away) and `{showgoodbye}` releases them;
`{gameactiononnpc SendScryEvent EventString RunToTortage}` is what sends her off. `{resolvegoal <TOKEN>}` is the
same inline directive that resolves her earlier stage (`BJGY` for 13480) — see Gitea #356.

### Scry events: the string is retail's, the ACTION is ours (#839)

`SendScryEvent` (GameAction **53241**) carries a bare length-prefixed string that the playfield's script listens
for — `000E "SpawnScavenger"`, addressed to the NPC firing it, not to the player. We harvest the string into
`NpcScriptEvents(NpcTemplateId, Event)` and hang a server-side **`Action`** on it, exactly the way a quest goal's
`GoalType` sits beside its harvested row:

| action | what it does | shipped on |
|---|---|---|
| `None` | harvested, stored, logged when it fires — nothing happens | `SpawnSheep`, `AriasMageLeave`, `ProtectiveSpell`, `knubotconarchdrunk` |
| `TurnHostile` | the NPC turns on the player | Saddur `Fight` |
| `LeaveWorld` | walks to the object that opened the window, then despawns | Casilda `RunToTortage` |
| `RevealQuestTargets` | the creatures this player's quests are waiting on enter the world | Casilda `SpawnScavenger` |

⭐ **The runtime dispatches on the action, never on the string** — which is what makes a future quest's script a
seed line rather than a code change. `RunToTortage` names one quest's fiction and fits nobody else; a
`RunToKhemi` gets the same behaviour by stamping the same action. `None` is load-bearing for the same reason
`QuestGoalType.None` is: an unmapped event is *visibly* unmapped instead of being bent into the nearest
behaviour. `SpawnSheep` is the case that proves it — it looks like `SpawnScavenger` and is not, because
Ulfheoinn grants no quest we hold and no goal gates a Sheep, so a reveal there would reveal nothing and read as
wired. Gated by `NpcScriptEventTests`.

### `SpawnScavenger` — and why the quest gate stays

`RevealQuestTargets` says **when**; the quest says **who**. That is not a simplification, it is what retail does,
measured over 13 captures:

- **7 captures fire the event**, and Scavenger Tuut's spawn follows within the same instant — usually the same
  TCP segment, and in `Strie_rogue-assassin_login_randomActions` the *same frame* (3263). Order is always quest
  full-update → `SpawnScavenger` → spawn.
- **6 captures have no event at all** and spawn him in the world-entry burst (frames 22–126). In
  `Stihetfv_…_compeltefirstquest` he arrives at frame 105 while the quest full-update is at 684 — he predates it.
  All six players held quest 13479 in their login snapshot (frames 68/61/23…).

So retail has **two** paths: the scry event for the live "he appears now", and the ordinary gated world-entry
stream for every session after. The second is our `GatesTargetVisibility` → `WorldEntity.GateQuestIds`, and it is
why the gate stays: deleting it in favour of a live spawn would strand the quest for any player who logs out.

**The trigger is `BehaviorId.UseDynel` (0x12) on the city door**, and it reproduces across both beach-to-city
captures — necro `fr11357 → fr11395`, demo `fr10180 → fr10213`, same action, same 60-byte body, ~35 frames ahead
of the window opening. The door is a `WorldObject`, so the trigger is stable rather than session-volatile:

| capture | door handle | spawn IIR | geometry | destination |
|---|---|---|---|---|
| necro | `0x0000106D` | `0x33A56FB0` | `object_closed` → `object_open` | "City of Tortage" |
| demo | `0x00001027` | `0x33A56FB0` | `object_closed` → `object_open` | "City of Tortage" |

Exactly one row matches: **`WorldObjects.Id 1044692756`** — map 4010, `GeometryName` `object_closed`,
`DestinationMapId` 4020. So the escort's completion trigger is *use that object*, which is also the zone line into
the city.

There are **two trigger kinds**, and they are complementary. Seven server-opened conversations exist across the
corpus (4 captures): the two door ones are interaction-triggered, and the other five are preceded by **movement
alone**.

⚠️ The five are **proximity to the NPC**, not entry into a bounded area. Saddur's is
*"{hidegoodbye}Who stands before Saddur?"* — the beach confrontation you get by walking up to him. Only
volume-entry lives in RDB `1000053`, and [`bounded-areas-1000053.md`](../rdb/bounded-areas-1000053.md)'s own
research already established that proximity-on-NPC and volume-entry are different mechanisms. **So this needs no
region geometry at all** — the NPC is the target, and the only unknown was the radius.

The radius is **measured, not chosen**: across three independent captures Saddur's window opens at **12.9, 13.8
and 10.9** units from his spawn, so `ApproachConversationRadius` is 15 — covering all three with slack while
staying far short of the next NPC (Casilda at 153+). `ConversationService.CheckApproach` runs beside the
`ReachArea` check on each accepted move, and latches per NPC per session
(`ApproachedConversationNpcs`): Saddur's nodes carry no quest gate, so without it the window would re-open on
every step taken near him.

These screens are imported as of `c76593a9` (Gitea #523): the extractor used to key only on a player-initiated
`TalkToNpc(field3=0)` greeting, and nothing ever ENDED an instance, so a server-opened window could not start one
and its packets were appended to whatever screen was last open — misattributed rather than merely lost.
`KnubotOpenChatWindow`/`CloseChatWindow` are exactly paired on the wire, so the close is the instance boundary,
and a `ConversationEntry` whose `Trigger` is not `Talk` marks the result.

⚠️ Casilda has TWO server-opened conversations, and under the old answer-path key both sat at `AnswerPath ""`, so
the longer merged the other away (`13228`, the `{hidegoodbye}Friend?` opener). Their identity is the TRIGGER, which
is why the decode above matters — and since #642 the trigger is a column of the ROOT rather than a conditional term
in a hash, so the two are simply two rows.

### The importer types goals from their own objective text

`--only rdb-quest-text` (RDB-backed, opt-in) decrypts each goal's objective line and types it from the client's own
words. It replaced a hand-written recipient table — the point being that this is a **rule**, not a seed:

- a **`Deliver`** goal (kind 20) names its recipient — *"Deliver a Supply Crate to **Croiss** at the refugee camp"*
- a **talk objective** in any kind names its target — *"Speak with **Kern Wolfeye**"*, *"Report to **Mei-zhu**"*

Both become `TalkToNpc` at that NPC. The name is the LAST `to`/`with <Name>` in the line, because the item can
contain one (*"Deliver the Head of Athyr-Bast to Hashima"*) and the person always closes the phrase.

One pass takes the wired talk goals from **6 to 21**, and every one of the 168 goals is accounted for by reason:
15 typed, 83 already wired, 5 rank/level-gated, 63 not a deliver/talk shape, 2 with no name in the text.

#### Preconditions are RECORDED, and enforced at the trigger

Five goals state a precondition in their own text, and both shapes are captured rather than discarded:

| text | columns |
|---|---|
| *"Talk to Sharak **(level 30)**"* | `RequiredLevel` 30 |
| *"…to Itsing **When You Have Become an Acolyte (Rank 1)**"* | `RequiredRankTitle` `Acolyte`, `RequiredRank` 1 |

The goal is **wired normally** and the gate is enforced at the TRIGGER, by
`QuestProgress.PreconditionMet`. That split is deliberate: refusing to wire would throw the information away and
need a re-harvest the day the gate can be evaluated.

- **Level works today** — checked against the character's level.
- **Faction rank cannot be evaluated yet** (no faction system, Gitea #310), so it resolves to `null` and
  **unknown is NOT met**. The three rank-gated goals stay inert exactly as they were, and start working the moment
  `QuestProgressionService.ResolveFactionRank` can answer — no goal re-wired, no capture re-harvested. That method
  is the single seam; it is a real function returning `null`, not a TODO buried in a caller.

⚠️ Unknown must never read as *met*: a bare talk trigger would otherwise complete those quests EARLY, which is
silent and looks like an engine bug.

⚠️ The name is extracted from the text with the precondition clause REMOVED. *"…to Itsing When You Have Become an
Acolyte"* otherwise yields the recipient **"Itsing When"** — the name pattern takes up to two Capitalised words and
`When` is one, so the clause silently becomes part of the name and no NPC ever matches it.

⚠️ **A name is wired even when we do not hold that NPC** (12 of the 15). It is the client's own statement of who
the goal is about, the goal is inert either way, and recording it means it works the moment its map is imported —
no second pass. The run says how many, so "wired" is never mistaken for "playable": a wired goal whose NPC is
missing, or who has no captured dialogue (1 of the 15), still cannot complete.

⚠️ Names are matched EXACTLY. `Amun` substring-matches the unrelated `Amunarton` — a different NPC on a different
continent — which reads as a hit and wires the goal to the wrong target.

### What is left is the data, not the rule

85 goals are still at the fresh-harvest default. With the objective text readable, typing them is a rule rather
than per-quest sourcing: `Talk to X` / `Speak to X` / `Speak with X` / `Report to X` → `TalkToNpc` at `X`.

Applying it today wires **nothing**, and the reason is worth recording so it is not re-derived:

| goal | text | why not |
|---|---|---|
| 10038, 10056, 11455, 12175, 14075 | *"Report to Mei-zhu"*, *"Talk to King Conan of Aquilonia"*, … | no `NpcTemplate` — uncaptured maps (mostly Khitai) |
| 11882, 5374, 6642 | *"…When You Have Become a Thug (Rank 1)"*, *"Talk to Sharak (level 30)"* | a **rank/level precondition** a bare talk would skip |
| 2977 | *"Speak with Kern Wolfeye"* | the NPC exists and the maps agree, but he has **no conversation nodes** |
| 6351 | *"Speak to Chulainn"* | the goal's marker is Atzel's Fortress (1085); our only Chulainn is in the Castle of King Conan (2040) |

⚠️ **A talk goal needs conversation data, not just an NPC.** `AdvanceTalkGoals` runs only after a greeting is
served (`TryReplayConversation` returns early when no screen matches), so an NPC with no `ConversationNode` rows
can never complete one. Any future auto-typing rule must gate on **both** — exactly as the delivery rule gates on
the recipient being in the catalog. Tracked in Gitea #358 / #521.

Open work is tracked in Gitea (#262, #358, #521).
