# Skeleton + attractors (RDB types 1010002 / 1010200) — #275 Phase 3

The bind-pose skeleton a skinned character mesh / animation system references. Decoded + validated against the
71-bone player skeleton (id 14853) by the C# `SkeletonReader` (`Faolan.Core.Rdb/Assets/SkeletonReader.cs`) and the
`--report rdb-skeleton` tool — byte-exact (the parse consumes the resource whole) and the composed world positions
are anatomically correct. (Skeletons live in the client RDB, not the packet captures.)

## Skeleton — RDB type 1010002

```
[12]  RDB-Id header     type=1010002, id, marker=1000002
[4]   version           u32 (0x10000)
[4]   boneCount         u32
per bone:
  [4]              nameLen u32
  [nameLen]        name (e.g. "rig_bone_Head")
  [16]             local rotation  float4 quaternion (x,y,z,w)
  [12]             local translation float3 (relative to parent)
  [4]              childCount u32
  [4*childCount]   child bone indices u32
```

Parents are recovered from the child lists. World transform = compose down the chain:
`world = localRotation * localTranslation * parentWorld` (row-vector convention). Composing the player skeleton
gives anatomically-correct world positions — Hips y=1.057, Spine2 1.480, Neck 1.766, **Head 1.898**, LeftHand
(-0.641, 1.101), LeftFoot (-0.328, 0.115) — and the parse consumes the resource to the exact byte (4209/4209).

`--report rdb-skeleton --report-arg <id>` prints key bone world positions.

## Attractors (equipment / effect mount points) — ANIMSYS, RDB type 1010200

ANIMSYS is XML: `<animsys skeleton="ID">` + a list of `<attractor name=".." type="N"><relative bone="rig_bone_X">
<translation>x y z</translation></relative></attractor>`. These are the named attach points — e.g. `ChestArmor` →
`rig_bone_Spine2` + (0, 0.1, 0.13), `eyes` → `rig_bone_eye_right`. So an equipment mesh's world placement =
attractor bone's world transform (from the skeleton above) ∘ the attractor's local offset.

## Using it for character assembly (#275 Phase 3 remaining)

- **Head**: place the head mesh at `rig_bone_Head` (or Neck) world transform. (A geometric fallback — aligning the
  body's top ring to the head's base — also works for a static view.)
- **Equipment**: place each worn item mesh at its ANIMSYS attractor (bone world transform ∘ offset).
- **Caveat**: the player BODY skin-cluster bone names (`neck`, `chest`, `L_elbow`, from the mesh's CLUS section)
  differ from the rig names (`rig_bone_Neck`, …) — the body→rig bone-name mapping still needs to be established to
  drive skinning from this skeleton (not needed for a static bind-pose assembly).
```

## Rig import — binding the mesh to the skeleton (#275 Phase 10)

A skinned RenderMesh (`<RenderSkinnedMesh>`, type 1010001) carries, per vertex, `BLENDINDICES` (UBYTE4 @ decl
offset 32) and `BLENDWEIGHT` (UBYTE4N @ 36, each `/255`, four summing to ~255). It does **not** index the skeleton
directly:

- **Matrix-palette register stride.** The on-wire BLENDINDICES are `bone * 2` (the engine stores each bone as two
  shader constant registers), so the body uses values `0,2,4,…,102`. Halve them to get the **local palette index**
  (`0..51` here).
- **Per-mesh bone palette.** The local index maps to a bone **name** via the palette exported in the rendermesh's
  ASCII material/param string: `g_Bone0="rig_bone_Head" g_Bone1="rig_bone_Neck" … g_NumberOfBones=N`. The palette
  is a compact, mesh-specific subset/reordering of the skeleton (the body's 52 entries are root→offhand; the
  head/neck/face bones are absent because the head is a separate mesh).
- **Resolve to a joint by NAME.** `skeletonJoint = indexOf(skeleton.bones, palette[localIndex].Name)`. Using the
  local palette index as a direct skeleton index scrambles the limbs (it passes a rest-pose check but fails an
  anatomical bone↔vertex-cluster check, because `inverseBind = inverse(boneWorld)` makes rest-pose identity hold
  for *any* assignment).

`GlbWriter.Build(subs, png, skeleton)` emits the glTF skin: one node per bone (`translation`+`rotation` from the
bone's local TRS, parented via the child lists), a `skin` with `inverseBindMatrices[j] = inverse(bone[j].World)`,
and per-primitive `JOINTS_0` (UNSIGNED_BYTE VEC4, name-remapped) + `WEIGHTS_0` (FLOAT VEC4, renormalised). The
inverse-bind matrix is written in `System.Numerics.Matrix4x4` natural field order, which is exactly glTF's
column-major encoding of the equivalent column-vector matrix. Because the skeleton's bind-pose world and the mesh's
bind-pose vertices share one frame and scale, the skinned mesh at rest is pixel-identical to the static extract
(validated err 6e-7) — and now posable. Tooling: `--report rdb-rig --report-arg <meshIndexId> [--skeleton <id>]`,
web `/api/model/{id}.glb?rig=1`, test `Faolan.Tests/RigSkinningTests.cs`.
