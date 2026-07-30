# Resource harvesting

Mining a rock, chopping a tree or picking a cotton shrub yields a **stack of the matching resource item**. There
is no harvesting-specific protocol: retail delivers the resource through the ordinary item-grant path.

## The nodes

Harvest nodes reach the client as `DestructibleFullUpdateIIR` (`0x6BD56736`) world objects. Their **max health of
1000** is what identifies one — it is the discriminator that separates them from guild-city pieces at 1,000,000
([#498](https://git.groenendaal.io/MelvinGr/projectfaolan/issues/498)) and from a destructible CNPC at 70,000.
The `(current, max)` pair sits at **body-end −20 / −16**.

All 141 nodes across 20 names were already imported into `WorldObjects` by `WorldObjectExtractor` and already
streamed to the client; only the interaction was missing.

⚠️ **Two traps, both of which cost real analysis time:**

1. **Handles are recycled.** Handle `0x2898` is a guild wall (max 1,000,000) at 18:32:19 and an *Oak Tree*
   (max 1000, current 31) at 18:47:44 in the same capture. Grouping capture records by handle alone merges
   unrelated objects and invents transitions — it produced an apparent "wall dropped from 1,000,000 to 31" and a
   false conclusion that the health offset was wrong.
2. **There is no depletion stream.** Each node is sent **once**, already carrying its current health. So "no
   node's health changed across its updates" is true and means nothing. 39 of 85 node records across two captures
   read below max, because a live retail playfield is being harvested by everybody.

## Health

Health **bottoms out at 1, never 0**, and the node then despawns (`n3ToClientQuit`) — three nodes in
`chat-annotated-4` read exactly 1 and despawn within 30-60 s. So *exhausted* is `health <= 1`, not `== 0`.

`LiveWorldObject.Health` is live per-zone state, not a column, for the same reason the handle is. A node starts
full on every zone load. That is deliberate: retail nodes are shared across the playfield and regrow, and neither
the sharing nor a respawn cadence has been measured — so a fresh zone giving a full node is the closest honest
model, and it means a harvested node returns rather than being gone for good. Harvesting **never deletes the row**.

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

- **The per-swing decrement.** One harvest empties the node. The ends are measured (full → 1 → despawn) but the
  middle is not: the partial values in the captures are other players' harvesting, so they cannot be read as a
  decrement curve. [V0: the harvest health decrement per swing]
- **The retail swing sequence.** The client selects the node (`LookAtIIR` carrying class `0xC879`
  `EntityClass.Destructible`) and then sends `BehaviorControlCenterIIR` swings; how many swings and what cast time
  are not decoded. The server harvests on the `UseObject` interaction instead.
- **Respawn.** No cadence measured; nodes return on zone reload.
- **`Elm Tree` and `Slate Rock`** ship as nodes but no item named Elm or Slate exists in `faolan.db`, so they are
  deliberately absent from the catalog and inert. `HarvestCatalogTests` fails if an import ever supplies one, so
  the gap cannot quietly become permanent.
- **`Birch Tree`** yields Birch **3927910**, outside the `1611xx` band every other resource sits in. Included
  because the row is structurally identical, but it is the one entry whose id space does not corroborate it.

Open work tracked in Gitea #494.
