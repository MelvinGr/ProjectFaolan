# Worn-equipment rendering + wardrobe

The viewer dresses a character in authentic armour. Two halves: the **render pipeline** and the **item→look
mapping**.

## Render pipeline

Skinned armour meshes (`MESHINDEXFILE` with `<RenderSkinnedMesh>`, type 1010001 geometry) are authored in the
**same body-world bind-pose space as the body**, so they overlay with **no transform** — just concatenate their
sub-meshes onto the assembled character. Each piece textures itself from its own mesh-index materials / header
texture refs (`RdbAssetExtractor.ReadTexturedModel`).

- `RdbAssetExtractor.BuildCharacterFromParts(..., equipmentMeshIndexIds)` overlays the listed meshindex pieces.
- `GET /api/character/{id}/model.glb?equip=290433,290301,...` exposes it (comma-separated MESHINDEXFILE ids).
- Verified: the 0142 aquilonia_wool_female set — tunic `290433`, belt `290301`, wrist `291610`, boots `290359`
  (pieces classified by Y-bounds) — renders fully textured and body-conformed.

## Wardrobe catalogue

`ArmorSetCatalog.Build(rdb)` (`Faolan.Core.Rdb`) scans every `MESHINDEXFILE`, keeps the skinned pieces whose source path
is under `items\armor\_sets\NNNN_<name>_<gender>`, and groups them by `(set number, gender)`. ~273 female sets.

- `CharacterModelService.ArmorSets(sex)` caches it; `GET /api/armorsets?sex=Female` serves JSON.
- The viewer page shows a **wardrobe dropdown** when a character is selected and re-renders them wearing the chosen
  set's pieces (verified in-browser: Darkandskiny in "0004 cimmeria noble female").

## Item → look mapping

Auto-dressing a character in their *own* equipped gear works fully offline (RDB + `faolan.db` only); the worn mesh
is named by the item template itself, not by client code behind an indirect look-id.

- The look-id at item-template body offset @16 (13117 for the cotton set) is a **red herring**: a
  dye/appearance grouping that coincidentally collides with an unrelated resource id (meshindex 13117 is a
  Tortage building). It does not drive the mesh.
- **The real mapping:** each equipped item's decrypted 1000020 template carries its worn armour meshindex(es)
  in a **byte-packed trailing sub-record tagged by field-id 9** — `... 09 00 00 00 <meshIndexId u32> ...`. The
  value resolves to a `MESHINDEXFILE` (1010030) under `items\armor`. A word-aligned template scan misses
  it because the sub-record is UNALIGNED. An item may carry several field-9 entries: a male/female pair of one
  garment (pick by character sex) OR distinct pieces (the Slave Manacles carry both the handcuff mesh `700842`
  and a base cotton body `700815` — keep both).
- Verified for **Darkandskiny** (Cimmerian female; Chest = Silver Gown 324139, Legs = Silver Bottom 324171,
  Wrist = Slave Manacles 3361557): field-9 yields exactly `{323713, 323725, 700842, 700815}`, matching the
  in-client capture. Generalises across plate/cloth/helms/boots (each gives its M+F variants); weapons carry no
  field-9 armour mesh (correct — they are held meshes).

**Shipped:** `ItemLookResolver` (Faolan.Core.Rdb — field-9 extraction + per-item sex-variant collapse) +
`CharacterModelService.ResolveEquippedMeshes`. `GET /api/character/{id}/model.glb` with **no** `?equip`
auto-dresses the character from their `faolan.db` equipment, byte-identical to the explicit `?equip=` overlay.

> Do not delete a character's `Prefs/.../Preview.bin` — this client null-derefs on the missing file at
> char-select. The resolver does not need it.

### Rendering the worn rig (skinning / morph / alpha)

The `/characters` page requests `model.glb?rig=1` (a posable skinned rig), so gear has to skin + morph, not
just overlay statically:

- **Skinning:** the armour `MESHINDEXFILE` geometry carries its own per-vertex bone weights + palette (even
  pieces whose manifest is `<RenderMesh>` not `<RenderSkinnedMesh>` — `ReadModel` reports `IsSkinned=true`).
  `BuildCharacterFromParts` adds the equip meshes before skeleton selection, so `GlbWriter` skins the whole
  assembly (body + head + hair + gear) to one shared skeleton. `GlbWriter` only emits a *skinned* glb when
  EVERY submesh is skinned; a genuinely rigid gear piece would flip the model back to static — bind such
  pieces to their anchor bone (like the hair path) instead.
- **Morph:** the LBS body-morph loop runs over all body+gear submeshes, so worn gear conforms to a morphed body
  instead of clipping through it.
- **Alpha (3-way `ClassifyAlpha`):** AoC diffuse alpha is overloaded. Near-zero "holes" (>1% of texels) ⇒
  **Cutout** (glTF `MASK`, hair/lattice); else a large mid-alpha body (>10%) ⇒ **Blend** (`BLEND`, sheer cloth);
  else **Opaque** (ignore the channel — many armour maps put a spec/gloss gradient in alpha, which a plain
  `alpha<128` test mistakes for transparency and carves into ragged holes). `SubMesh.Alpha` (enum) drives
  `GlbWriter`.

Weapons/held items and non-armour slots carry no field-9 armour mesh, so they are not auto-dressed.
