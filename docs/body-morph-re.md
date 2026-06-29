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

## SOLVED — dynamic analysis (gdb on the live char-create screen)

Found the runtime apply by searching the exe for morph action strings: `ApplyMorph` → **`FUN_00fede10`**
(blend-shape morph apply). gdb attached to the live client (no ASLR, image base `0x400000` = Ghidra
addresses; pass Wine signals `handle SIGUSR1/2/32-34/PWR nostop noprint pass`), with the Aquilonian-female
char-create body on screen. Breakpoint in the case-1 delta loop (`0x00fee405`) fires on a **SIZE-slider
drag** (not the "Random Body" button — that only sets values). Each hit exposes a morph entry (stride
`0x4c`: name@+0, weight@+0x1c, type@+0x24) and its delta list.

**The mechanism:** each body slider is a **deform-joint scale**, NOT a vertex blend shape. A morph's "delta
list" is a sparse set of `(nodeIndex u32, weight f32)` pairs (stride 8). `FUN_00fede10` accumulates
`weight × sliderValue × scale` into a per-node buffer, then writes each node's value to `node+0x40`; the
mesh, skinned to those nodes, follows. The 7 body morphs and their exact data (captured live):

| morph | min,max | joints (nodeIndex:weight) |
|-------|---------|---------------------------|
| arm | -0.05,0.05 | 6:.8 7:.8 8:.6 9:.4 + mirror 26-29 (shoulder→elbow falloff) |
| chest | -0.10,0.10 | 4:.6 5:.8 6:.3 7:.2 26:.3 27:.2 |
| bosom | -0.10,0.20 | 5:.2 76:.8 77:.8 (chest front, z>0) |
| stomach | -0.15,0.20 | 2:.35 3:.7 4:.35 5:.15 (spine) |
| arse | -0.10,0.10 | 2:.8 3:.3 4:.15 86:.2 93:.2 (pelvis+hips) |
| thigh | -0.05,0.10 | 2:.2 86:.8 87:.2 93:.8 94:.2 |
| leg | -0.035,0.10 | 86:.5 87:.8 88:.2 + mirror 93-95 (hip→ankle) |

**Node world positions** dumped live (`FUN_00fede10` node loop: array at `*(*(model+0x16c)+0x1a0)`, count
`+0x1a4` = 109 nodes, stride `0x50`, **world xyz at node+0x30**, morph scale at node+0x40). Validated vs
anatomy (arm sweeps shoulder y1.58→elbow y1.13; bosom y1.4 z+0.05 front; leg hip1.08→ankle0.07; etc.). Full
data with positions: `docs/morph-capture/body_morphs_aqf.json`. gdb scripts: `Captures/ghidra/dump_morph.gdb`
(delta lists), `dump_nodes.gdb` (node array).

DB slider [-100,100] → `t=slider/100` → client weight `= t × (t≥0 ? max : |min|)`. The viewer's
`BodyMorphDeformer` now applies this data-driven (radial soft-skin about each joint's world position, weighted
by the captured joint weight). Exact replication = standard linear-blend skinning with the captured node
skeleton + the mesh's BLENDINDICES/WEIGHT (ties into the #275 rig-import follow-up). Note the captured node
positions are the Aquilonian-female body in the muscular state; other race/sex bodies share the node topology
(same indices/weights) but have their own positions — re-dump per body for exactness.
