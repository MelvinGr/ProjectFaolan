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
  of the equipment-appearance lookup (same shape as the body-morph live-gdb work in `docs/body-morph-re.md`).

Until then the wardrobe lets you preview any set manually; auto-dressing a character in their own gear is blocked
on this mapping.
