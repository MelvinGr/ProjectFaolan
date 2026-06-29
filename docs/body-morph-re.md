# Body-morph reverse engineering (#275)

Status of the attempt to recover AoC's *exact* body-proportion morph deformation from `AgeOfConan.exe`
(so the viewer can apply authentic morphs instead of the geometric approximation in
`Faolan.Core/Rdb/Assets/BodyMorphDeformer.cs`). Ghidra project: `/home/melvin/Development/Ghidra/Aoc`
(scripts in `Captures/ghidra/`, exe PE32 i386 stripped).

## What was recovered (solid)

- **Rendermesh chunk dispatcher** `FUN_00e4ec90` (`fmemesh.cpp`) switches on the 4-byte chunk tag:
  - `0x53554c43` **"CLUS"** → `FUN_00e51cb0` (cluster definitions)
  - `0x4d505044` **"DPPM"** → `FUN_00e51ef0` (**DPP = depth-pre-pass / alpha-safe mesh, NOT a morph target** —
    the reader's own assert string is *"DPP mesh for alpha-safe mesh %d"*). This kills the earlier theory
    that DPPM is the morph submesh; the viewer's geometric deformer is right to ignore it.
  - `0x30464e49` **"INF0"** → `FUN_00e51b40` (influence data)
  - `0x54535543` **"CUST"** → `FUN_00e51a50`
- **CLUS reader** `FUN_00e51cb0`: iterates a *pre-existing* cluster vector (mesh `+0x624` data / `+0x628`
  size, stride `0x2c`). Per cluster it reads only `u16 start (+0x1c)`, `u16 count (+0x20)`, `u16 (+0x24)`
  (constant `0x9030` in our blobs), `bool (+0x28)`. So CLUS supplies (start,count) ranges into a per-cluster
  influence array; it does **not** itself carry the cluster names or vertex lists (those are populated
  elsewhere, before this chunk).
- **CHARACTERCREATOR morph defs** `FUN_01106a60` builds XML query paths `%s/BodyNodes/MorphNode` and
  `%s/FaceNodes/MorphNode`, reads `MorphName`/`min_value`/`max_value`, and validates the slider value is
  within `[min_value, max_value]`. Confirms the BodyNode clamp model (BODY morphs clamp to ±0.1; the
  importer stores the raw slider ~[-100,100]). Other refs: `FUN_012900e0`, `FUN_012940b0`.

## What is still unknown (the blockers)

1. **Cluster vertex membership.** The CLUS `(start,count)` do NOT index either render index buffer to give
   name-coherent clusters — tested both the base index buffer and the DPPM index buffer in Python on the
   Cimmerian-female body (rm 1102537): the named clusters land on the wrong geometry either way ("thighs"
   at the feet, "neck" in the legs, etc.). So (start,count) index a *separate* per-vertex influence/pair
   array (probably the INF0 chunk), which is not yet decoded.
2. **The deform apply function** (the actual scale/offset math + per-cluster pivot/axis + slider
   normalization) has not been located. `+0x624` is too common an offset (37 unrelated accesses) to anchor
   on; the apply path is spread across the mesh-engine code.

## Conclusion

Ghidra clarified the *mechanism* and eliminated wrong theories (DPPM, simple index-range membership), but
extracting the exact deformation is a deep multi-round trace (decode the INF0 influence array, find +
decompile the apply, recover the pivot/axis/magnitude), not a constant lookup. The shipped approximation
(`BodyMorphDeformer`, geometric region deform keyed to normalized height + radial/front-back position) is
the practical stand-in. Resume points if revisited: decode the INF0 chunk grammar in the body blob; trace
callers that read the mesh cluster vector together with a float weight.
