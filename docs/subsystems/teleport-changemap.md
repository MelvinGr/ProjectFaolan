# Change-map / teleport prep records

The two IIR bodies the retail server sends on the live connection before the `0x2002` enter-world handoff when
a player changes map, decoded from the changemap captures (frames 4200/4212 for map 2010, 4417/4420 for map
2000) and named against the client's IIR readers. Builder: `Faolan.Core.Network/Codecs/World/TeleportCodec.cs`,
byte-exact golden tests in `TeleportHandoffTests`.

Both carry the same **PlayfieldProxy** stream (the `FUN_00a6a600` sub-reader): `[u8 version='b'(0x62)]
[u32 ×8][u8 bool]`, where `u32[0]` is a constant playfield-class word (`0xC79C`) and `u32[1]` is the
destination map id.

## A cross-map teleport is a mid-session server handoff

The two records below are the *prep*; what actually moves the session is a `DispatchRedirectToServer` — the
same method the login tier uses — carrying `host:port` in plain ASCII, followed by a fresh `Authenticate2` on the
new connection and only then the destination `PlayfieldFIIR`. Retail's world is sharded across game-server
processes by playfield, on one host:

| capture time | redirect | landed on |
|---|---|---|
| 537.5 | `gs013-nj4.ageofconan.com:7030` | 1020 Field of the Dead |
| 812.9 | `…:7028` | 1090 Ymir's Pass |
| 1103.2 | `…:7031` | **1092 The Amphitheatre of Karutonia** (the group instance) |
| 1243.3 | `…:7028` | 1090 Ymir's Pass — back to the same port as before |
| 1503.1 | `…:7029` | 2000 Old Tarantia |

(`NecroL80_..._chat-annotated-4`.) Two things fall out. A re-sent `PlayfieldFIIR` for the map you are already on
carries no redirect — only a genuine change does, so the redirect is the transition, not the announce. And an
instance gets its own port (`:7031` for the Amphitheatre) alongside the outdoor zones, which is what
`(mapId, instanceId)` looks like when a live service implements it: one process per copy.

The new port is advertised in plaintext ASCII on the old connection, one second before the move. The capture
tooling learns the port another way (a SYN-ACK sweep), but the advert is there and is decodable.

## Moving a non-player: a different mechanism, and a jump is not one packet

Retail describes its teleport family as moving *"self or another **dynel**"*, a dynel being any world entity —
NPCs included. Ours has two mechanisms rather than one with a branch, because the two subjects have nothing in
common:

| subject | mechanism | code |
|---|---|---|
| a player, cross-map | changemap handoff — persist, announce, redirect, re-run world entry on a NEW connection | `TeleportService.DoTeleport` |
| a player, same map | relocate in place, no loading screen | `WorldSimulation.RelocateSameMap` |
| **anything else** (NPC, corpse, loot bag) | move the world model, re-resolve the viewers | `WorldSimulation.RelocateEntity` |

An NPC has neither character nor connection, so nothing of the handoff applies.

**A large jump is not "a move or a despawn/respawn pair" — it is both, and which one a client gets is a property
of the viewer, not of the jump.** Three populations, and each needs a different message:

- a viewer who keeps the entity in sight → the mode-02 `RotateMoveTask` snap (`MoveTaskNodes.Rotate` with
  `from != to`, which is retail's own "a step the client snaps to" — and exactly why `BroadcastNpcMove`
  deliberately does *not* use it for the per-tick chase, where a snap reads wrong);
- a viewer who loses it → a despawn;
- a player near the destination who never saw it → a spawn.

The last two are what an interest pass already produces, so `RelocateEntity` snaps and then re-runs
`ResolveInterest` for every player in the zone. That is the interest sweep whose subject is "an entity moved
itself and the player did not" — a teleport is that, at maximum step size. Sweeping the zone rather than the
two interest radii is deliberate: the union of *was in range of the origin* and *is in range of the destination*
is what has to be re-resolved, and the pass is a cheap set-difference against each viewer's `Known` set that
sends nothing to a player whose view did not change.

Two limits, both refused by name rather than half-done:

- **Live only.** It does not touch `NpcSpawn`, the placement the entity respawns at — move a creature and kill
  it and it returns to where it was placed. Right for repositioning a stuck mob or staging a scene, and it means
  this is not a world-editing primitive.
- **Same zone only.** Crossing to another map means taking a handle out of one `Zone` and into another while
  every viewer of the old one still holds it. `/teleporttarget` says so when a playfield is named for an NPC
  target. Open work tracked in Gitea #917.

`MoveFrom` travels with `Position`: it is the FROM point of the segment the tick's broadcaster sends, so leaving
it at the old spot would render the entity's next ordinary step as a walk all the way back across the map.

## IntendedTeleportIIR_c (0x10D27BC0) — "begin teleport to map N"

Reader `FUN_005bfce0` = the PlayfieldProxy + a trailing bool.

```
[u32 0x10D27BC0][u32 0xC350 receiverClass][u32 clientInstance]
[u8 env=0x01]
[u8 'b'][u32 0xC79C][u32 destMap]   # the PlayfieldProxy (only the class word + dest map are non-zero)
[26x 00]                            # PlayfieldProxy u32[2..7]=0 + its bool + the trailing bool
```

## n3TeleportIIR_t (0x5A32F0D7) — destination announce

Reader `FUN_005d1ae0`: an arrival transform, the PlayfieldProxy, 2 words, a length-prefixed blob
(`FUN_00cbf5d0`, the spawn position), and a trailing bool. One grammar covers all 128 captured records
[V3: TeleportHandoffTests.ParseSameMapDestBody_ReadsTheRotationAsAQuaternion]:

```
[u32 0x5A32F0D7][u32 0xC350][u32 clientInstance][u8 env]   # carrier; env 0 s2c (113), 1 on the client's echo (15)
[f32 x][f32 y][f32 z]                              # the ARRIVAL position    -> obj+0x2c..
[f32 qx][f32 qy][f32 qz][f32 qw]                   # the ARRIVAL rotation, a unit quaternion
[u8 'b'][u32 classWord][u32 destMap]               # PlayfieldProxy (FUN_00a6a600): version + u32[0..1]
[u32 0][u32 owner][u32 0]                          #   u32[2..4] - the owner pair, see PlayfieldProxy
[u32 slot5][u32 0x9C50][u32 destPlayfieldToken][u8 0]  #   u32[5..7] + the proxy bool
[u32 0][u32 0]                                     # -> obj+0x6c / +0x70
[u32 len][u32 0x00018704][pos x/y/z f32 BE][u32 destDescriptor]  # the spawn-position blob (FUN_00cbf5d0)
[u8 trailing]                                      # n3TeleportIIR trailing bool -> obj+0x90
```

### The two forms are the blob's length, not two grammars

The census splits cleanly and the length word states which side a record is on — no heuristic:

| | records | leading transform | blob | trailing | proxy class |
|---|---:|---|---|---:|---|
| **cross-map announce** (a loading screen) | 92 | zero + identity | 20 B — the arrival | `0` | `0xC79C` (`0xCABC` once, epic) |
| **same-map relocate** (no loading screen) | 36 | the arrival | **absent**, `len = 0` | `1` | `0xC7A0` |

The leading block of a cross-map announce ("six zero words and a `1.0f`") is not a discriminating constant: it is
the same position + quaternion pair at zero and identity — the same trap the `0x3F1` list-frame notes describe,
one field wider.

The four words after the position are a quaternion, not `facingX, 0, facingZ` with two zero words:
`x²+y²+z²+w²` is 1.0 to within 1e-4 on every one of the 128 records, including the 34 whose `x` and `z` are
non-zero. Writing a horizontal facing direction into two of those slots encodes a rotation of twice the intended
yaw.

`0x9C50` and the playfield token are `proxy[6]`/`proxy[7]` inside the PlayfieldProxy (with `proxy[5]=1` a
ready/valid flag), and the position + `destDescriptor` form a 20-byte length-prefixed blob — `[u32 0x18704][x][y][z][u32 descriptor]` — read by the
`FUN_00cbf5d0` sub-stream (a generic `[u32 len][len bytes]` reader). `destDescriptor` is the blob's trailing word.

### The destination-derived fields

`destPlayfieldToken` (`0x00503570` for map 2010 in the capture) is the destination playfield-instance
token — and it is not a per-map constant: it differs between captures of the same map (the changemap
capture's map-2010 token `0x00503570` vs our DB's stored map-2010 token `0x005045E6`, `Maps.PlayfieldToken`),
because the retail server mints a token per playfield instance. So the dest-announce only matches the destination world-entry if both come from the
same source. The live change-map path sources it via
`WorldBroadcaster.ResolveMapPlayfieldToken(destMapId)`, which reads the destination map's stored
`Map.PlayfieldToken` column (falling back to `WorldState.DefaultPlayfieldToken` for a map with no captured
token), and the reconnect's world entry stamps the zone from the same column — so the announce and the entry carry
the same token; a mismatch hangs the loading screen. `TeleportCodec.Build` keeps the capture default only for
the golden test / a burst-less map.

`destDescriptor` (`0x14` for map 2010, `0xBE` for map 2000) is the position blob's trailing word — a per-zone
descriptor that does not correlate with the map's NPC / world-object / burst-record counts, so its meaning
is not derivable from the data; it stays a parameter with the capture default.

## Zone-line doors: where a destination comes from

A zone-line door is a `WorldObject` (a `DoorFullUpdateIIR` `0x33A56FB0`, or a `SimpleDynelFullUpdateIIR`
`0x66AEDD50` where the door backs a building entrance) whose `InteractAction` is `Teleport` (same playfield) or
`ProxyTeleport` (cross playfield). Using it runs the records above. The destination has three independent
sources, in a strict precedence, and which one a row has decides what happens on use:

| # | source | what it yields | what the server does |
|--:|---|---|---|
| 1 | **The wire.** A captured `UseObject` → `IntendedTeleport` + `n3Teleport` (`WorldTeleportExtractor`) | destination map and arrival position | teleports to the exact arrival — same-playfield doors relocate with no loading screen |
| 2 | **The client's own teleport table**, RDB **1000087** — `RdbTeleportExtractor` | destination map only | change-maps to that map's default spawn |
| 3 | **The door's own name.** Its name is the playfield behind it, matched against the client's playfield table (`Maps`, RDB 1070003) — `ZoneLineNames` | destination map only | as above |
| — | none of them | nothing | the door reports that it has no known destination |

Each pass runs only over doors the pass above it left inert, so the order *is* the precedence.

### Tier 2 — the client's own table

One plain-text XML record (`1000087:0`, ~140 KB) stating 675 teleports over 298 source playfields, reaching
212 destinations, as `SourcePlayfieldID` + `SourcePosX/Y/Z` + `DestinationPlayfieldID`. Against that, the
capture harvest holds 30 links: it can only ever see a door somebody walked through on camera.

**The element has exactly those four attributes** — censused across all 675, not read off the first one. Two
absences follow, and both shape how it is used:

- **No arrival position.** Which is why it ranks below the wire, and why it writes a null arrival rather than an
  invented one — the default-spawn fallback already exists for exactly this case.
- **No door name.** So the match back to a `WorldObject` row is position-only, where tier 1 can also require
  the name to agree. A position-only key fails by picking the *neighbouring* door rather than by missing, so it
  is guarded twice: a hard 8 u cap, and a runner-up test that declines when a second door is nearly as close.
  Measured on the shipped data — 58% of matchable rows land within 0.5 u, 90.9% within 8 u, and zero are
  ambiguous.

**Why the wire outranks it.** Where both sources speak (45 doors), 41 agree. The
four that differ are all day/night or state variants of the same place — `Renton's House` (4572) vs
`Renton's House Night` (4582), `Villa Camillus` (2141) vs `Villa Camillus - Cursed` (2146). So the table is a
design-time export naming a canonical destination and the runtime picks the variant: the table is right
about *which place*, the capture about *which instance of it*. Two of those four disagreements are against a
name guess rather than a capture, and there the table is plainly the better answer — on `City of Tortage Night`
its `Renton's House Night` beats the name match's daytime `Renton's House`. That is the whole argument for
slotting it between the two existing tiers rather than at either end.

587 of the 675 rows are on maps with no imported world objects, so only 88 can be matched at all
and 60 land. That is the world data being incomplete, not a defect in the table — the yield grows on its own as
maps are imported. `--report teleport-match` prints the whole measurement.

### Tier 3 — the door's name

It is checked against the capture harvest, which is the only ground truth: of the captured cross-map
doors whose name matches a playfield, every one names the playfield the capture actually teleported to
(Conall's Valley 1010, Conarch Village 1000, Field of the Dead 1020, Spider Caverns 1004, Ymir's Pass 1090,
Tarantia Noble District 2010, Old Tarantia 2000, Thunder River 2090, City of Tortage 4020).

Two guards keep it honest, both from cases the harvest contradicts:

- **A door that names its own playfield is not a zone-line.** The `City of Tortage` doors standing in City of
  Tortage relocate *within* it; the night twin is a different map id (4022 vs 4020), so the base name — not the
  id — is what recognises them. Left inert rather than sent to the wrong Tortage.
- **A playfield that defines no respawn point cannot be a destination while a same-named one does.** The client
  ships duplicate playfield rows (`Conall's Valley` = 508 *and* 1010, `Old Tarantia` = 2000 *and* 2002); the
  harvested truth is always the one with respawn points. A genuine tie (`Outflow Tunnels` 2026/2027, `Thirsty
  Dog Inn` 4533/4534) stays unresolved and reports rather than guessing.

A door whose name matches no playfield is left alone: from the data, a `Lever` and a zone-line to a playfield
the client does not ship are indistinguishable.

### The confirmation step

Some destinations ask first: *"Do you want to travel to Ymir's Pass?"* — a `DispatchScryShowMessageBox` sent
between the use and the teleport, whose answer drives the redirect. It is per destination map, not per
door and not "the door crosses a map": half the captured walk-throughs raise no box at all, and the ones that
do split cleanly by destination. `world-teleports` harvests it into `Map.TravelConfirmKey` alongside the
destination; an empty key means the door teleports straight through. Full grammar in
[`scry-message-box.md`](scry-message-box.md).

## Decode coverage

Both bodies are built by `TeleportCodec`, reader-structural — the `begin` / PlayfieldProxy / `dest` readers are
decompiled (`tools/ghidra/DumpTeleportReaders.java` → `FUN_005bfce0` / `FUN_00a6a600` / `FUN_005d1ae0`; the
position-blob reader `FUN_00cbf5d0` is a generic `[u32 len][len bytes]` sub-stream) and every dest byte is
accounted for. The guild renown block is likewise a typed builder (`Faolan.Core.Network/Codecs/Guild/GuildRenownBlockCodec.cs`, `GuildRenownBlockCodecTests`; full reader walk
documented in `docs/subsystems/guilds.md`). `destDescriptor` is placed and round-trips but nothing names what it
selects, and the GM teleport path has never been checked against the retail client — open work tracked in
Gitea #665.
