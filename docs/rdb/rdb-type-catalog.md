# RDB type (category) catalog

Generated from `le.idx` (v5, 873,588 entries, 195 distinct types). Names from the
rdbreader `TYPES` enum + Project Faolan's own decodes (`*` = decoded/used by us). `local` = entries in
the local `NN.rdbdata` files; the rest are on-demand (CDN, `file==0xFF`). Size = sum of the index size
fields (compressed on-disk bytes).

| type | name / description | entries | local | cdn | size |
|-----:|--------------------|--------:|------:|----:|-----:|
| 3 | — | 3,727 | 0 | 3,727 | 1.0 MB |
| 1000001 | QA/test playfield data | 454 | 454 | 0 | 487.0 KB |
| 1000004 | — | 2 | 0 | 2 | 8 B |
| 1000007 | PHYSX30COLLECTION | 127 | 0 | 127 | 613.0 MB |
| 1000010 | Texture atlas def | 1 | 1 | 0 | 1.8 MB |
| 1000012 | * PhysX collision trimesh (LoS/heightmap #284) | 292 | 192 | 100 | 664.0 MB |
| 1000013 | KDv2 collision kd-tree (large) | 7,994 | 1 | 7,993 | 1.0 GB |
| 1000015 | Binary point/transform data | 618 | 240 | 378 | 8.9 MB |
| 1000016 | KDv3 collision kd-tree | 610 | 240 | 370 | 898.4 MB |
| 1000020 | * Feat/ability + item-template catalog (RDBH TEA-encrypted; #251/#137) | 98,048 | 98,048 | 0 | 23.5 MB |
| 1000028 | * CollisionMesh2 (KDv2 kd-tree; #275) | 9,382 | 6,257 | 3,125 | 116.7 MB |
| 1000042 | AI/spawn data (near STATICAIPOINTS)? | 1 | 1 | 0 | 53.0 KB |
| 1000043 | AI/spawn data (near STATICAIPOINTS)? | 1 | 1 | 0 | 62.2 KB |
| 1000044 | AI/spawn data (near STATICAIPOINTS)? | 1 | 1 | 0 | 868 B |
| 1000047 | * CameraPaths (e_RDB_CameraPaths, #80) | 1,071 | 242 | 829 | 426.0 KB |
| 1000048 | Spawn-point data | 1,060 | 241 | 819 | 35.6 KB |
| 1000049 | Point-light defs | 1,066 | 242 | 824 | 2.2 MB |
| 1000050 | Territories - DECODED: plain-text XML, 4,729 named polygons with a `<Height>` and a `(categoryname, categoryvalue)` pair. NOT trigger volumes and NOT a new vocabulary: the pair is the same Race/Community/Superfamily/Name space `NpcSpawnAreas` already carries from 1010506, and 488 of 488 groups on shared playfields already exist there. See [`bounded-areas-1000053.md`](bounded-areas-1000053.md#type-1000050--territories-and-why-they-are-not-a-naming-source) | 1,058 | 241 | 817 | 15.3 MB |
| 1000052 | QA/test playfield data | 2 | 2 | 0 | 11.0 KB |
| 1000053 | BOUNDEDAREAS | 478 | 204 | 274 | 3.9 MB |
| 1000054 | XML data | 325 | 132 | 193 | 421.2 MB |
| 1000055 | Bounded areas, COMPILED - the binary twin of 1000053: convex hulls (vertices + triangles) instead of polygon+height, framed with the wire's own `ListFrame` `(n+1)*0x3F1`. Redundant: 5,569 of its 5,731 names are already in 1000053, which holds 3,110 more over 135 extra playfields. Do not import. See [`bounded-areas-1000053.md`](bounded-areas-1000053.md#type-1000055--the-same-areas-compiled-do-not-import) | 383 | 189 | 194 | 2.2 MB |
| 1000057 | Armor/asset name index | 1,075 | 245 | 830 | 1.0 MB |
| 1000058 | Environment FX placement | 1,079 | 242 | 837 | 1.7 MB |
| 1000059 | Environment area defs | 1,059 | 242 | 817 | 70.7 KB |
| 1000060 | Binary point/transform data | 655 | 267 | 388 | 12.9 MB |
| 1000061 | Binary point/transform data | 802 | 237 | 565 | 211.1 KB |
| 1000069 | Nav/pathfinding grid? | 595 | 312 | 283 | 284.2 MB |
| 1000070 | CLIMBINGAREASEXPORT | 1,042 | 242 | 800 | 678.2 KB |
| 1000071 | World-data export (near CLIMBINGAREAS)? | 1 | 1 | 0 | 9.8 KB |
| 1000075 | RIVERS | 281 | 126 | 155 | 12.9 KB |
| 1000076 | * MorphTargets / customization registry (appearance tags+categories, #291) | 1 | 1 | 0 | 40.0 KB |
| 1000080 | World-data export (spawn/dynel)? | 1 | 1 | 0 | 80 B |
| 1000081 | World-data export (spawn/dynel)? | 1 | 1 | 0 | 1.2 KB |
| 1000082 | World-data export (spawn/dynel)? | 1,021 | 0 | 1,021 | 36.3 KB |
| 1000083 | TREASUREDATAEXPORT | 1,018 | 240 | 778 | 255.9 KB |
| 1000087 | Teleport data (XML) | 1 | 1 | 0 | 19.5 KB |
| 1000090 | RESURRECTIONPOINTSEXPORT | 1,024 | 240 | 784 | 299.7 KB |
| 1000094 | World-data export (spawn/dynel)? | 1 | 1 | 0 | 1.1 KB |
| 1000095 | CLIMBINGDYNELDATAEXPORT | 850 | 235 | 615 | 530.7 KB |
| 1000096 | FCTX texture | 307 | 248 | 59 | 179.8 MB |
| 1000097 | OCEANVOLUMESROOTNODEEXPORT | 846 | 236 | 610 | 124.7 KB |
| 1000098 | VOLUMETRICFOGSROOTNODEEXPORT | 770 | 229 | 541 | 100.2 KB |
| 1000099 | World-data export (spawn/dynel)? | 1 | 1 | 0 | 9.7 KB |
| 1000102 | Structure/asset name index | 743 | 231 | 512 | 6.5 KB |
| 1000103 | STATICAIPOINTS2 | 652 | 0 | 652 | 37.8 KB |
| 1000104 | * RDBQUESTGIVERS — the QUEST CATALOG (`QuestGiverList` protobuf; #569) | 1 | 1 | 0 | 332.4 KB |
| 1000105 | Playfield/zone metadata (protobuf) | 1 | 1 | 0 | 17 B |
| 1000107 | Playfield/zone metadata (protobuf) | 1 | 1 | 0 | 16 B |
| 1000108 | Playfield/zone metadata (protobuf) | 1 | 1 | 0 | 62.0 KB |
| 1000504 | Ambient sound zones | 1,071 | 1,071 | 0 | 558.9 KB |
| 1000505 | Social-rank name table | 1 | 1 | 0 | 380 B |
| 1000506 | Game data/enum table (1000500x) | 2 | 2 | 0 | 376 B |
| 1000507 | Game data/enum table (1000500x) | 2 | 2 | 0 | 864 B |
| 1000508 | * CREATURE RACES — the names behind `CharacterStats.race` (109 rows; `--report creature-races`) | 1 | 1 | 0 | 1.6 KB |
| 1000509 | Game data/enum table (1000500x) | 1 | 1 | 0 | 2.9 KB |
| 1000510 | Game data/enum table (1000500x) | 1 | 1 | 0 | 1.1 KB |
| 1000511 | Game data/enum table (1000500x) | 1 | 1 | 0 | 5.3 KB |
| 1000512 | Game data/enum table (1000500x) | 1 | 1 | 0 | 56 B |
| 1000513 | Per-class/level table: 12 classes x 80 levels, 24-byte records ([`client-tweak-table.md`](client-tweak-table.md)) | 1 | 1 | 0 | 23.1 KB |
| 1000514 | Game data/enum table (1000500x) | 1 | 1 | 0 | 16 B |
| 1000515 | UI/GUI element defs | 1 | 1 | 0 | 17.4 KB |
| 1000516 | Tweak (TWK_) tuning table - DECODED, 294 named f64 constants ([`client-tweak-table.md`](client-tweak-table.md)) | 1 | 1 | 0 | 11.3 KB |
| 1000517 | Race/nation name table | 1 | 1 | 0 | 137 B |
| 1000518 | Relation-title table | 1 | 1 | 0 | 1.1 KB |
| 1000519 | DECALPROJECTORS | 496 | 8 | 488 | 47.4 KB |
| 1000520 | Token/relic defs - DECODED: the `tokenID` table behind `DispatchUpdateTokenAmount` (s2c 0x204D). One 760-byte record, a 12-byte header then 29 fixed-shape entries `[u8][u8][u8 tokenId][u16 nameLen][name][7 bytes]`. The id byte is the only one of the three fixed prefix bytes that takes 29 distinct values, which is what identifies it; the ids are sparse (1..17, 19, 30..36, 50, 60..62) and four of them are corroborated on the wire. Names in [`token-pool-ids.tsv`](../re/token-pool-ids.tsv), enum `TokenPool`. The 7-byte per-entry tail is unread - it is fixed-width, and neither a varint nor a 3x u16 reading of it produces anything that looks like an item template id | 1 | 1 | 0 | 760 B |
| 1000521 | Game data/enum table (1000500x) | 1 | 1 | 0 | 704 B |
| 1000523 | Game data/enum table (1000500x) | 1 | 0 | 1 | 20 B |
| 1000620 | item-template catalog (RDBH, variant of 1000020) | 271 | 271 | 0 | 94.7 KB |
| 1000621 | Ability/effect catalog - name + description + a property run in the SAME field-id namespace as the spell record, but at a **9-byte stride** (`[fieldId u32][tag u8][value u32]`) and off word alignment, so the spell reader cannot see it. Carries the consumable-potion effects, which have no 1040005 record at all (`RdbSpellImporter.ReadTaggedFields`, #659) | 146 | 146 | 0 | 45.6 KB |
| 1000622 | EFFECTPACKAGE | 9 | 0 | 9 | 6.0 KB |
| 1000623 | SOUNDENGINE | 1 | 1 | 0 | 84.2 KB |
| 1000624 | Random name-gen list | 33 | 1 | 32 | 1.1 MB |
| 1000625 | Game data/enum table | 1 | 1 | 0 | 20 B |
| 1000626 | Random name-gen list | 1 | 1 | 0 | 3.7 KB |
| 1000627 | Teleport / zone-in points | 1 | 1 | 0 | 2.2 KB |
| 1000637 | Media/data (near MPEG/PNG 1000635)? | 129 | 0 | 129 | 2.9 KB |
| 1000638 | Footstep-sound map (protobuf) | 1 | 1 | 0 | 330 B |
| 1010001 | * RenderMesh geometry (mesh/hair; #275 rendermesh-grammar) | 95,289 | 51,135 | 44,154 | 5.8 GB |
| 1010002 | * Skeleton (rig bones; #275) | 1,034 | 1,034 | 0 | 2.1 MB |
| 1010003 | * Animation keyframe curves (per-bone T+R, #292) | 13,384 | 11,403 | 1,981 | 975.4 MB |
| 1010004 | * FCTX1 texture (DXT/MIXD; #275/#293) | 35,299 | 24,214 | 11,085 | 17.7 GB |
| 1010006 | FCTX2 texture | 969 | 375 | 594 | 216.1 MB |
| 1010008 | PNG | 3,713 | 3,713 | 0 | 33.7 MB |
| 1010012 | * `MTI1` tile index (accompanies some 1010013 map images) | 74 | 68 | 6 | 293.3 KB |
| 1010013 | * In-game map image (JPEG tile pyramid, by playfield; see [map-image-tiles-1010013.md](map-image-tiles-1010013.md)) | 653 | 73 | 580 | 762.6 MB |
| 1010026 | RTriMesh (runtime tri-mesh) | 84 | 84 | 0 | 1.2 MB |
| 1010028 | SCRIPTGROUP2 | 2,011 | 2,011 | 0 | 19.1 MB |
| 1010030 | * MESHINDEXFILE (mesh manifest XML; #275) | 29,854 | 29,854 | 0 | 38.5 MB |
| 1010031 | Attractor defs - DECODED: `[u32 typeId][u32 recordId][u32 1000002][u32 0x00010000][u32 count]` then count x `[u32 nameLen][name][f32 x4 quaternion][f32 x3 offset]` - the chain tiles the record exactly on all 400 sampled. A model's named ATTACHMENT POINTS (`attractor:weaponstart`, `attractor:guiitemcamera`) - WHERE an effect plays, which is what "on attractor 10" indexes. It does NOT say WHICH effect plays there | 2,771 | 2,771 | 0 | 546.0 KB |
| 1010034 | CollisionMesh (aux, not needed for display; #275) | 9,669 | 6,740 | 2,929 | 67.6 MB |
| 1010035 | * TEXTSCRIPT / texscript (tattoos, colours; #240/#291) | 819 | 819 | 0 | 434.9 KB |
| 1010040 | Texture (primary/gloss; per DataFilter refs) | 100 | 100 | 0 | 19.7 MB |
| 1010041 | Texture (primary/gloss) | 324 | 0 | 324 | 27.2 MB |
| 1010042 | TIFF | 218 | 185 | 33 | 191.3 MB |
| 1010050 | PNG image | 102 | 0 | 102 | 6.1 MB |
| 1010051 | PNG image | 99 | 0 | 99 | 10.0 MB |
| 1010060 | Binary point/transform data | 22 | 2 | 20 | 873.9 KB |
| 1010200 | * ANIMSYS (skeleton/anim XML: attractors, clip map; #292) | 733 | 664 | 69 | 3.8 MB |
| 1010201 | * MONSTERDATA (NPC model def; #76) | 3,185 | 2,873 | 312 | 1.7 MB |
| 1010202 | BCTGROUP | 476 | 476 | 0 | 947.6 KB |
| 1010203 | BCTMESH | 29 | 29 | 0 | 63.5 KB |
| 1010205 | MOVEMENTSET | 55 | 55 | 0 | 13.5 KB |
| 1010206 | Combo animation defs (XML) | 744 | 744 | 0 | 386.2 KB |
| 1010207 | Particle effect (zlib blob; #678) | 8,163 | 1,824 | 6,339 | 20.2 MB |
| 1010209 | * Map top-down image (PNG, by playfield; #284) | 172 | 172 | 0 | 87.4 MB |
| 1010210 | PNG4 | 344 | 344 | 0 | 20.7 MB |
| 1010211 | PNG5 | 344 | 344 | 0 | 29.0 MB |
| 1010213 | PNG6 | 8 | 8 | 0 | 57.9 KB |
| 1010214 | Effect-set defs (XML) | 4,549 | 3,050 | 1,499 | 3.0 MB |
| 1010216 | CARS | 8,602 | 8,602 | 0 | 4.1 MB |
| 1010217 | Geometry/vertex data (binary) | 138 | 138 | 0 | 68.6 MB |
| 1010218 | FCTX3 texture | 120 | 120 | 0 | 78.8 MB |
| 1010219 | Fatality group defs (XML) | 59 | 59 | 0 | 74.6 KB |
| 1010220 | Ground/foliage texture refs | 290 | 290 | 0 | 1.7 MB |
| 1010221 | GMSH geometry mesh | 108 | 46 | 62 | 19.7 MB |
| 1010222 | Lua/script data (near LUAARCHIVE)? | 1 | 0 | 1 | 20 B |
| 1010223 | * LUAARCHIVE (conversation/quest Lua; lua_vm) | 1 | 1 | 0 | 17.3 KB |
| 1010228 | near LUAARCHIVE? | 1 | 0 | 1 | 16 B |
| 1010230 | Lua script | 1 | 0 | 1 | 789 B |
| 1010300 | ROOMANDPORTALSYSTEM | 416 | 226 | 190 | 366.0 KB |
| 1010301 | Room/portal geometry? (near 1010300) | 9,889 | 3,766 | 6,123 | 95.0 MB |
| 1010302 | Room/portal geometry? (near 1010300) | 9,126 | 3,230 | 5,896 | 77.3 MB |
| 1010303 | Room/portal data (near 1010300)? | 1 | 1 | 0 | 764 B |
| 1010400 | FORMATION | 71 | 71 | 0 | 128.6 KB |
| 1010500 | ANIMSYS LMXB (anim data) | 493 | 493 | 0 | 926.0 KB |
| 1010501 | ANIMSYS LMXB (anim data) | 30 | 30 | 0 | 61.7 KB |
| 1010502 | ANIMSYS LMXB (anim data) | 750 | 750 | 0 | 480.6 KB |
| 1010503 | ANIMSYS LMXB (anim data) | 57 | 57 | 0 | 23.9 KB |
| 1010504 | ANIMSYS LMXB (anim data) | 72 | 72 | 0 | 148.1 KB |
| 1010505 | ANIMSYS LMXB (movement/anim data) | 9,320 | 9,320 | 0 | 5.4 MB |
| 1010506 | * NPC roam/movement data (open-world spawn layout, #332 — [decoded](npc-spawn-layout-1010506.md)) | 817 | 237 | 580 | 10.2 MB |
| 1010508 | Fatality group defs | 61 | 0 | 61 | 28.8 KB |
| 1010510 | ANIMSYS LMXB (anim data, CDN) | 1,072 | 0 | 1,072 | 23.7 MB |
| 1010511 | * ANIMSYS clip metadata (LMXB; clip->skeleton/rdbname/duration, #292) | 739 | 739 | 0 | 4.0 MB |
| 1010512 | ANIMSYS LMXB (anim data) | 652 | 209 | 443 | 37.3 KB |
| 1010513 | ANIMSYS LMXB (anim data) | 4,549 | 4,549 | 0 | 2.8 MB |
| 1010514 | ANIMSYS LMXB (anim data) | 3,185 | 3,185 | 0 | 2.0 MB |
| 1010700 | * CHARACTERCREATOR (per-race/sex appearance XML; #275/#291) | 3 | 3 | 0 | 110.4 KB |
| 1010701 | Faction relations (XML) — DECODED, the Khitai reputation ruleset ([factions.md](../subsystems/factions.md)) | 1 | 1 | 0 | 15.9 KB |
| 1010702 | ANIMSYS LMXB (anim data) | 2 | 1 | 1 | 8.3 KB |
| 1010703 | Playfield list (XML) | 1 | 0 | 1 | 372 B |
| 1020002 | OGG1 (audio) | 17,192 | 14,062 | 3,130 | 349.2 MB |
| 1020003 | LIP (lipsync) | 50,250 | 7,220 | 43,030 | 4.4 GB |
| 1020004 | OGG/audio | 20 | 20 | 0 | 2.2 MB |
| 1020005 | OGG3 (audio) | 296 | 264 | 32 | 2.2 GB |
| 1030001 | TDL1 locale plural-rules | 7 | 7 | 0 | 20.3 KB |
| 1030002 | * LDB / TextStrings (TDC2 localized strings; #291) | 1,103 | 1,103 | 0 | 110.8 MB |
| 1040005 | * Spell catalog (spell_cast_fsm) | 21,502 | 21,502 | 0 | 8.5 MB |
| 1040007 | NOT a spell table - keyed by PLAYFIELD (196 of its 198 local record ids are a `Map.Id`). `[u32 typeId][u32 playfield][u32 1][u32][u32 count]` then count x 16 bytes `[u16 index][u16 0xFFFF][u32][u32][u32]`. Framing carved, semantics unknown | 203 | 117 | 86 | 16.8 KB |
| 1040008 | NOT a spell table - keyed by PLAYFIELD (147 of its 147 local record ids are a `Map.Id`). `[u32 typeId][u32 playfield][u32 1][u32][u32 count]` then count x 16 bytes `[u16 index][u16 0xFFFF][u32][u32][u32]`. Framing carved, semantics unknown | 149 | 96 | 53 | 6.3 KB |
| 1040009 | NOT a spell table - keyed by PLAYFIELD (90 of its 90 local record ids are a `Map.Id`). `[u32 typeId][u32 playfield][u32 1][u32][u32 count]` then count x 16 bytes `[u16 index][u16 0xFFFF][u32][u32][u32]`. Framing carved, semantics unknown | 96 | 61 | 35 | 5.9 KB |
| 1040010 | BOUNDEDAREACOLLECTIONS | 670 | 199 | 471 | 184.7 KB |
| 1040023 | Spell-system data table (1040xxx)? | 1 | 0 | 1 | 56 B |
| 1060666 | Environment/main settings (XML) | 128 | 43 | 85 | 711.0 KB |
| 1060667 | ENVIRONMENTSETTINGS | 4,841 | 4,841 | 0 | 13.5 MB |
| 1060668 | SKYDOME | 559 | 242 | 317 | 5.9 MB |
| 1065100 | Name/profanity filter list - DECODED: `[u32 typeId][u32 0][u32 1][u32 count]` then count x `[u32 byteLen][UTF-8]`. 1,819 entries / 926 distinct, multilingual (DE/EN/ES/FR/PL + a large Korean block), most terms listed twice non-adjacently. Read by `RdbNameFilterList`, imported HASHED by `rdb-name-filter` - see [`character-names.md`](../subsystems/character-names.md) | 1 | 1 | 0 | 19.4 KB |
| 1065101 | NOT a text/filter table - 49 fixed 16-byte records after the same 16-byte header, keyed by a single byte 0x41..0xFE (`[u8 key][u8 0x42][u16 0x000F][u32 value][u32 flag][u32 0]`). Unrelated to 1065100's word list and still unread | 1 | 1 | 0 | 800 B |
| 1065102 | NOT a text/filter table - 1,142 sorted u32s that resolve against the LDB as SPELL ids (category 50210), not names. Header then a flat id array | 1 | 1 | 0 | 4.6 KB |
| 1065103 | Text/filter table (1065100x)? | 1 | 1 | 0 | 1.6 KB |
| 1065211 | Text/filter table (1065x)? | 1 | 0 | 1 | 16 B |
| 1066601 | Terrain/streaming data (1066xxx)? | 409 | 158 | 251 | 209.7 KB |
| 1066602 | Compressed streaming data (terrain?) | 127,843 | 30,729 | 97,114 | 4.1 GB |
| 1066603 | PNG2 | 33,132 | 22,231 | 10,901 | 2.7 GB |
| 1066604 | GHM1 terrain/height mesh? | 408 | 78 | 330 | 394.3 MB |
| 1066606 | FCTX4 texture | 6,180 | 1,516 | 4,664 | 1.7 GB |
| 1066609 | CTM1 collision/terrain mesh? | 372 | 76 | 296 | 27.9 MB |
| 1066610 | FCTX5 texture | 126 | 65 | 61 | 139.7 MB |
| 1066611 | * Heightmap (1536^2 float; #284) | 59 | 37 | 22 | 158.9 MB |
| 1070003 | * PLAYFIELDDESCRIPTIONDATA (tilemap/world bounds; #284) | 520 | 241 | 279 | 689.7 KB |
| 1070011 | DataFilter request (XML) | 133 | 0 | 133 | 24.3 KB |
| 1070012 | DataFilter manifest (XML) | 133 | 0 | 133 | 18.9 KB |
| 1070013 | DataFilter result (XML) | 133 | 0 | 133 | 16.6 KB |
| 1070014 | TGA image (Truevision) | 123 | 0 | 123 | 303.1 KB |
| 1070020 | Playfield/filter data (near 1070003)? | 4 | 0 | 4 | 16 B |
| 3097248 | — | 60 | 0 | 60 | 10.0 MB |
| 3107156 | FCTX texture (DXT) | 28,935 | 3,492 | 25,443 | 3.1 GB |
| 3107158 | FCTX texture | 59 | 0 | 59 | 4.5 MB |
| 3117154 | OGG audio (Vorbis) | 1,807 | 398 | 1,409 | 33.8 MB |
| 3117155 | OGG audio (Vorbis + LIP lipsync) | 50,422 | 7,955 | 42,467 | 3.0 GB |
| 3117157 | OGG audio (Vorbis) | 278 | 233 | 45 | 351.4 MB |
| 5194362 | Texture (CDN, FCTX-like) | 3,758 | 0 | 3,758 | 151.4 MB |
| 5204334 | PNG image (CDN, large; textures/art) | 29,245 | 0 | 29,245 | 25.6 GB |
| 5204505 | PNG image (CDN) | 2,898 | 0 | 2,898 | 534.3 MB |
| 5204511 | PNG image (CDN; UI/web) | 6,480 | 0 | 6,480 | 3.3 GB |
| 9388666 | PNG image (CDN) | 853 | 0 | 853 | 15.7 MB |
| 9388704 | PNG image (CDN) | 263 | 0 | 263 | 11.2 MB |
| 9398612 | PNG icon (CDN, tiny) | 33,567 | 0 | 33,567 | 862.1 MB |
| 9398638 | PNG image (CDN) | 29,440 | 0 | 29,440 | 324.8 MB |
| 9398809 | PNG image (CDN) | 3,124 | 0 | 3,124 | 20.4 MB |
| 9398815 | PNG image (CDN) | 7,025 | 0 | 7,025 | 99.6 MB |
| 9398825 | PNG image (CDN) | 137 | 0 | 137 | 1.8 MB |
| 4294967295 | Combo/ability tech defs | 12 | 0 | 12 | 8.2 KB |

**1010207 is the PARTICLE-EFFECT type**, not an unlabelled compressed blob (#678). Its record id is what a
`SimpleDynelVisualChangeIIR` `e_StartParticleEffect` carries - the client reads only the id off the wire and
pairs it with `1010207` itself, then errors as *"StartParticleEffect() Invalid particle resource id"* when it
does not resolve. The client's own combat log names the same pairing: *"Starting 7 visual effect [ResourceID
1010207:3667002] on attractor 10"*.

⚠️ **This page used to call the payload "an undecoded zlib blob (a binary effect graph, no strings)". BOTH
HALVES WERE WRONG**, each for its own reason, and both are worth keeping written down because each is a named
trap this catalog walked straight into.

**"no strings" was the `pack_grep_lies` trap.** The record is zlib, so a grep over the pack file finds nothing
and the text only appears once it is inflated. Inflated, all 8,163 records open cleanly and 559 carry the
original build-machine paths of the textures they draw with -
`W:\achardb\conan\particles\smoke\gradient_ab.png` and the like.

⭐ **"undecoded" was the `some_rdb_text_xml` trap: 189 of the 8,163 records are not the binary form at all -
they are the ARTIST'S AUTHORED XML, shipped verbatim.** A `<particlesystem>` document with a **192-tag schema**:
`<systems>` naming each emitter/particle pair, `<emitters>` with per-axis position and direction, emitter shape
(box / sphere / radius / height), `<particles>` with alignment, rotation and air resistance, `<textures>` with
blend mode and animation rows/cols, `<light>`, animated `<curve>`s built from `<controlpoint position="...">`,
and an `<exportpath>` carrying the source file's own name. So the effect graph's schema was never a
reverse-engineering problem - the client ships the documentation. Same shape as 1010203 being plain-text XML
while its 1010501 twin is the merged binary.

⚠️ **The two populations are DISJOINT** - a record is either the XML or the compiled binary, never both - so the
XML records are not a Rosetta stone for their own binary twins, because they have none. Zero of the 7,974 binary
records carry a source path. Decoding the compiled form still needs the client's reader; what the XML gives is
the exact field list to expect when someone writes one.

`--report particle-effects [--report-arg <substring>|xml:<id>]` lists the population, searching **by effect name
and by texture**, and `xml:<id>` prints one authored source whole. That answers the question the server actually
asks: it never renders an effect - it sends an id and the client does the rest - so what is needed is never
"what does effect 3667002 look like" but "which id is the one I want", and
`spell_demonologist_summonpet_gather35` answers that far better than a texture list does.

⭐ **One authored field is genuinely server-side: `<loopcountmax>`.** It separates a one-shot effect, which ends
by itself, from one that runs until something stops it - retail's authoring tool writes "forever" as a saturated
`50000000` / `2000000000` rather than a flag. That is the "does this id need a paired STOP, and how long does it
last" question, which is what a transient effect on a world object (#698) has to answer. It does NOT say which
id to send; that half is a different question and is unaffected. See
[`../re/iir-readers-combat-world.md`](../re/iir-readers-combat-world.md).

**Totals:** 873,588 entries · 410,382 local · 463,206 CDN · 84.6 GB indexed.
 192/195 types named (21 decoded by us).
