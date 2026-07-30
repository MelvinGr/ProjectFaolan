# Bounded-area trigger volumes (RDB type 1000053) — #504

The client's **trigger-volume table**: one record per playfield holding every named region the playfield reacts
to — hazards, spawn camps, resurrection points, encounter triggers, collision blockers, audio regions. **478
records, 8,323 areas over 478 playfields.** The RDB record id **== the playfield id** (== `Map.Id`), the same
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

## What is in the 8,323 areas

Classified by name. Only the **lava** row is imported today (see `MapHazard` for why the rest are deliberately
left out — they are hazard-shaped by name with no wire evidence, and a mis-guessed kill volume is worse than a
missing one). The remaining buckets are unexploited and each touches an open subsystem.

| bucket | areas | maps | related |
|---|---:|---:|---|
| collision blockers | 1,542 | 160 | collision / line-of-sight (#284) |
| encounter + event start triggers | 757 | 176 | scripted encounters (#502) — retail's real trigger volumes, where ours are hand-seeded radii |
| audio / SFX / music regions | 541 | 122 | — |
| hazards: fire / death / misc | 445 | 79 | #504 |
| hazards: trap / spike | 185 | 29 | #504 |
| **hazards: lava** | **111** | **24** | **#504 — imported** |
| spawn camps | 111 | 20 | ambient spawns (#332/#363) |
| quest goal areas | 95 | 38 | `ReachArea` goals (#262) |
| resurrection points | 61 | 23 | resurrection spots (#372) |
| teleport / port areas | 44 | 25 | local travel + zone lines (#506) |
| zone / area names | 24 | 8 | area banners (#348) |
| hazards: drown / water damage | 14 | 12 | #504 |
| dismount areas | 8 | 2 | mounts (#27) |
| uncategorised | 4,385 | 343 | — |

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
