# RenderMesh geometry grammar (RDB type 1010001)

The geometry blob a `MESHINDEXFILE` (type 1010030) points at via `<RenderMesh file="ID"/>`.

> **The manifest comes in two forms and its XML must be carved by ROOT TAG.** Of the 29,854 type-1010030
> records, **28,337** are the `<MeshIndexFile>` XML and **1,501** are an older LINE-ORIENTED export carrying the
> same fields (`Source: <path>` / `RenderMesh: <id>` / `CollisionMesh: <id>` / `OccluderMesh: <id>`, no materials);
> the remaining 25 are header-only stubs or binary records misfiled under the type. `MeshIndexFile.Parse` reads
> both (29,829 parsed), and anchors the XML on `<MeshIndexFile` via `RdbXml.Carve`. Anchoring on the **first `<`**
> lands inside the binary 12-byte RDB-Id header whenever one of its bytes is `0x3C`, which silently empties 1,693
> manifests — a data-dependent failure, the record parsing or not depending on its own id.

The geometry is decoded and validated (decode-first dissector: `tools/capture/decode_rendermesh.py`) and ported to
C# — `RenderMeshReader`
(`Faolan.Core.Rdb/Assets/RenderMeshReader.cs`), which drives the `GlbWriter` → glTF pipeline into the Blazor viewer.

## Container

Decompressed (the normal `IOz1`+zlib unwrap via `RdbReader.ReadDecompressed`) the blob is:

```
[12]   RDB-Id header     type u32 LE = 1010001, id u32, marker u32 = 1000003
[4]    version/flags     u32 (0x00009003 for static props; skinned bodies use 0x00007000)
[4]    texRefCount       u32 N
[8*N]  texRefs           N × (type u32, id u32)   — the FCTX textures, same set as the manifest <Material>
                         (skinned 0x7000 blobs instead store bare N × (id u32) with the type implied FCTX 1010004;
                          `RenderMeshReader.ReadHeaderTextureRefs` disambiguates by testing the first word)
[8]    0xFFFFFFFF × 2     null/terminator
...    material params    ASCII "g_Name=… g_ExporterVersion=NNN" block(s), NUL-terminated (may be absent)
...    geometry section(s) — one or more, see below
...    trailing chunk      a "DPPM" (0x4450504d) tagged block — not needed for display
```

## Geometry section (repeats; one per sub-mesh / LOD)

Each section begins right after the **vertex-declaration end anchor**:

```
ANCHOR  FF 00 00 00 11 00 00 00        D3D-style decl-end sentinel (0xFF) + 0x11 section marker
[4]     stride            u32          bytes per vertex (32 / 36 / 44 observed — format varies per mesh)
[4]     vertexCount       u32
[V*S]   vertex buffer     vertexCount × stride bytes; bytes [0:12] = position float x,y,z (LE)
[4]     triCount          u32
[T*3]   index buffer      triCount × 3 × u16   (triangle list, indices < vertexCount)
```

The bytes after the position (stride-12) are the packed vertex attributes — normal/tangent (packed bytes),
vertex colour (BGRA u8×4), and UV(s) (half/packed); positions + indices alone already give the silhouette.
`RenderMeshReader` parses the declaration element table that sits just before the anchor (walked backward as
8-byte `D3DVERTEXELEMENT9` records: `[u16 stream][u16 offset][u8 type][u8 method][u8 usage][u8 usageIndex]`) rather
than assuming a fixed stride, so it decodes NORMAL (UBYTE4N), TEXCOORD (FLOAT2 for props / SHORT2N for skinned
bodies), and BLENDINDICES/BLENDWEIGHT skinning per element.

## Validation

`decode_rendermesh.py` validates every decoded position against the manifest's `<RenderMesh min_* max_*>` bounding
box (flat decor with `min_y==max_y==0` was the initial oracle). Verified across props, the full Ardashir Fort
(rm 4757467: **63 sub-meshes, 243 773 verts, 189 794 tris**, strides 32/36/44), and equipment — Last Legion helmet
(983v/1304t), Jhebbal Sag mask (547v/500t), ragged cloak (131v/196t) — all render as coherent meshes.

## Notes

- Multi-submesh meshes: iterate the anchor; concatenate with per-section vertex-index offsets (both the dissector
  and `RenderMeshReader.Read` do — it returns one `SubMesh` per section).
- Some rendermeshes are CDN-only (`le.idx` file==0xFF) — fetched through the CDN mirror like other on-demand
  resources (`RdbReader.CanRead`).
- Character body meshes add skinning (bone indices/weights in the vertex attrs): `RenderMeshReader`
  emits per-vertex `BlendBones`/`BlendWeights` and binds each section to its `g_BoneN=` palette, and `RigSkinningTests`
  covers it. Bone names resolve against `SkeletonReader`.
- Shipped C# pipeline: `RenderMeshReader` (beside `FctxConverter` in `Faolan.Core.Rdb/Assets/`) → `SubMesh`
  positions/normals/UVs/skinning → `GlbWriter.Build` → glTF/glb for the Blazor viewer.
