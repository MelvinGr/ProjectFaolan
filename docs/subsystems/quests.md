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
| `Kill` | `CombatService` on an NPC death | `TrailingRefId == NpcTemplateId` first, `TargetName` as fallback |
| `LootFromNpc` | `LootService` when an item leaves a corpse bag | the bag's source NPC **and** the item id |
| `TalkToNpc` | `ConversationService` on the NPC greeting | `TargetName`. **Solves** the goal (jumps to required) rather than +1, so a "deliver N to X" hand-in completes in one talk |
| `Escort` | — | not wired |
| `ReachArea` | the move handler, per accepted move | the goal's **region** if it has one, else a 40-unit radius around its map marker |
| `CollectItem` | **any item reaching the inventory** | `TrailingRefId == item template id` |

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

⚠️ **The goal's own objective TEXT is not readable.** Every goal's `ObjectiveCategory` is `50304`
(`quest_goal_templates.goal_name`) with `ObjectiveRefId == GoalId`, and 50304 is one of eleven **encrypted** LDB
categories (Gitea #520). So the server cannot render, or read, what a goal asks the player to do — which is why
goal wiring is sourced from captures and the reference above rather than from the client's text.

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

## ReachArea: the marker is a pin, the region is the trigger

A `ReachArea` goal carries a map marker, and the marker is what the client draws — not what retail triggers on.
Where the goal has a `51200` reference the real volume is recoverable and imported as a `MapRegion`
(see [`../rdb/bounded-areas-1000053.md`](../rdb/bounded-areas-1000053.md)); the runtime then tests that polygon
prism. Goals without one keep the 40-unit marker radius.

Worked example — the beach starter, quest 10238 goal 1448: `TrailingRefId 180` → `LDB[51200][180]` =
`"Trappers Haven"` → a 7-vertex region on Tortage Island containing the goal's own marker.

⚠️ A `51200` reference does **not** by itself make a goal a reach objective. Goal 11712 carries one with
`RequiredCount 3`, so its region *scopes* the objective rather than being it. Distinguishing the two needs the
objective text, i.e. #520.

## Wiring is bootstrap-only

The harvested goal carries the wire fields; the *server trigger* fields (`GoalType`, `TargetName`,
`QuestItemId`, `NextQuestId`) are applied afterwards by the `quest-wiring` importer module, and every step there
skips a goal that is already wired. So a hand edit — or the `/admin/quests` editor in Faolan.Web — survives a
re-import, while a freshly harvested quest still bootstraps.

`TypeItemGoalsFromTrailingCategory` is the general rule rather than a per-quest seed: any unwired goal whose
trailing reference is an item becomes `CollectItem`. It replaced 72 goals sitting at the `Kill` default with no
target — inert fetch quests that could never complete. They were harmless as well as useless: NPC template ids
run 38–22,982 and these item refs 110,891–4,675,657, so no kill could ever have matched one.

Open work is tracked in Gitea (#262, #358, #521).
