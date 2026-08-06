# Bounded-area trigger volumes (RDB type 1000053) — #504

The client's **trigger-volume table**: one record per playfield holding every named region the playfield reacts
to — hazards, spawn camps, resurrection points, encounter triggers, collision blockers, audio regions. **478
records holding 9,692 regions; 8,917 of them over the 393 records whose id is a real playfield** (the other 85 are
the variant `0x40000000`-based numbering). The RDB record id **== the playfield id** (== `Map.Id`), the same
convention [type 1010506](npc-spawn-layout-1010506.md) uses.

This matters because the project had assumed regions like these were server-side and therefore only
reconstructible from captures. That is **false** — the client needs them to draw the lava, block the camera and
start its own scripts, so it ships them exactly.

⚠️ For **area banners** the assumption does not survive at all, and it is worth being exact about what is whose,
because this has been stated wrongly twice:

- **The TEXT is the client's.** The server sends an `AreaNameId` and nothing else; the client renders the string.
  "The West Gate" is in English LDB page 974162.
- **The GEOMETRY is the client's too** — it ships the trigger volumes, and 12 are now imported and driving the
  banners. See [the zone / area-name section](#the-zone--area-name-regions-exist--found-by-containment-not-by-name).
- **What is ours** is only the LINK between them: which region carries which `AreaNameId`. The client's
  bounded-area record does not name a banner id, which is why the pairing had to be reconstructed geometrically.

The old evidence for "the text is server-side" does not support it. Scanning all 410,433 local RDB records for a
banner's text id found chance collisions, and none of the 22 `AreaNameId`s resolves through our LDB page index —
but that is a statement about our **id → (page, index) lookup**, which is #291's un-pinned page base, not about
the strings being absent. Searched by content they are plainly there.

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

## The `quest-goal` bucket — and the discovery volumes hiding in it

57 regions over 30 maps by name, holding four different things:

- **Named quest regions** — `QUEST Threat of the Vanir`, `Quest_TheCowardWay`, `Quest_Who_Is_Wretched`,
  `BA_CasildaScreamAndQuestNPCTutorial` (our #261 Casilda tutorial, on Tortage Island).
- **Quest FAIL volumes** — `FailQuestBetrayer`, `FailQuestToungues` on Gateway to Khitai. Leaving a region fails
  the quest; we have no such mechanic.
- **Discovery volumes** — `BA_*_RezDiscovery`, `FrostFields_Discovery`, `FotD_Discovery`, `BA_CampDiscovery`.
- A generic `BA_Quest_GenericPlayerSolveGoal` on Conarch Village.

⚠️ **The bucket under-counts the real quest regions**, because retail names a region after the PLACE, not the
quest — the "enter the jungle" region below is called `Jungle Main` and classifies as uncategorised. Name-based
bucketing finds the ones a designer happened to label `Quest*`; it cannot find the rest.

### ReachArea goals: one real match, and no id link

We hold 10 `QuestGoalType.ReachArea` goals, but **only one is a retail region goal**: quest 10238 goal 1448
("Enter the jungle") carries `TrailingRefId 180` in `TrailingCategory 51200`, a region reference. The other nine
are our own seeds, marked by the `TrailingCategory = 100` we assign, so there is nothing retail to look up for
them.

For that one goal the client's region is obvious and our approximation is poor:

| | trigger |
|---|---|
| ours | a **40-unit radius** around the goal's marker (416, 701) |
| the client | `Jungle Main`, a 13-vertex polygon spanning **392 × 529** units |

The marker sits inside it, and so does Saddur (521, 757) — the quest's *second* goal — further into the same
jungle, which is the order the quest is played in. So the marker is a **map pin**, not the trigger; the trigger is
a named region many times larger.

⚠️ **Superseded — there IS an id link**, found later via the client's category descriptor table; see "LDB
category 51200" below. `TrailingRefId 180` resolves to the region named **`Trappers Haven`**, which is one of the
four regions containing the marker, and is 96 × 82 rather than `Jungle Main`'s 392 × 529. What follows was the
reading before that: the regions carry no `<Guid>`, and `BoundedAreaID` is a per-playfield index (`Jungle Main` is
`-65516`, low word 20) rather than the goal's `180`, so matching looked like geometry plus a judgement call.

### The discovery volumes DO map onto data we hold

The one place in this bucket where the client's geometry lines up with something we already run. **13 of the 16
discovery-named regions contain one of our `RespawnPoint` rows**, by name as well as position —
`BA_MountainGlade_RezDiscovery`, `BA_CascadeFalls_RezDiscovery`, `BA_CimmerianSettlement_RezDiscovery` on Conall's
Valley, `BA_HoCRezDiscovery` on House of Crom and its three instance copies.

And they say our radius is wrong. `WorldSimulation.RespawnDiscoveryRange` is a hardcoded **30 units** — a 60 × 60
box with no vertical bound — while the real volumes are:

| kind | footprint | prism height |
|---|---|---|
| res-point discovery (11 regions) | **16 × 20 to 29 × 33** | ~10 units |
| area discovery (`FrostFields_Discovery`, `FotD_Discovery`) | 157 × 185, 114 × 74 | 50–60 units |

So we discover a resurrection point from roughly **twice** the distance retail requires, ignoring height entirely
— and the two large ones are a different mechanic (zone discovery) that we do not implement at all. Tracked in
Gitea #519.

## The `teleport` bucket is a WALK-IN mechanism we do not have

43 regions over 24 maps, and this is the first bucket that is neither a duplicate nor a dead end: it describes
transitions we cannot currently express.

**It is not our doors.** Our zone lines are `UseObject` on a `WorldObject` (`DoorFullUpdateIIR`/SimpleDynel), 22
of them configured with a destination. The two sets are **disjoint**: only map 1020 has both, and there the door
(`To Ymir's Pass`, at 1149/226) and the region (`ArenaTeleport`, at 232/554) are nowhere near each other. **No
door falls inside any teleport region.** These are additional transitions, and they fire on *entering a volume*
rather than on interacting with an object.

**It is not the NPC travel system either.** `TravelDestination` is NPC-mediated, paid, cross-map travel (two
wagoner rows). The regions named `*LocalTeleport*` mean *within-map*: `BA_TheCrypticTrail_LocalTeleport_Ground`
(y 32–42) and `_Middle` (y 115–125) are two 8 × 10 pads at nearly the same (x, z) some 83 units apart
vertically — a climb shortcut on Kara Korum's Cryptic Trail. That is our `RelocateSameMap` path, not travel.

Four sub-families:

| family | example | destination recoverable? |
|---|---|---|
| named destination | `BA_Teleport_ChosainProvince`, `BA_teleport_paikang`, `BA_TeleportToConarch` ×3 | **yes, from the name** — 9 resolve straight to a `Map` row, 12 with a `To…` strip |
| zone-line **edge strips** | `BA_Teleport_NorthernGrasslands` (737 × 109), `BA_Teleport_ChosainProvince` (582 × 89) on Kara Korum, lying along the map border at z ≈ −60…49 | yes — this is the outdoor Khitai zone line: walk off the edge, arrive in the neighbour |
| `BA_TeleportOut` ×7 | Blessed Caves, Cavern of Malice, Crevice of Malice, Enigmata of Yag | **no** — "out" implies the parent zone and never names it |
| lettered **pairs** | The Enigmata of Yag's `BA_TeleportA_1`/`A_2`, `B_1`/`B_2`, `C_1`/`C_2`, `D_1`/`D_2` | **self-sufficient** — the pairing IS the data; walk into one pad, arrive at its twin |

One reciprocal pair spans two playfields and confirms the family is two-ended: `BA_Teleport_TheMaze` on Wild
Lands of Zelata (2080) and `BA_Teleport_WLoZ` on The Maze (2082).

### ⚠️ The blocker: a region says where you LEAVE, never where you ARRIVE

The `<boundedarea>` element carries no destination field of any kind — its whole attribute set is collision
behaviour and editor cosmetics. So even where the name gives the destination *map*, the arrival **position** is
absent, and a teleport needs one. Our door mechanism only has arrival coordinates because
`WorldTeleportExtractor` harvests them out of captured `N3Teleport` bodies, and none of these playfields is in
the corpus.

The **lettered pairs are the exception** and the one importable case: the twin region's centre is the arrival
point, so `A_1`↔`A_2` is complete information with no capture and no name resolution required.

(A naming inconsistency worth knowing: Cradle of Decay's exit box is `BA_Teleport_FotD` on the real playfield
1026 but `BA_Teleport_WLoZ` on the 967 audio variant — the same geometry with two different claimed
destinations. Trust the real playfield.)

## The zone / area-name regions exist — found by containment, not by name

This page declines to classify a `zone-name` bucket by regex, because the regions are named after the PLACE. That
makes them invisible to a name rule but **not** invisible: they can be found from the other end, by asking which
region contains a banner we have already observed. `MapArea` (#348) is exactly that observation set — 22 rows over
7 playfields, each an `AreaNameId` plus a hull over the positions the banner was seen firing at.

**Every one of the 22 hulls falls inside a region, and 18 of them inside a recognisably PLACE-NAMED one:**

| banner (`AreaNameId`) | map | smallest place-named region containing it |
|---|---|---|
| 3385037 / 3385038 | Old Tarantia 2000 | `BA_EastGate` (413 u²) / `BA_WestGate` (651 u²) |
| 3373525 | Old Tarantia 2000 | `BA_MarketWard` (3,102 u²) |
| 3373519 | Old Tarantia 2000 | `BA_Bridge` (4,615 u²) |
| 3906077 | Old Tarantia 2000 | `Outlaw Camp` (2,701 u²) |
| 3147973 / 3474586 | Tarantia Noble District 2010 | `The Southern Bridge` (578 u²) / `Castle Approach` (25,314 u²) |
| 277763 / 277804 / 277805 | Field of the Dead 1020 | `Clan Laeda Burial Mounds` / `Scout Camp` / `Clan Gaud Burial Party Camp` |
| 269294 | Ymir's Pass 1090 | `POI A - The Last Cimmerian House` — the client's own "point of interest" prefix |

Those read exactly like the banners the client shows on entry, which is the substantive result: **the area-banner
regions do ship with the client**, so #348's note that they are "server-side and only reconstructible from
captures" is wrong about the geometry. It is not right about the text either — the client renders that from its
own LDB. What is genuinely ours is the LINK from a region to its `AreaNameId`, and nothing else.

### The ambiguity is resolved by the region's SCRY SCRIPT, and 12 are now imported

One of the two blockers below is gone. The other stands.

- **No id link *for the banner* — still true.** The banner's `AreaNameId` is not the ELF-28 hash of the region
  name (13 spellings of all 5,308 distinct region names against all 22 ids — zero hits), and **none of the 22 ids
  resolves through our LDB page index**. So there is nothing to join a region to a banner id by, and the match can
  only ever be geometric. ⚠️ That is a missing *link*, not a missing *string*: the client renders these banners
  from its own LDB, and searching by content finds them (`The West Gate` → page 974162). The id lookup is #291.
- **~~Containment is ambiguous~~ — SOLVED.** The ambiguity was entirely the audio and collision regions, and the
  client separates those itself: a region's `<scryid>` says which client script it runs, and **220656 is the
  crossing NOTIFIER** — the script that tells the server the player entered. A region that never notifies the
  server cannot be what fires a server-sent banner, so filtering on it is not a heuristic but a statement of
  mechanism.

[V1: Faolan.Importer/Rdb/BoundedAreaClassifier.cs — 479 regions over 60 playfields carry `scryid 220656`. On Old
Tarantia it selects exactly the `BA_*` sub-areas, on Tarantia Noble District the named quarters (Noble Quarters,
The Market, Castle Approach) and on Field of the Dead the Clan burial mounds. Applied to the banner test it takes
Old Tarantia's `3373519` from *four* containing regions (tightest `City_sounds`, an audio region at 142,490 u²) to
**exactly one**, `BA_Bridge` at 4,615 u². `--report bounded-areas --report-arg area-banners` prints the whole
test.]

⚠️ It is **not** "the banner script". 4 dismount volumes, 5 fire hazards, a respawn-discovery area and 3 encounter
triggers carry it too — it means *this volume talks to the server*, which is a weaker and more useful claim than
identification. It is a FILTER; pairing it with a captured banner is what identifies one.

**Imported** by `RdbRegionImporter` (`rdb-regions`) under three gates: the scry filter, containment of the
banner's captured hull, and SEPARATION — a region holding two banners' samples cannot be what distinguishes them,
so it is rejected for both. That resolves **12 of the 22**; `WorldSimulation` then tests the polygon with no slack
where one is linked and falls back to the hull elsewhere.

⚠️ **No tolerance on the containment test, deliberately.** A banner fires ON ENTRY, so its sample sits right at the
boundary and a near-miss is expected — 10 of 22 land outside every notifier region (including `3373522`, the
best-sampled at 18 sightings, whose hull spills outside `BA_TradingPost` at the edges). Padding until they match
would be fitting the answer. Those keep the captured hull, which is what it is for.

The prize was real: 11 of `MapArea`'s 22 rows rest on a **single sample**, i.e. a zero-area point that only works
with a runtime slack, and the client ships true polygons for the same places.

⚠️ An earlier version of this page said the area titles live in LDB category 50300 and that decoding it was the
unlock. **Both halves were wrong.** The client's own category descriptor table (recovered from the language
record — see [`ldb-string-index.md`](ldb-string-index.md)) names 50300 as `main_quest_templates.name`, and the
bounded-area names are category **51200 `bounded_areas.bounded_area`, which is not encrypted at all**. Decoding
50300 would yield quest names and would still never name a banner.

## The client's own name table for these regions: LDB category 51200

`bounded_areas.bounded_area` — **400 entries, plaintext**, and **374 of its 381 distinct strings are an exact
`<name>` of a region in this table**, which is what identifies it.

It matters because it is a **real id cross-reference**, the thing the per-banner matching above lacks. A quest
goal's `TrailingCategory`/`TrailingRefId` points into it: quest 10238's goal 1448 ("Enter the jungle") carries
`TrailingCategory 51200`, `TrailingRefId 180`, and `LDB[51200][180]` = **`"Trappers Haven"`** — a 7-vertex region
on Tortage Island spanning (402, 642)–(498, 725) that contains the goal's own marker at (416, 701).

So a `ReachArea` goal's real trigger volume resolves **mechanically**, with no geometry guessing:

```
goal.TrailingRefId --(LDB cat 51200)--> region name --(that playfield's 1000053 record)--> the polygon
```

That supersedes the "no id link, pick a region by judgement" reading in the quest-goal section above — which was
reached before the category table was known, and which guessed `Jungle Main` where the client actually says
`Trappers Haven` (a 96 × 82 region rather than a 392 × 529 one).

**Only three goals in the whole catalog carry a 51200 reference**, and all three regions exist:

| goal | quest | map | count | region | on playfield |
|---|---|---|---|---|---|
| 1448 | 10238 | 4010 | 1 | `Trappers Haven` | 4010 ✓ — contains the goal's own marker (416, 701) |
| 11712 | 17608 | 6400 | 3 | `NightmareRoomBA` | 6400 ✓ |
| 14073 | 19364 | — | 1 | `BossRoom` | 2027 ✓ (the goal has no MapId; the name is unique to one playfield) |

⚠️ A 51200 reference does **not** by itself make a goal a reach objective — 11712 wants **3** of something, so
its region reads as a *scope* ("3 of something in the Nightmare Room") rather than the objective. Settling that
is answered by the goal's own objective text, readable since #520: 11712 is *"Overcome the Emperor's Manifested
nightmares"*, a defeat objective the region merely scopes, while 1448 is *"Enter the jungle"* — a true reach. So
both halves are now mechanical: the kind from the text, the polygon from the reference.

## Import

Two modules read this table, and both import a DELIBERATELY SMALL slice of it — the regions some subsystem
actually consumes. The server has no RDB access, so anything the runtime needs has to come through here; what
nothing reads stays out.

`RdbRegionImporter` (the opt-in `rdb-regions` module) imports **14 regions / 59 vertices** into `MapRegion` +
`MapRegionVertex` and links each to its consumer:

- **11 resurrection-point discovery volumes** (#519) → `RespawnPoint.DiscoveryRegionId`, matched by containment:
  the smallest `*RezDiscovery* `region containing the point. `WorldSimulation` then tests the polygon instead of
  its 30-unit radius, which was a 60 × 60 box with no vertical bound against volumes of 16 × 20 to 29 × 33 and
  ~10 units tall.
  ⚠️ `Rez` is load-bearing and is the client's own discriminator. The looser `_Discovery` spelling also matches
  `FrostFields_Discovery` and `FotD_Discovery` — **area** discovery, 157 × 185 and 114 × 74 — and each of those
  contains a respawn point of the *same name*, so a containment test alone happily links them and widens two
  points' discovery to a box 25× the size. Different mechanic, matching name.
- **3 quest goal regions** (#521) → `QuestGoalDefinition.RegionId`, resolved through the goal's typed LDB
  reference (see below). `BossRoom` resolves even though goal 14073 carries no `MapId`, because the name occurs on
  exactly one hosted playfield; ambiguity is reported rather than guessed.

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
