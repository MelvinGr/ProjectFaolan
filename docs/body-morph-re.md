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

## CLUS is NOT the morph system (the key reframe)

A second Ghidra pass settled it. The `FUN_00f73xxx` functions that consume the cluster vector
(`mesh+0x624`, stride `0x2c`) all live in **`SandBox\modelconstraints.cpp`** / `ModelInstance.h` and only
**toggle per-cluster status flags** ("pStatus") — enable/disable a body-part triangle group. The cluster
set includes `_L_arm_decapitation` / `_R_arm_decapitation`. So **CLUS is the body-part visibility /
constraint system** (gore dismemberment, hiding skin under armor), and its `(start,count)` are index-buffer
**triangle ranges** that partition the mesh for selective show/hide (that is why the ranges sum exactly to
the index count). It is not a proportion-morph carrier. The correct CLUS parse is
`[u16 start][u16 count][u16 0x9030][u8 flag][u32 nameLen][name]` (numbers BEFORE name) — but it does not
help morphs.

Other carriers eliminated this pass:
- **DPPM** = depth-pre-pass / alpha-safe mesh (assert *"DPP mesh for alpha-safe mesh %d"*).
- **INF0** (`FUN_00e51b40`) = bbox/pivot floats into `mesh+0x2f0` + a **vertex-lighting** buffer ("unable
  to create vertex lighting buffer"). Not morphs.
- The body mesh-index (1102540) bundles two extra resources (`type=5204334`, `type=9398638`) — both turned
  out to be **PNG textures**, not morph geometry.

## Where the body morph actually is (still open)

The CHARACTERCREATOR `BodyNodes/MorphNode` carry only `MorphName` + `min/max` (no target geometry, unlike
`FaceNodes` which name blend targets). The morph names (`bosom`, `thigh`, ...) are referenced ONLY by the
CC XML parser `FUN_01106a60` — so at runtime body morphs are applied **positionally** (a value array), not
matched by name. `FUN_01106a60` itself is reached only through a **vtable** (`DATA @0203ec08`), i.e. virtual
dispatch. There is no static string/const anchor into the runtime deform.

## Conclusion

Static RE has eliminated every obvious data path and established that the body morph is a positional,
vtable-dispatched deform with no static anchor — so the efficient route to the exact algorithm is **dynamic
analysis** of the live client (gdb / RenderDoc on a known-morph character: breakpoint/diff the body vertex
buffer between two morph values to catch the apply function), per [[gdb_live_client_debugging]]. The shipped
`BodyMorphDeformer` (geometric region deform) remains the stand-in until then. If the morph turns out to be
bone-scale, importing the rig (#275 follow-up) is the prerequisite to apply it.
