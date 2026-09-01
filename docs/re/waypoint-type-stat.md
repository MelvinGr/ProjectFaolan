# A typed map pin is a STAT on a dynel — `WaypointType` (0xAC) — #921

`DispatchAddWayPoint` cannot make a typed pin: `FUN_013a18f0` zeroes the waypoint record's three type words
before calling the add, so every pin planted by that message gets the default look whatever is put in `i_cID` or
`i_nLabel` (#911). The type comes from somewhere else entirely.

## The mechanism

**A dynel gets a typed map pin by carrying stat `0xAC`.** The client's own stat-name table calls it
`WaypointType`, and `CharacterStatId.WaypointType` has carried that name all along — what was missing was the
link between the name and the behaviour.

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
level-80 characters' Poitain and Khitai play. Every observed value is in the accepted set. So this is not a
recovered-but-unexercised path: retail drives typed pins through the same stat channel we already send.

## What that settles about #921

The ticket offered three readings for "can a server make a typed pin at all". The scan decides between them:

* ⛔ **not reading 1** (another message we have never captured) — there is no message; the type never travels as
  one.
* ⛔ **not reading 2** (purely client-side) — retail pushes the stat 672 times.
* ⭐ **reading 3 is right**: the type is a stat on a dynel, so it is a **spawn-time / stat-push property**, and a
  world object gets a typed pin by being told its `WaypointType`.

## Which classes carry it

The field is on the dynel base, and six classes share the two vtable slots that gate the pin
(`FUN_00808310` places on stream-in, `FUN_008082f0` removes on stream-out):

`SimpleDynel` · `LootBag` · `Destructible` · `ClimbingDynel` · `CoreDoor` · `ProjectDestructible`

`CoreDoor` and `ClimbingDynel` are the interesting pair for quest targets and travel destinations.

⚠️ Nothing else in the image writes `+0x2FC`: the constructor zeroes it and the two slots above only read it as
a gate. `FUN_0080acd0`'s `0xAC` branch is the single writer, which is what makes the stat the whole story rather
than one route among several.

## What is still open

**What each value MEANS.** The six observed values are per-playfield appearance keys, resolved against
`Gui/Default/Waypoints/PF%u.xml` (`tooltip_title`, `tooltip_text`, `min_level`, `max_level`, `tooltip_image`,
`normal_bitmap`, `inactive_bitmap`, `dir_arrow`, `dir_arrow_inactive`) — so the name of a type is *client
content per playfield*, not a global enum. ⚠️ They are a different set from `ParseWPType`'s (`8`, `9`, `0x0D`,
`0x15`, `0x16`, default `custom`), which belongs to the script-command path, so the two must not be conflated.
Reading the `PF%u.xml` files for the playfields the 672 records were captured on is what would name them.

Open work tracked in Gitea #921.
