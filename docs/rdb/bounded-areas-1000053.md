# Bounded-area trigger volumes (RDB type 1000053) — #504

The client's **trigger-volume table**: one record per playfield holding every named region the playfield reacts
to — hazards, spawn camps, resurrection points, encounter triggers, collision blockers, audio regions. **478
records holding 9,692 regions; 8,917 of them over the 393 records whose id is a real playfield** (the other 85 are
the variant `0x40000000`-based numbering). The RDB record id **== the playfield id** (== `Map.Id`), the same
convention [type 1010506](npc-spawn-layout-1010506.md) uses.

This matters because the project had assumed regions like these were server-side and therefore only
reconstructible from captures. For **area banners** that is true and stays true (`MapArea` documents the evidence:
scanning all 410,433 local RDB records for a banner's text id found only chance collisions). For **bounded areas
it is false** — the client needs them to draw the lava, block the camera and start its own scripts, so it ships
them exactly.

## Format

The payload is a 12-byte header followed by **plaintext XML** (no encryption, no nested compression beyond the
RDB record's own zlib). One `<boundedarea>` element per region:

```xml
<boundedareas><boundedarea StaticCollisionEnabled="false" PathingCollisionEnabled="false" … >
	<name>	BA_Lava7	</name>
	<scryid>	0	</scryid>
	<heights>	235.000000 241.699997	</heights>
	<Guid>	4892178	</Guid>
	<BoundedAreaID>	4294901798	</BoundedAreaID>
	<vertice>
		<vertex>	1286.014648 465.011475	</vertex>
		<vertex>	1288.386719 463.399994	</vertex>
		…
	</vertice>
</boundedareas>
```

| element | meaning |
|---|---|
| `<name>` | the region's name — the only classifier it carries (see below) |
| `<heights>` | the prism's `minY maxY`. Load-bearing: Ymir's Pass lava sits at 232–269 across its 22 volumes and a bridge crosses some, so an (x, z)-only test kills players walking safely overhead |
| `<vertice>`/`<vertex>` | the boundary polygon as `x z` pairs, in ring order, **implicitly closed** (the first point is not repeated). Concave ribbons are normal — up to 583 vertices for one lava flow |
| `<scryid>` | the id of the **client-side** script attached to the region, or `0` for none |
| the element attributes | per-region collision behaviour (`MovementCollisionBehaviour`, `LOSCombatCollisionBehaviour`, `CameraCollisionBehaviour`, …) |

The client's own names for this machinery are in `AgeOfConan.exe`'s RTTI and string table — recovered with a
plain `strings` sweep, no Ghidra needed:

```
BoundedAreaCollection          BoundedAreaCollectionManager
BoundedAreaLayer               BoundedAreaEnvironmentLayer
BoundedAreaSFX_Music_c         BoundedAreaSFX_Fixed3D_c        BoundedAreaSFX_Reverb_c
BoundedAreas   BoundedAreaNames   OnPlayerEnteredRegion
"BoundedAreaContainer_c::Enter starting script "
```

That last one is the load-bearing string: the client STARTS A SCRIPT when a player enters a region, which is why
the geometry has to ship with the client rather than living on the server.

## `scryid` separates the client's regions from the server's

A non-zero `<scryid>` means the CLIENT runs a script on entry; `0` means it does not, and any behaviour is the
server's. That is a real discriminator, not a naming guess, and it cleanly splits the lava set: the 111
lava-named areas include exactly three with a script — `Lava` on maps 917/942 (`scryid` 1112697) and
`BA_Lava_music` on 1090 (`scryid` 1112698), all of them tall ambient/audio companion regions — while every
**damage** volume carries `scryid 0`.

⚠️ The converse does not hold: `scryid 0` is the common case for *every* kind of region (an ordinary spawn camp
has it too), so it excludes rather than includes. Name is still the only thing that says what a region is *for*.

## What the server side looks like

For the lava, the server's own feedback names its script class outright:

```
<remoteformat token="showfifomessagefromserver(14)|playerenter(3)|GenericHazardiousBoundedArea"
              category="50018" … >
```

`GenericHazardiousBoundedArea`, triggered by `playerenter(3)`. So the division of labour is **the client owns
WHERE, the server owns WHAT**: geometry is imported from here, and the effect (which spell, how much damage,
whether it kills) is server data — `MapHazard`'s own columns.

## Validated against the wire

In `chat-annotated-4` the lava kills the player on **Ymir's Pass** (map 1090, pinned from the `MapChange` at
20:24:35). Walking the player's position track against the polygons puts the boundary crossing exactly where the
server fired:

| time | position | inside `BA_Lava7`? |
|---|---|---|
| 20:25:57.783 | (1286.7, 240.4, 478.0) | no |
| 20:25:58.281 | (1288.4, 239.2, 474.4) | no — 0.30 units outside the edge |
| **20:25:58.444** | **(1289.3, 238.3, 473.5)** | **yes** |
| 20:25:58.**706** | — | ← the `GenericHazardiousBoundedArea` feedback |
| 20:25:58.745 … | (1290.8, 238.1, 472.2) … | yes (and every later sample) |

The first sample inside the polygon precedes the server's feedback by **262 ms**, and the Y at the crossing
(238.3) is inside the prism's 235.0–241.7 range. So the volume the client ships *is* the volume retail triggers
on, to within one ~200 ms position sample — which is why the runtime containment test uses the polygon **with no
tolerance at all**. [V3: MapHazardGeometryTests.CapturedTrack_CrossesTheClientPolygonWhereRetailFired]

## What is in the 8,917 areas

Classified by name. Only the **lava** row is imported today (see `MapHazard` for why the rest are deliberately
left out — they are hazard-shaped by name with no wire evidence, and a mis-guessed kill volume is worse than a
missing one). The remaining buckets each touch an open subsystem.

The table is **generated**, not maintained by hand — `BoundedAreaClassifier` is the classification and
`--report bounded-areas` prints it, so it is recomputed from the real parser rather than from a script nobody can
re-run:

```zsh
dotnet run --project Faolan.Importer -- --report bounded-areas --RdbDir <rdb-dir>
dotnet run --project Faolan.Importer -- --report bounded-areas --report-arg spawn-camp --RdbDir <rdb-dir>
```

**8,917 regions over 393 playfield records**, excluding the 85 records in the variant (`0x40000000`-based) id
space, which are not `Map.Id`s. 544 of the regions carry fewer than 3 vertices and so have no testable footprint.

| bucket | areas | maps | related |
|---|---:|---:|---|
| `collision` | 2,205 | 252 | collision / line-of-sight (#284) |
| `encounter` — encounter + event start triggers | 625 | 138 | scripted encounters (#502) — retail's real trigger volumes, where ours are hand-seeded radii |
| `audio` — SFX / music regions | 600 | 161 | — |
| `spawn-camp` | 198 | 55 | ambient spawns (#332/#363) — **checked, see below** |
| `hazard-fire-death` | 147 | 31 | #504 |
| **`hazard-lava`** | **111** | **24** | **#504 — imported** |
| `resurrection` | 96 | 46 | resurrection spots (#372) — **checked, see below** |
| `hazard-trap` | 79 | 8 | #504 |
| `quest-goal` | 57 | 30 | `ReachArea` goals (#262) |
| `teleport` | 43 | 24 | local travel + zone lines (#506) |
| `dismount` | 14 | 4 | mounts (#27) |
| `hazard-drown` | 7 | 5 | #504 |
| `uncategorised` | 4,735 | 326 | — |

⚠️ The buckets are a **naming heuristic**, and the ordering is load-bearing: rules are applied in the order above
and first match wins, so an audio twin (`Vanir_camp_music`, `BA_Lava_music`) is audio rather than the thing it is
named after, a resurrection pad named for its camp (`ShadyCampRezz`) is a resurrection point rather than a camp,
and an ambush inside a camp (`TacticianCampAmbush`) is a trigger. `BoundedAreaClassifierTests` gates exactly those
contested names, since a mis-ordered rule silently mis-sorts a whole family.

There is deliberately **no rule for a "zone / area names" family**. Those regions are named after the place
(`Acheronian Ruins`, `The Iron Tower`, `Onyx Chambers`), so identifying one means matching the name against the
place-name catalog — a data lookup, not a pattern, and `MapArea`'s banner ids are LDB string ids rather than text.
That is #348's job.

## The client also ships a purpose-built table for two of these buckets

Two buckets describe something the client ALSO exports in a dedicated record type — and in both cases the
dedicated table carries strictly more, so the trigger volume is a **cross-check on what we imported, not a source
to import from**. `--report bounded-areas --report-arg <bucket>` prints the overlap in both directions.

| bucket | dedicated table | what we already hold |
|---|---|---|
| `spawn-camp` | **1010506** `NpcSpawnLayout` | `NpcSpawnArea` — 2,669 roam groups: bounds, population, and the exact spawn points |
| `resurrection` | **1000090** `RESURRECTIONPOINTSEXPORT` | `RespawnPoint` — 538 points: `Guid`, position, orientation, restoration %, discoverable, `shady` |

**Resurrection — redundant.** 91 of the 96 regions contain a 1000090 point already in `RespawnPoint`, and between
them they cover only 103 of our 538 points. So the bucket is a set of markers drawn around a subset of the points
we hold, with no id, no restoration level and no orientation. It corroborates the 1000090 import and adds nothing
to it.

**Spawn camps — corroborating, but not a source.** Only 56 of the 198 camps contain any 1010506 group: 189
(camp, group) containments over **160 distinct groups** of 2,669 (the two numbers differ because camps nest — on
Eiglophian Mountains `Vanir Camps` encloses 21 groups that `Camp27`/`28`/`29 Vanir` also enclose).

Where both exist the correspondence is exact, and the camp is the **parent** rather than a peer:

- **30 camps enclose exactly one group** — there the polygon is that group's authoritative footprint.
- **26 camps enclose 2–21** (`Camp23 White Hands` → `A`–`D`, `Camp24 White Hands` → 7, `Vanir Camps` → 21). The
  parent polygon is *looser* than each sub-group's own AABB, so it cannot stand in for one: all the sub-groups
  would collapse onto a single shared region and lose their distinct spots.

The 1010506 group names are already the same strings (`Camp18 Mammoths` is both a bounded area and an
`NpcSpawnArea.GroupName`), so the camp adds no naming either.

### The camps carry no creature, and neither table carries a template id

Field for field, this is everything the two sources hold:

| | 1000053 bounded-area camp | 1010506 group (`NpcSpawnArea`) |
|---|---|---|
| shape | **polygon** (x, z ring) + `heights` prism | AABB (min/max XYZ) |
| spawn points | — | the exact anchors (23,782 `NpcSpawnAreaPoint` rows) |
| population | — | `MaxCount` |
| roam paths | — | `pathCount` + the wander polylines |
| creature | only as free text inside `<name>` | `Category` + `GroupKey` |
| other | collision-behaviour flags, `scryid`, editor colours (`Color`, `Capped`, `BoundingBoxColor`) | — |

A `<boundedarea>`'s **entire** attribute set is collision behaviour and editor cosmetics —
`StaticCollisionEnabled`, `PathingCollisionEnabled`, `MovementCollisionBehaviour`, `LOSCombatCollisionBehaviour`,
`CameraCollisionBehaviour`, `IsLocked`, `IsVisible`, `FollowsHeightmap`, `HeightmapOffset`, `ShowBoundingBox`,
`BoundingBoxColor` — with `<name>`, `<heights>`, `<vertice>`, `<scryid>`, `<Guid>`, `<BoundedAreaID>` as the
children. Nothing about what lives there.

**Neither side carries an `NpcTemplate` id** — the creature is server-authoritative in both — but 1010506 says
much more about it: 179 `Name` groups name the creature outright (`Yeti`, `Shar Kihn (4YQ1)`, `Refuge
Corpse-Waker (7CSI)`) and the remaining 2,490 give a numeric family id (`Race` 926, `Community` 1,038,
`Superfamily` 229). So a camp name is *strictly weaker* evidence than a `Name` group's `GroupKey`.

That is also where the ceiling is. Only 22 of those 179 explicitly-named groups resolve; the other 157 name a
creature absent from `NpcTemplate`, which is 1,018 capture-derived rows. Map 1050's 87 groups are all unresolved
because `Mammoth`, `Cannibal`, `Ice Worm` and `White Hand` are simply not in it. The ambient spawner
(`LoadAmbientSpawns`) spawns at the 1010506 **points** and needs a resolved `NpcTemplateId`, so more geometry
cannot populate a single one of them — the blocker is the creature CATALOG (tracked in Gitea #518).

**Nothing from this bucket is imported.** It is checked, and the check is the result.

## The `encounter` bucket is real trigger volumes — but not for proximity encounters

625 regions over 138 maps, and they behave like genuine server-side triggers:

- **574 of 625 carry `scryid 0`** — no client script, so the behaviour is the SERVER's. The client ships only the
  geometry, exactly the split the lava volumes showed.
- **Small boxes.** Median footprint 25 units across, 347 of them under 30 — the shape of "walk in here, fire an
  event", not an ambient region.
- **Concentrated in instances**: Kara Korum 32, The Jade Citadel 28, Threshold of Divinity 26, The Vile Nativity
  25, Halls of Eternal Frost 14 — plus Conall's Valley (18), a scripted levelling zone.
- Names say what they are: 354 mention `ambush`, 191 `trigger`, 54 `encounter`, 52 `cutscene`/`scene`, 30 a
  `start`, 16 a `boss`/`miniboss`.

They also **name the creature**, which the spawn camps did not do usefully: **169 of the 625** contain a word that
matches a spawnable `NpcTemplate`. Some are specific enough to be unambiguous — `TriggerRaboz` → Raboz,
`SpiderBossTrigger1` → Spider Queen, `BA_DemonTrigger`, `AriasMageTriggerSensor`. Conall's Valley is the clean
worked example, where the name carries creature + location + a lettered series:

```
VanirAmbushGormleyA/B/C   SpiderAmbushGormleyD/E/F   WolfAmbushA/B/C/D   VanirAmbushA/B/E/F/G
```

### ⚠️ They are NOT the radii behind our proximity encounters

The obvious hope — that these supply retail's real volumes where `NpcEncounter` carries hand-seeded radii — does
**not** hold, and the two encounters we know about both say so:

| encounter | map | contained by |
|---|---|---|
| `Damsel in Distress` (the #502 lure, at 1130.1, 1141.7) | Poitain 2070 — 24 regions total | `PlayfieldBorder` only, i.e. the whole playfield. The nearest region of ANY kind is **166 units away**. |
| `Saddur` tpl 9500 (Tortage beach, at 521.4, 757.2) | Tortage Island 4010 — 51 regions | `Headless Hollow`, `Jungle Main`, `TutorialLockedGate`, `Flies_Roam2`, `Jungle Main Music` — place-name, gate and audio regions. No trigger volume. |

Both fire on **distance to an NPC**, not on entering a region, so their radii were never going to be in a table of
regions. Proximity-on-NPC and volume-entry are two different mechanisms, and only the second one is here. The
`NpcEncounter` radii stay measured from capture tracks.

Saddur is the sharper of the two tests, because he is a *known* proximity encounter on a **real, well-covered
playfield**: Tortage Island carries 51 regions including ambush volumes elsewhere on the map (`BA_PictAmbush`,
44 units away), so the table is clearly populated there — it simply has nothing for him.

Two incidental observations from that test: `Headless Hollow` and `Jungle Main` are the place-name family this
page declines to classify by regex, confirming it exists and is identifiable only against a place-name catalog;
and `NpcStatCore.ClientScryScript` — the obvious place an NPC-attached trigger script would live — is **NULL for
all 1,018 rows**, so nothing in our data currently describes the NPC-side mechanism either.

## Import

`RdbHazardImporter` (the opt-in `rdb-hazards` module) reads the lava volumes into `MapHazard` +
`MapHazardVertex`: **105 volumes / 5,396 vertices over 22 playfields**, from Tortage Volcano and the Acheronian
Ruins through Ymir's Pass, the Amphitheatre of Karutonia, Dragon's Spine and the Catacombs.

Seven lava-named areas are skipped and the importer prints why for each — all seven are the scripted ambient
companions (`Lava` on 917/942/5012, `Lava Area` on 944/4010, `LavaUpper` on 5012, `BA_Lava_music` on 1090). A
further 85 whole records are skipped as a counted summary: they sit in the `0x40000000`-based variant playfield
numbering, which is not a `Map.Id`.

⚠️ Two traps cost real time here and are worth knowing before touching the parser:

- **A record holds several sibling ROOT blocks**, not one (Ymir's Pass has ten). Scanning from the first
  `<boundedareas>` to the first `</boundedareas>` yields 7 of that map's 121 areas — and none of its lava.
- **There are two export dialects.** Newer records open the element with attributes (`<boundedarea Static… >`),
  older ones write a bare `<boundedarea>`. Anchoring on the string `"<boundedarea "` parses the first perfectly
  and returns **zero** areas for the second — which silently lost 9 playfields including Tortage Volcano's 7
  volumes, while every other number in the import looked healthy.

Both are gated by `BoundedAreaCollectionTests`.

```zsh
dotnet run --project Faolan.Importer -- --only rdb-hazards --RdbDir <rdb-dir> --captures <data-repo>/Captures
dotnet run --project Faolan.Importer -- --report rdb-dump --report-arg 1000053:1090 --out <dir> --RdbDir <rdb-dir> --captures <data-repo>/Captures
```
