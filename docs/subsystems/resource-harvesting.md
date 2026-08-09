# Resource harvesting

Mining a rock, chopping a tree or picking a cotton shrub yields a **stack of the matching resource item**. There
is no harvesting-specific protocol: retail delivers the resource through the ordinary item-grant path.

## The nodes

Harvest nodes reach the client as `DestructibleFullUpdateIIR` (`0x6BD56736`) world objects. Their **max health of
1000** is what identifies one — it is the discriminator that separates them from guild-city pieces at 1,000,000
(Gitea #498) and from a destructible CNPC at 70,000.
The `(current, max)` pair sits at **body-end −20 / −16**.

All 197 nodes across 25 node TYPES were already imported into `WorldObjects` by `WorldObjectExtractor` and already
streamed to the client; only the interaction was missing.

⚠️ **Two traps, both of which cost real analysis time:**

1. **Handles are recycled.** Handle `0x2898` is a guild wall (max 1,000,000) at 18:32:19 and an *Oak Tree*
   (max 1000, current 31) at 18:47:44 in the same capture. Grouping capture records by handle alone merges
   unrelated objects and invents transitions — it produced an apparent "wall dropped from 1,000,000 to 31" and a
   false conclusion that the health offset was wrong.
2. **The depletion stream is not in the FULL-UPDATE.** Each node's `DestructibleFullUpdateIIR` is sent **once**,
   already carrying its current health, so "no node's health changed across its updates" is true and means
   nothing. 39 of 85 node records across two captures read below max, because a live retail playfield is being
   harvested by everybody. The changes ride a **different record** - see below.

## Health

Health **bottoms out at 1, never 0**, and the node then despawns (`n3ToClientQuit`) — three nodes in
`chat-annotated-4` read exactly 1 and despawn within 30-60 s. So *exhausted* is `health <= 1`, not `== 0`.

`LiveWorldObject.Health` is live per-zone state, not a column, for the same reason the handle is. A node starts
full on every zone load, and harvesting **never deletes the row**.

## Retail's live health stream (#678)

Health moves on the wire, on **`StatIIR` (`0xF98E10B3`) stat `0x1B` `HealthCurrent`**, addressed to the node's
own entity class `0xC879`. It was invisible until that record was decoded: `StatCodec` used to model it as a
spellweave burst with the class pinned to `Character`, so every node's health update failed to parse. 3,431 of
them are in the corpus, over 406 node series [V2:
NecroL80_mage-necromancer_login_gameplay_chat-annotated.tshark.json].

- **Depletion is 100 per swing.** In the annotated capture the player's own mining walks node `0x2943` down
  `900 → 800 → 700`, three records ~8 s apart, and two other nodes do the same in the same span. So a full 1000
  node is ten swings, not one. (This closed the "harvest health decrement per swing" gap this doc used to carry;
  the annotated mining session then confirmed it independently, with the player narrating "-10% each time".)
- **Regrowth is a fixed step on a fixed cadence, per node.** The same `0x2943` then climbs `700 → 726 → 752 →
  … → 882`, exactly `+26` every 60 s, starting ~17 s after the last swing. Across the corpus the common steps
  are `+25 / 15 s` (755 samples), `+16 / 60 s` (322), `+26 / 60 s` (182) and `+33 / 60 s` (139) — so the STEP is
  per node and the tick is 15 s or 60 s. It stops at the 1000 cap (`997 → 1000`, then nothing).
- **The node's MESH tracks its health.** A regrowing wood node is `ChangeMesh`'d between the tier meshes as it
  fills - `006475_resource_wood_tier1_node3` at health 1, `..._node2` at 42 - each swap followed by the
  matching `SetKDTree` (that manifest's `<CollisionMesh2 file>`) and, in one case, a particle puff. It is the
  same `SimpleDynelVisualChangeIIR` sequence the quest-item pickups play, on the destructible class.

**Depletion is modelled** (#732): one interaction is one swing, taking 100 health and granting 1 unit, with the
new health broadcast on that same `StatIIR` so the health bar moves. A node is exhausted only when it can no
longer pay for a swing, and only then does it despawn and come back through `DynelVisualSequence`. **Regrowth in
place is not** — see "What is not modelled".

### The particle puff is not harvestable, and the ATTRACTOR is why (#686)

The one part of the sequence we cannot build is the dust cloud —
`StartParticleEffect` naming an RDB **1010207** record. #686 proposed harvesting it into a nullable column on the
strength of *"192 `e_StartParticleEffect` records sit in the corpus over ~13 distinct effect ids"*. That count is
real and it conflates **two different phenomena**, which the payload's own `attractor` word separates cleanly:

| attractor | records | what they are |
|---|---:|---|
| **34 / 35** | **191** | a STANDING decoration bolted to a bone. Six of their effect ids are literally the ones the objects' own geometry nodes already declare (#687). |
| **0** | **1** | free-standing at a position — the harvest puff. Effect `3667224`, handle `0x6AF3`, in the single capture that harvests a quarry brick. |

So the harvestable population is **one record**, and a `HarvestEffectId` column built from it would hold one row.
Building the puff from that is hardcoding one capture's dust cloud onto every pickup in the world under a new
name, which is what the current code declines to do — and it declines for the right reason.

What the split *does* buy is a mechanical filter: `attractor == 0` is the harvest family, so the moment more
harvest captures land the column becomes a one-line lift rather than a judgement call.

⚠️ It also surfaces a **separate** gap worth its own scope: six of the attached effect ids — `150629`, `199753`,
`199754`, `3198566`, `3198572`, `3198578` — appear on the wire but in **no** geometry node we hold. Those are
standing decorations our world objects are missing, which is a different subject from the harvest puff the ticket
was about.

## The payoff

```
DispatchAddItem                     -> the resource stack into the backpack (container 0x68)
DispatchDisplayItemLootChatMessage  -> [u32 itemId][u32 itemId][u32 0][u32 count]
```

⚠️ **That 4th u32 is the item's LEVEL, not a count**, and reading it as a count is what put 40 / 60 / 40 into
`HarvestCatalog.Yields`. The body is the same `ProtoAcgItem`-shaped layout the client's `itemref://` chat link
carries — `[template][template][0][level][hash][key1][key2][key3]`, `key3` being the `NullIdentity` sentinel —
and `Items.Level` for Granite, Oak and Iron is 40, 60 and 40 exactly, which is why the misread looked like three
independent measurements.

**One swing grants exactly one unit.** The count rides the ADD's own `stat_list` 26, and it is `1` on every
captured harvest in *both* mining captures. The resource stack ticks up by one per swing
(`DispatchSetItemStat` `StackSize` 2, 3, 4 …), and the annotations say the same: *"also -10 %, got 1"*, and
*"got 3"* after three swings on the gold vein.

| annotation | item | id | level (the old "count") | count |
|---|---|---|---|---|
| `just mined` | Granite | 161206 | 40 | 1 |
| `gethering wood` | Oak | 161213 | 60 | 1 |
| `mining iron` | Iron | 161196 | 40 | 1 |

[V2: NecroL80_mage-necromancer_login_gameplay_chat-annotated.tshark.json]
[V2: NecroL80_mage-necromancer_login_gameplay_annotated_5.tshark.json]

So a full node is ten swings for ten units, not one harvest for forty.

## The percentage the player steers by IS the health bar

The annotated mining session settles what the health stream is *for*: the client renders `health / max` as the
**percentage in the node's health bar**, like any character's. That is where the player's own running commentary
comes from, and it makes the numbers checkable against the wire to the digit:

| annotation | node | wire health |
|---|---|---|
| `next to a 12% silver vein now` (18:24:16) | Silver Vein `0x26d5` | 129 at 18:24:34 |
| `next to a 30% gold vein now` (18:26:12) | Gold Vein `0x2688` | 298 at 18:25:34 |
| `-10% each time` (18:26:42) | Gold Vein `0x2688` | 298 → 198 → 98 |
| `mining copper, also -10% per` (18:37:59) | Copper Node `0x26c0` | −100 a swing |

Regrowth measured on the same nodes agrees with the corpus figures above: `+33` (Gold, Basalt, Oak, Duskmetal),
`+26` (Iron, Granite, Yew, Electrum) and `+16` (Silver, Copper) per 60 s.

⚠️ **The node does not stop at 1 here.** The gold vein bottoms at 31 and the basalt rock at 28, and both then
regrow — so "bottoms out at 1, then despawns" is what the *earlier* captures' nodes happened to do, not a floor.
Nobody swung at a node holding less than 100, so whether a partial pool can pay for a swing is unmeasured.
[V0: whether a node under 100 health can still be harvested]

## The node's own name string lies — so the key is the DISPLAY id (#734)

The `DestructibleFullUpdateIIR` carries both a name STRING and a `DisplayId`. The client shows the `DisplayId`
resolved through **LDB category 50200**; the string is an internal name that retail itself has wrong on several
of the nodes:

| dynel | name string on the wire | LDB 50200 (what the client shows) |
|---|---|---|
| `0x26d5` / `0x26d6` / `0x26d4` | `Martial Ring` | **Silver Vein** |
| `0x26c0` / `0x26c5` | `Vitalic Bronze-studded Armbands` | **Copper Node** |
| `0x2688` | `Gold Vein` | Gold Vein |
| `0x269a` | `Iron Node` | Iron Node |

Across the shipped `WorldObjects` rows the same defect appears on **seven** node types — the two above plus an
ash tree shipping as `Bloodsilver-studded Gloves`, a cotton shrub as `Blacksmithing Hammer TESTITEM`, and three
carrying raw `NPC_Global_leather_…` asset names. Most rows of each node type carry the right string and a few do
not, which is what made a name key look like it worked.

`HarvestCatalog` therefore keys on the **display reference** — `(DisplayCategory 50200, DisplayId)` — and carries
the LDB name beside each entry as documentation and as the text the server logs and says. Both halves of the
reference are required: an LDB id is only unique within its category, and the guild-city destructibles next door
are category `50050` with ids as low as 73.

That also fixed the gate. `HarvestCatalogTests` used to look for shipped nodes by NAME SUFFIX (`" Tree"`,
`" Rock"`, `" Node"`, `" Vein"`, `" Shrub"`), which made it blind to exactly the rows the catalog was blind to —
it passed with seven node types uncovered. It now enumerates every **destructible whose max-health trailer word
is 1000 in category 50200**, which is the client's own definition of a harvest node, and demands a catalog entry
for each. That raised the catalog from 13 node types to the **25** that actually ship.

The node name maps straight to the resource, and most of the family already ships (161190-161225: Ironsilk, Silk,
Cotton, Duskmetal, Iron, Copper, Gold, Electrum, Silver, Basalt, Granite, Sandstone, Oak, Yew, Ash).

## What is not modelled

- **Regrowth in place**, now that the cadence is measured: an exhausted node still despawns and comes back
  through `DynelVisualSequence` rather than climbing its health back on a timer.
- **The retail swing sequence.** The client selects the node (`LookAtIIR` carrying class `0xC879`
  `EntityClass.Destructible`) and then sends `BehaviorControlCenterIIR` swings; how many swings and what cast time
  are not decoded. The server harvests on the `UseObject` interaction instead.
  Which step a given node takes is not derivable from any column we hold, only from what its own series did —
  though the step does at least group by node kind (`+33` Gold/Basalt/Oak/Duskmetal, `+26` Iron/Granite/Yew/
  Electrum, `+16` Silver/Copper). [V0: what selects a node's regrowth step]
- **The mine-triggered ambush.** Harvesting can spawn a hostile: at 18:29:47 of the annotated session, mid-swing
  on an Iron Node, `Argus the Thief` arrives as a full `FA015807` spawn followed by the aggro exchange, and the
  player narrates *"got ambushed while mining"* ten seconds later. We have no spawn-on-harvest hook at all.
- **The rare cross-drop.** A copper node paid out **tin** — a resource from a different node type — into its own
  resource slot at 18:39:57, beside the copper stack it was filling. So the yield is a roll against a table, not
  a fixed node→resource mapping, and `HarvestCatalog.Resources` models only the fixed part.
- **Ten node types have no resource item in `faolan.db`** — `Elm Tree`, `Ironwood`, `Greenstone Formation`,
  `Marble`, `Slate Rock`, `Nickel Deposit`, `Chromium Deposit`, `Bismuth Lode`, `Mithrite Lode`.
  They are listed in `HarvestCatalog.Nodes` with a **null** resource rather than omitted, so they render, deplete
  and name themselves correctly but pay out nothing; inventing an id would hand out the wrong resource.
  `HarvestCatalogTests` fails if an import ever supplies one, so the gap cannot quietly become permanent.
- **`Sandstone`, `Lead Deposit` and `Pyrite Lode`** were in the same gap and are not any more. The first two have
  a shipped item (161207, 3927913) and were only missing because the name key never reached them. ⭐ `Pyrite`
  (3927918) is subtler: the item shipped all along but **NAMELESS**, so a gap check that matches on the NAME could
  not see it — it only became findable once `rdb-items` decrypted its template. A row can be present and still
  invisible to a name-keyed test.
- **`Birch Tree`** yields Birch **3927910**, outside the `1611xx` band every other resource sits in. Included
  because the row is structurally identical, but it is the one entry whose id space does not corroborate it.

Open work tracked in Gitea #494.
