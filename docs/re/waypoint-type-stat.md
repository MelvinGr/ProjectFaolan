# A typed map pin is a stat on a dynel — `WaypointType` (0xAC)

`DispatchAddWayPoint` cannot make a typed pin: `FUN_013a18f0` zeroes the waypoint record's three type words
before calling the add, so every pin planted by that message gets the default look whatever is put in `i_cID` or
`i_nLabel`. The type comes from somewhere else entirely.

## The mechanism

**A dynel gets a typed map pin by carrying stat `0xAC`.** The client's own stat-name table calls it
`WaypointType` (`CharacterStatId.WaypointType`).

The setter is the dynel's stat-apply, `FUN_0080acd0(statId, value)`:

```c
if (statId == 0xac) {
    if (this->waypointType != 0) RemovePin();   // FUN_0080be00
    this->waypointType = value;                 // this + 0x2FC
    if (value == 0) return;
    PlacePin();                                 // FUN_0080bb20
    return;
}
```

So the stat **is** the pin: setting it non-zero places one of that type, setting it to 0 removes it. No waypoint
message is involved at any point. (`in_ECX` in that function is `this + 0x10C` — it addresses the object through
a shifted base, which is why the field reads as `in_ECX[0x7c]`; `0x10C + 0x1F0 = 0x2FC`.)

`FUN_0080bb20` then switches on the stored value and refuses anything outside its set, with the client's own
complaint — *"The waypoint type stat has a strange value."*

| accepted by the client | `0x0F` `0x10` `0x11` `0x12` `0x1A` `0x1B` `0x1D` `0xC748` |
|---|---|
| **observed in the corpus** | `0x0F` ×160 · `0x10` ×166 · `0x11` ×146 · `0x12` ×130 · `0x1B` ×38 · `0x1D` ×32 |

## It is capture-proven, and it is an ordinary stat push

**672 `StatIIR` (0xF98E10B3) records carrying stat 0xAC**, across 9 captures — Hakotep's Conarch sessions, both
level-80 characters' Poitain and Khitai play. Every observed value is in the accepted set. So retail drives typed pins
through the same stat channel we already send.

The type therefore never travels as a message of its own, and it is not purely client-side (retail pushes the stat
672 times): it is a **spawn-time / stat-push property**, and a world object gets a typed pin by being told its
`WaypointType`.

## Which classes carry it

The field is on the dynel base, and six classes share the two vtable slots that gate the pin
(`FUN_00808310` places on stream-in, `FUN_008082f0` removes on stream-out):

`SimpleDynel` · `LootBag` · `Destructible` · `ClimbingDynel` · `CoreDoor` · `ProjectDestructible`

`CoreDoor` and `ClimbingDynel` are the interesting pair for quest targets and travel destinations.

Nothing else in the image writes `+0x2FC`: the constructor zeroes it and the two slots above only read it as
a gate. `FUN_0080acd0`'s `0xAC` branch is the single writer, which is what makes the stat the whole story rather
than one route among several.

## What each value means — the client's own type table

The six observed values are not per-playfield keys. `Gui/Default/Waypoints/PF%u.xml` carries type
names (`destination` ×351, `region` ×79, `dungeon` ×41 — the `ParseWPType` path, `FUN_0139e6f0`) and never a
number, and `PlacePin` (`FUN_0080bb20`) does no mapping of its own: for every accepted value it fetches the
dynel's name for the tooltip and hands the stat value **verbatim** as the type word to the map's waypoint add
(`FUN_0139f520`). The meaning lives in **`RMWaypoint_c`'s constructor** (`FUN_01727820`, `MapGUI\RMWaypoint.cpp`),
a dense 38-way switch over the type word that loads each pin's bitmaps — the string table beside it
(`WorldmapGUI/minimap_*.tga`) is laid out in the same order. Composed from the jump table at `0x017289A8` and the
`MOV EDX,<string>` each branch performs (the decompiler folds them into argument-less `FUN_010d16a0()` calls, so
this needed the disassembly), it is `Faolan.Core/Enums/WaypointType.cs`:

| value | bitmap | | value | bitmap |
|---:|---|---|---:|---|
| 1 | `minimap_arrowself` (the player) | | 20 | `minimap_travel` |
| 2 | `minimap_player` | | 21 | `worldmap_dungeon` + `locked`/`unlocked` |
| 3 | `minimap_shop` | | 22 | `worldmap_hub` / `_premium` + `locked`/`unlocked` |
| 4 | `minimap_questgiver` | | 23–25 | `minimap_npc` |
| 5 | `minimap_quest_done` | | **26** | `minimap_massivepvp_active` |
| 7 | `minimap_resurrectionpoint` | | **27** | **`minimap_cloth`** |
| 8 | `minimap_marker_green` (legend: POI) | | 28 | `minimap_resurrectionpoint_red` (murderer) |
| 9 | `minimap_marker` (legend: custom waypoint) | | **29** | **`minimap_resourcegathering_active`** |
| 10 / 11 | `ctf_flag_red` / `ctf_flag_blue` | | 30–32 | the six quest location/direction/region bitmaps: untracked / tracked / selected |
| 12 | `minimap_tracking` | | 33–38 | `minimap_onslaught_enemyspawn` / `_enemy` / `_wildlifecamp` / `_lootmummy` / `_treasurechest` / `_boss` |
| 13 | `worldmap_playfield` / `_premium` + `locked`/`unlocked` | | **0xC748** | `minimap_travel` — the zone label, and the value IS its class word (`EntityClass.ZoneLabel`) |
| **15 / 16 / 17 / 18** | **`minimap_stone` / `_wood` / `_gold` / `_iron`** | | 0xC87B | `minimap_tombstone` + `_direction` — likewise `EntityClass.TombStone` |
| 19 | `worldmap_icon_dungeon3` | | 6, 14 | default — no bitmap |

The bold rows are the dynel-stat subset, and `WorldObjects.WaypointType` (proto field 7 of the spawn) already
holds them: **15** on the 48 rocks (Basalt … Slate), **16** on the 49 trees (Ash … Yew), **17** on the 43 lodes and
veins (Bismuth … Silver), **18** on the 41 deposits and ore nodes (Chromium …), **27** on the 11 cotton shrubs,
**0xC748** on the 104 zone labels. So a harvest node's pin is its resource kind's minimap icon, and the spawn
already sends it. 26 (massive PvP) is accepted by the dynel and never captured.

## 0x1D is a transient: the node being gathered

`ResourceGatheringActive` (0x1D) is the one value no row stores, because retail pushes it and takes it back
within a harvesting bout. All 32 captured pushes (16 bouts, every one a `Destructible` harvest node) follow one
rule, measured over the corpus [V2: demol80-demonologist_login_gameplay_annotated_1.tshark.json dynel 0x4D0]:

```
21:34:01  StatIIR 0x4D0  HealthCurrent 351 -> 251        the swing lands
21:34:01  StatIIR 0x4D0  WaypointType = 0x1D   (x2)       ... and the pin flips to "being gathered"
21:34:11  StatIIR 0x4D0  WaypointType = 0x11   (x2)       ten seconds after the last swing, the kind comes back
```

* **The flip rides the first swing of a bout.** Every −100 swing that was *not* followed by 0x1D landed on a node
  already showing it (23 of 23); every swing that *was* landed on a node at its own kind (12 of 12).
* **The restore is 10–15 s after the last swing** (10 s ×7, 12, 13, 14 ×5, 15 ×2 — never sooner), so a bout of
  several swings holds the pin for as long as the player keeps swinging plus ten seconds.
* **Every WaypointType push is doubled** — 672 frames are 336 identical back-to-back pairs, while the
  `HealthCurrent` push beside them is single. The server sends it twice for that reason alone.
* Half the bouts carry a `SimpleDynelVisualChange` `ChangeMesh`/`SetKdTree` pair in the same second — that is the
  node's mesh tracking its health tier (see `docs/subsystems/resource-harvesting.md`), not part of the pin.

Our server implements this as `Zone.MarkGathering` / `Zone.TickGatheringPins` (the countdown, `GatheringPinSeconds` = 10),
`WorldObjectService.BroadcastGatheringPin` (called by `TradeskillService.Harvest` beside the health push) and
`WorldObjectService.RestoreNodeWaypointType` (the tick's call through `IWorldOutbound`). Gated by
`HarvestGatheringPinTests`. A node whose row holds no `WaypointType` gets nothing — it never had a pin, and there
is no kind to come back to.

Open work tracked in Gitea #921.
