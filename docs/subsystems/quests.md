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
| `Escort` | the `UseObject` handler, on the object that plays the escortee's farewell | `TargetName`, resolved from `ConversationNode.TriggerWorldObjectId` — see below |
| `ReachArea` | the move handler, per accepted move | the goal's **region** if it has one, else a 40-unit radius around its map marker |
| `CollectItem` | **any item reaching the inventory** | `TrailingRefId == item template id` |

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
`QuestItemId`, `NextQuestId`) are applied afterwards by the `quest-wiring` importer module, and every step there
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

### A gated kill target is an editorial call, never a rule

Some kill targets are one-time story NPCs: they are streamed only to a player holding the quest active, and they
do not respawn once killed (`WorldEntity.GateQuestIds` / `NoRespawn`, fed by
`DatabaseRepository.GetQuestGatedNpcMap`). Saddur and Scavenger Tuut are the two, and retail solo-instances both.

Membership is the goal's own **`GatesTargetVisibility`** column, set by hand. It used to be *derived* from "a
`Kill` goal that has a `TargetName`", which was true of the only two goals ever wired and false in general — the
gate keys on the **name**, so it hides every same-named NPC in the world, and it forces `NoRespawn`, which caps a
goal at however many spawns exist. Filling the target names mechanically therefore turned quest 12057 (*kill 9
Vanir Polluters*, 8 spawns in the world) into a permanently uncompletable quest, stuck at 8/9 with nothing left
alive.

Whether a target is a unique story NPC or a population mob is not stated anywhere in the client data we can read,
so it is a column and not a rule. `SeedContentTests.GatedKillTargets_HaveEnoughSpawnsToFinishTheGoal` asserts the
property that makes setting one safe, rather than the current membership.

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
`observedTypes` — that is what the trigger spawns from. `WorldBroadcaster.EnsureZoneNpcs` skips them too, as the
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
staying far short of the next NPC (Casilda at 153+). `CheckPlayerApproachConversations` runs beside the
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
