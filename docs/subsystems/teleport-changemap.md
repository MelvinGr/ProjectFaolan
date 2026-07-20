# Change-map / teleport prep records (Gitea #219)

The two IIR bodies the retail server sends on the live connection before the `0x2002` enter-world handoff when
a player changes map, decoded from the changemap captures (frames 4200/4212 for map 2010, 4417/4420 for map
2000) and **named** against the IIR readers recovered in #233. Builder: `Faolan.Core.Network/Codecs/World/TeleportCodec.cs`,
byte-exact golden tests in `TeleportHandoffTests`.

Both carry the same **PlayfieldProxy** stream (the `FUN_00a6a600` sub-reader): `[u8 version='b'(0x62)]
[u32 ×8][u8 bool]`, where `u32[0]` is a constant playfield-class word (`0xC79C`) and `u32[1]` is the
destination map id.

## IntendedTeleportIIR_c (0x10D27BC0) — "begin teleport to map N"

Reader `FUN_005bfce0` = the PlayfieldProxy + a trailing bool.

```
[u32 0x10D27BC0][u32 0xC350 receiverClass][u32 clientInstance]
[u8 env=0x01]
[u8 'b'][u32 0xC79C][u32 destMap]   # the PlayfieldProxy (only the class word + dest map are non-zero)
[26x 00]                            # PlayfieldProxy u32[2..7]=0 + its bool + the trailing bool
```

## n3TeleportIIR_t (0x5A32F0D7) — destination announce

Reader `FUN_005d1ae0`: 7 leading words, the PlayfieldProxy, 2 words, a **length-prefixed blob** (`FUN_00cbf5d0`,
the spawn position), and a trailing bool. Every byte of the captured body is reader-accounted (108/108):

```
[u32 0x5A32F0D7][u32 0xC350][u32 clientInstance][u8 env=0x00]   # carrier
[6x u32 0][f32 1.0]                                # 7 leading words -> obj+0x2c.. (the 7th = 1.0)
[u8 'b'][u32 0xC79C][u32 destMap]                  # PlayfieldProxy (FUN_00a6a600): version + u32[0..1]
[u32 0][u32 0][u32 0]                               #   u32[2..4] = 0
[u32 1][u32 0x9C50][u32 destPlayfieldToken][u8 0]  #   u32[5]=ready-flag, [6]=0x9C50, [7]=token, + bool
[u32 0][u32 0]                                     # -> obj+0x6c / +0x70
[u32 len=20][u32 0x00018704][pos x/y/z f32 BE][u32 destDescriptor]  # the spawn-position blob (FUN_00cbf5d0)
[u8 0]                                             # n3TeleportIIR trailing bool -> obj+0x90
```

The fields earlier carried as "loose" are now placed precisely: **`0x9C50` and the playfield token are
`proxy[6]`/`proxy[7]` INSIDE the PlayfieldProxy** (with `proxy[5]=1` a ready/valid flag), and the **position +
`destDescriptor` form a 20-byte length-prefixed blob** — `[u32 0x18704][x][y][z][u32 descriptor]` — read by the
`FUN_00cbf5d0` sub-stream (a generic `[u32 len][len bytes]` reader). `destDescriptor` is the blob's trailing word.

### The destination-derived fields

`destPlayfieldToken` (`0x00503570` for map 2010 in the capture) is the **destination playfield-INSTANCE
token** — and it is **not a per-map constant**: it differs between captures of the same map (the changemap
capture's map-2010 token `0x00503570` vs our DB's stored map-2010 token `0x005045E6`, `Maps.PlayfieldToken`),
because the retail server mints a token per playfield instance. So the dest-announce only matches the destination world-entry if **both come from the
same burst**. The live change-map path now sources it via
`WorldBroadcaster.ResolveMapPlayfieldToken(destMapId)`, which reads the destination map's stored
`Map.PlayfieldToken` column (falling back to `WorldState.DefaultPlayfieldToken` for a map with no captured
token) — so the announce and the reconnect's world-entry stamp the **same** token (relevant to the #211
loading-screen hang). The live world-entry recovers the same token from the burst snapshot via
`WorldDespawn.TryExtractPlayfieldToken`. `TeleportCodec.BuildDestination` keeps the capture default only for
the golden test / a burst-less map.

`destDescriptor` (`0x14` for map 2010, `0xBE` for map 2000) is the position blob's trailing word — a per-zone
descriptor that does **not** correlate with the map's NPC / world-object / burst-record counts, so its meaning
is not yet derivable from the data; it stays a parameter with the capture default until another data point pins it.

## Status

Both bodies are built by the named `TeleportCodec` (was inline spliced templates in `GMCommands`), now fully
**reader-structural** — the `begin` / PlayfieldProxy / `dest` readers are decompiled (`tools/ghidra/
DumpTeleportReaders.java` → `FUN_005bfce0` / `FUN_00a6a600` / `FUN_005d1ae0`; the position-blob reader
`FUN_00cbf5d0` is dumped in `tools/ghidra/out/helpers.txt`) and every dest byte is accounted for. Sibling lift under #219 — the **guild renown block** — is
likewise a typed builder (`Faolan.Core.Network/Codecs/Guild/GuildRenownBlock.cs`, `GuildRenownBlockTests`; full reader walk
documented in `docs/subsystems/guilds.md`). Remaining: in-client verification of the GM teleport path, and the semantic
meaning of `destDescriptor`.
