# Worn-equipment rendering + wardrobe (#275 Phase 9)

The viewer can dress a character in authentic armour. Two halves: the **render pipeline** (done) and the
**item→look mapping** (open RE).

## Render pipeline — DONE

Skinned armour meshes (`MESHINDEXFILE` with `<RenderSkinnedMesh>`, type 1010001 geometry) are authored in the
**same body-world bind-pose space as the body**, so they overlay with **no transform** — just concatenate their
sub-meshes onto the assembled character. Each piece textures itself from its own mesh-index materials / header
texture refs (`RdbAssetExtractor.ReadTexturedModel`).

- `RdbAssetExtractor.BuildCharacterFromParts(..., equipmentMeshIndexIds)` overlays the listed meshindex pieces.
- `GET /api/character/{id}/model.glb?equip=290433,290301,...` exposes it (comma-separated MESHINDEXFILE ids).
- Verified: the 0142 aquilonia_wool_female set — tunic `290433`, belt `290301`, wrist `291610`, boots `290359`
  (pieces classified by Y-bounds) — renders fully textured and body-conformed.

## Wardrobe catalogue — DONE

`ArmorSetCatalog.Build(rdb)` (Faolan.Core) scans every `MESHINDEXFILE`, keeps the skinned pieces whose source path
is under `items\armor\_sets\NNNN_<name>_<gender>`, and groups them by `(set number, gender)`. ~273 female sets.

- `CharacterModelService.ArmorSets(sex)` caches it; `GET /api/armorsets?sex=Female` serves JSON.
- The viewer page shows a **wardrobe dropdown** when a character is selected and re-renders them wearing the chosen
  set's pieces (verified in-browser: Darkandskiny in "0004 cimmeria noble female").

## Item → look mapping — OPEN (needs Ghidra)

A character's *actual* equipped item (e.g. Silver Gown, item 324139) does **not** carry its mesh in a decodable
field. The 1000020 item template (decrypt with `Item.Data1..4` TEA key via `--report item-template <itemId>`) has:

- a header region, then a property table of `[fieldId u32][value u32]` pairs (f148 = equip-slot bitmask:
  chest 0x20 / legs 0x10000 / hands 0x8000; f74 = value; f496/f499 = quality tier), then the display-name string
  (`0x21` tag + u16 len), then a trailing binary sub-record (item stats/effects).
- The visual is an **indirect "look id"** at template header offset @16 (cloth gown/bottom/tunic all share 13117;
  steelsilk pieces have their own 17153 / 18162). That id resolves to **no** armour mesh through any RDB type
  (13117 is *coincidentally* a Tortage building meshindex), and 18162 isn't an RDB entry at all — so the
  look-id→mesh table is almost certainly **client-side code**, not in the RDB. Cracking it needs a Ghidra trace
  of the equipment-appearance lookup (same shape as the body-morph live-gdb work in `docs/rdb/body-morph-re.md`).

Until then the wardrobe lets you preview any set manually; auto-dressing a character in their own gear is blocked
on this mapping.

### Ghidra investigation (2026-06-30) — subsystems mapped, resolution is templated client code

Static RE of `AgeOfConan.exe` (Ghidra project `/home/melvin/Development/Ghidra/Aoc`, no-ASLR base 0x400000 =
Ghidra image base, so FUN_ addresses are live runtime addresses) confirmed the architecture but not (yet) the
exact resolution function — the item/visual code is fcVector/template-heavy and string-cached, so the look→mesh
lookup has no clean static anchor:

- **`VisualCatMesh`** (`VisualCatMesh.cpp`) = the composited skinned character. `m_pcCatModel` @ `+0x16c` is the
  deform model whose node array (`*(*(model+0x16c)+0x1a0)`) the body-morph RE already used. Methods:
  `FUN_00fede10` (ApplyMorph), `FUN_00fe8b70` (reset/free m_pcCatModel), `FUN_00feee20` (per-frame node update).
  Worn armour meshes are added as instances to this VisualCatMesh.
- **`acgitemdata.cpp`** item-data class: `FUN_008c7fa0` / `FUN_008c8580` / `FUN_008c89e0` (templated template-field
  reader, string-cache `m_pcCache`) — where the item template's fields land in the runtime item struct.
- **RDB layer**: `FUN_01072430` = le.idx index PARSER (reads `IDBR` magic 0x52444249); `FUN_01073ef0` = index
  WRITER (CDN-update rebuild). `MeshLoader.cpp` geometry-stream parsers at `FUN_00dd0970/dd1430/dd1670/dd1880`.
- Equipment **attractor** strings exist (`PrimaryEquipVisualAttractor`, `SecondaryEquipVisualAttractor`,
  `SetVisualData()`), confirming held-item (weapon) mounts go through the ANIMSYS attractor path already documented
  in `docs/rdb/skeleton-grammar.md`.

**Recommended crack = live gdb (the method that solved the body morphs).** With the client logged in **in-world
wearing the Silver Gown** (char Darkandskiny / item 324139, look id 13117, slot chest), breakpoint the RDB
resource fetch and log every `(type, id)` load while the gown renders; the `type=1010030` (MESHINDEXFILE) load
that appears IS the gown's armour mesh — that directly gives `look 13117 + chest → meshindex → set`, no table
decode needed. Repeat for the Silver Bottom (legs) and a steelsilk piece to confirm the `(look, slot)→mesh`
relation. The console `ClientLauncher` does **not** render, so it won't trigger the mesh load — this needs the
real client driven to the world (you at the keyboard, I attach gdb), same as the morph capture. gdb setup +
Wine-signal passthrough: see `docs/rdb/body-morph-re.md` / memory [[gdb_live_client_debugging]].

**Resource-fetch breakpoint class identified:** `FUN_01070d60` is the **`LocalRDBSource_c` constructor** (builds the
`fcVector_x<RDBIndex_s>` / `RDBLocation_s` / `Bundle` / `BundleGroup` vectors — the in-memory RDB index + data-file
map). The universal "load resource by `(type,id)`" is a **method on `LocalRDBSource_c`** (vtable
`LocalRDBSource_c::vftable`). Breaking on that method and logging its `(type,id)` args while the gown renders is the
turn-key capture. Last prep before the live session: enumerate `LocalRDBSource_c::vftable` (VtableSlots.java) and
pick the get-by-id slot (two int args → blob/stream), or just break on the `0107xxxx` reads and filter
`type==1010030`.

## Update (2026-06-30): mechanism pinned to runtime attr 0x2da; precise capture identified

Offline RE narrowed the resolution exactly (no live client needed for these):

- **Look-id is not a direct meshindex.** `--report rdb-id 13117` → meshindex 13117 is the Tortage building
  `tortage_addon_c30.max`. The known armour look-ids share no consistent RDB type (13117 → {1010030,
  0x4f696e, 0x8f696e}; 17153 → {1010001, 1010301, 1010302}; 18162 → nothing local). So `(look)→mesh` is a
  client-runtime computation, not an RDB id-of-fixed-type lookup.
- **Body-armour builder `FUN_008b9ca0`** (thiscall; `[EBP+8]`=avatar, gate `[avatar+0x1b0]==2`):
  ```
  PUSH 0x30b ; CALL 0089bd80 → EAX → EBX            (attr 0x30b = secondary param)
  PUSH 0x2da ; CALL 0089bd80 → EAX                  (attr 0x2da = the meshindex id)
  MOV [ECX+0x14], 0xf696e                            (struct type = MESHINDEXFILE)
  0x008b9d05: MOV [ECX+0x10], EAX                    (struct id = attr 0x2da)  ← decisive
  CALL 0x005b07e0                                    (visual mesh loader)
  ```
  So the **body-armour meshindex id = runtime attribute 0x2da (730)**, type `0xf696e`. `FUN_0089bd80(obj,
  attrId)` is the generic runtime-attribute getter (aggregates base+mods); attr 0x2da/0x30b are NOT static
  template fields (the gown template has neither field 730 nor 779). ~7 sibling builders for other slots:
  008b9ca0/008b9d40/008afd00/008b00b0/008b0370/008b0600/0080acd0 (call-site EAX-dump addrs in
  `Captures/ghidra/capture_armorbuild.gdb`).
- **`VisualCatMesh` string is a logging tag** for the FUN_00fe*/00ff* visual subsystem (e.g. FUN_00feee20
  "Animpool is empty!"), not the resolver — earlier note retracted.

**Capture plan (supersedes the RDB-fetch-breakpoint plan above): cache-proof in-world re-equip.** The builder
recomputes the meshindex every time the avatar visual is (re)built, regardless of whether the mesh DATA is
cached — so the fragile "catch the load on world entry" path (and its loading-screen crash) is unnecessary.
Instead: be in-world, attach `Captures/ghidra/capture_armorbuild.gdb` (set its `attach PIDHERE` to the live
`Z:\...AgeOfConan.exe` wow64 pid), then **re-equip the gown** (server/GM) → breakpoint 0x008b9d05 logs
`meshindex=<id>`. Then `--report rdb-assets --report-arg <id>` extracts the actual gown mesh. Note `FUN_005b07e0`
got 0 hits as a world-entry breakpoint (client crashed mid-load before render) — it is the loader but not a
reliable world-entry trigger; the builder call-site is the right hook.
