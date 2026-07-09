# RenderMesh geometry grammar (RDB type 1010001) — #275 Phase 1

The geometry blob a `MESHINDEXFILE` (type 1010030) points at via `<RenderMesh file="ID"/>`. This is the format the
#275 feasibility ticket called the "hard blocker"; it is now **decoded and validated** (decode-first dissector:
`Captures/scripts/decode_rendermesh.py`). The C# reader is the next step.

## Container

Decompressed (the normal `IOz1`+zlib unwrap via `RdbReader.ReadDecompressed`) the blob is:

```
[12]   RDB-Id header     type u32 LE = 1010001, id u32, marker u32 = 1000003
[4]    version/flags     u32 (0x00009003 seen)
[4]    texRefCount       u32 N
[8*N]  texRefs           N × (type u32, id u32)   — the FCTX textures, same set as the manifest <Material>
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
vertex colour (BGRA u8×4), and UV(s) (half/packed). Decoding those is Phase 2 (needed for textured/lit render);
positions + indices alone already give the silhouette. Just before the anchor sits the declaration element table
(`[u16 offset][u16 size][u32 usage]` entries) which formally yields the stride+attribute layout — parse it for a
fully general reader rather than anchoring.

## Validation

`decode_rendermesh.py` validates every decoded position against the manifest's `<RenderMesh min_* max_*>` bounding
box (flat decor with `min_y==max_y==0` was the initial oracle). Verified across props, the full Ardashir Fort
(rm 4757467: **63 sub-meshes, 243 773 verts, 189 794 tris**, strides 32/36/44), and equipment — Last Legion helmet
(983v/1304t), Jhebbal Sag mask (547v/500t), ragged cloak (131v/196t) — all render as coherent meshes.

## Notes / next

- Multi-submesh meshes: iterate the anchor; concatenate with per-section vertex-index offsets (the dissector does).
- Some rendermeshes are CDN-only (`le.idx` file==0xFF) — fetch-by-hash (`RdbCdnCache`) like other on-demand resources.
- Character body meshes add skinning (bone indices/weights in the vertex attrs) — Phase 2/3, needed to pose a player.
- C# port: a `RenderMeshReader` beside `FctxConverter`, emitting positions+indices (then UVs/normals) → glTF for the
  Blazor viewer.
