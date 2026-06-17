# Change-map / teleport prep records (Gitea #219)

The two IIR bodies the retail server sends on the live connection before the `0x2002` enter-world handoff when
a player changes map, decoded from the changemap captures (frames 4200/4212 for map 2010, 4417/4420 for map
2000) and **named** against the IIR readers recovered in #233. Builder: `Faolan/GameServer/World/TeleportCodec.cs`,
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

Reader `FUN_005d1ae0` = 7 leading words, the PlayfieldProxy, 2 words, a sub-read, a bool.

```
[u32 0x5A32F0D7][u32 0xC350][u32 clientInstance]
[u8 env=0x00][24x 00][f32 1.0]            # 7 leading words (the 7th = 1.0)
[u8 'b'][u32 0xC79C][u32 destMap][15x 00] # the destination PlayfieldProxy
[01][u32 0x9C50][u32 destPlayfieldToken][12x 00][14][u32 0x00018704]
[pos x/y/z f32 BE]                        # spawn position
[u32 destDescriptor][00]
```

### The destination-derived fields (formerly "undecoded")

`destPlayfieldToken` (`0x00503570` for map 2010, `0x00503610` for map 2000) is the **destination zone's
playfield token** — the same per-zone token the simulation already mints as `Zone.PlayfieldToken`, not an
opaque constant. `destDescriptor` (`0x14` / `0xBE`) is a per-zone descriptor word. Both default to the
map-2010 capture values in `TeleportCodec.BuildDestination`; a real cross-map teleport should pass the
destination zone's actual token (see #211 for the loading-screen handoff).

## Status

Both bodies are now built by the named `TeleportCodec` (was inline spliced templates in `GMCommands`).
Sibling lift under #219 — the **guild renown block** — is likewise a typed builder
(`Faolan/CSPlayerAgent/GuildRenownBlock.cs`, `GuildRenownBlockTests`). Remaining: in-client verification of
the GM teleport path, and sourcing `destPlayfieldToken`/`destDescriptor` from the live destination zone.
