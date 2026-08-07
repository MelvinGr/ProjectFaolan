# Resource harvesting

Mining a rock, chopping a tree or picking a cotton shrub yields a **stack of the matching resource item**. There
is no harvesting-specific protocol: retail delivers the resource through the ordinary item-grant path.

## The nodes

Harvest nodes reach the client as `DestructibleFullUpdateIIR` (`0x6BD56736`) world objects. Their **max health of
1000** is what identifies one — it is the discriminator that separates them from guild-city pieces at 1,000,000
(Gitea #498) and from a destructible CNPC at 70,000.
The `(current, max)` pair sits at **body-end −20 / −16**.

All 141 nodes across 20 names were already imported into `WorldObjects` by `WorldObjectExtractor` and already
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
  node is ten swings, not one. (This is the `[V0: the harvest health decrement per swing]` this doc used to
  carry.)
- **Regrowth is a fixed step on a fixed cadence, per node.** The same `0x2943` then climbs `700 → 726 → 752 →
  … → 882`, exactly `+26` every 60 s, starting ~17 s after the last swing. Across the corpus the common steps
  are `+25 / 15 s` (755 samples), `+16 / 60 s` (322), `+26 / 60 s` (182) and `+33 / 60 s` (139) — so the STEP is
  per node and the tick is 15 s or 60 s. It stops at the 1000 cap (`997 → 1000`, then nothing).
- **The node's MESH tracks its health.** A regrowing wood node is `ChangeMesh`'d between the tier meshes as it
  fills - `006475_resource_wood_tier1_node3` at health 1, `..._node2` at 42 - each swap followed by the
  matching `SetKDTree` (that manifest's `<CollisionMesh2 file>`) and, in one case, a particle puff. It is the
  same `SimpleDynelVisualChangeIIR` sequence the quest-item pickups play, on the destructible class.

We model none of this yet: a harvest empties the node in one use and despawns it, and it returns on zone reload.
Wiring the measured stream up is Gitea #494.

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

Measured in the mining span, each landing beside its own annotation:

| annotation | item | id | count |
|---|---|---|---|
| `just mined` | Granite | 161206 | 40 |
| `gethering wood` | Oak | 161213 | 60 |
| `mining iron` | Iron | 161196 | 40 |

[V2: NecroL80_mage-necromancer_login_gameplay_chat-annotated.tshark.json]

The node name maps straight to the resource, and the whole family already ships (161190-161225: Ironsilk, Silk,
Cotton, Duskmetal, Iron, Copper, Gold, Electrum, Silver, Basalt, Granite, Sandstone, Oak, Yew, Ash) matching the
streamed node names one-for-one. `HarvestCatalog` therefore keys on the node **name**, the same stable key shops
and conversations use — so no new column, no importer change, and no re-harvest of `faolan.db`.

## What is not modelled

- **The per-swing decrement**, now that it is measured at 100: one `UseObject` still empties the node here,
  where retail takes ten swings off it.
- **The retail swing sequence.** The client selects the node (`LookAtIIR` carrying class `0xC879`
  `EntityClass.Destructible`) and then sends `BehaviorControlCenterIIR` swings; how many swings and what cast time
  are not decoded. The server harvests on the `UseObject` interaction instead.
- **Respawn**, now that the cadence is measured: nodes still return on zone reload rather than regrowing in
  place. Which step a given node takes is not derivable from any column we hold, only from what its own series
  did. [V0: what selects a node's regrowth step]
- **`Elm Tree` and `Slate Rock`** ship as nodes but no item named Elm or Slate exists in `faolan.db`, so they are
  deliberately absent from the catalog and inert. `HarvestCatalogTests` fails if an import ever supplies one, so
  the gap cannot quietly become permanent.
- **`Birch Tree`** yields Birch **3927910**, outside the `1611xx` band every other resource sits in. Included
  because the row is structurally identical, but it is the one entry whose id space does not corroborate it.

Open work tracked in Gitea #494.
