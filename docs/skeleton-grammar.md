# Skeleton + attractors (RDB types 1010002 / 1010200) — #275 Phase 3

The bind-pose skeleton a skinned character mesh / animation system references. Decoded + validated against the
71-bone player skeleton (id 14853): `Captures/scripts` proof + C# `SkeletonReader` (byte-exact, world positions match).

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
