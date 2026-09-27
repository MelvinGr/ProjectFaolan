# Body atlas gutter dilation — why the viewer needs it, and where "gutter" comes from

A character's skin comes from one atlas texture whose UV islands (torso, arms, legs, hands, head) sit on a flat
tan background. A game engine pads the border of every island at texture-build time, so a triangle sampling
right at an island's edge still reads skin; the raw RDB atlas has no such padding, so the viewer's edge
triangles sample the flat background and the body shows lighter seam blotches along the midline, spine, waist
and island boundaries.

`BodyAtlasDilator` adds that padding: fill every gutter texel with the nearest island colour (grassfire), then
box-blur the gutter at growing radii, re-pinning the island texels after each pass so nothing real is smeared.

## "Gutter" is the mesh's UV coverage, not a colour test

A colour test can decide gutter from the pixels themselves: near the sampled background colour **and** locally
flat, flood-filled inward from the image border. Both guards are needed because the tan background is close to tan
skin — and both together are still not enough.

`BodyAtlasUvCoverage.TryRasterize` answers the same question from the mesh. A texel a UV triangle covers is
drawn on the character, so it is skin by definition; a texel no triangle covers is never drawn, so filling it
cannot lose anything. That is a measurement, not a guess, and `BodyAtlasDilator.Dilate` takes the mask as
authoritative when the caller has geometry — which the body path always does. The colour heuristic remains for
a caller with no mesh.

Rasterization is deliberately **conservative**: a texel counts as covered if a triangle touches it at all
(centre-inside fill plus the three edges walked), because the two errors are not symmetric. A missed island
texel gets blurred away and shows; an extra covered texel merely goes unfilled in a region nothing samples.

A triangle's UV tile is a property of the **triangle**, not of each corner. Wrapping the three corners
independently tears a triangle that straddles a tile boundary into one spanning the whole atlas — visible as
long straight scars across the mask. All three corners shift by the same whole-tile offset instead.

## The measurement

`--report body-atlas-coverage --report-arg <id>[,<id>...] [--out <dir>]` prints the heuristic's mask beside the
UV coverage and, with `--out`, writes all three as images. The id is any of the three a body is reached by: a
CHARACTERCREATOR `<body monsterdataid>` (1091334 is the Aquilonian female), a MESHINDEXFILE manifest, or a bare
RenderMesh.

On the Aquilonian female body (1024×1024 atlas):

| | |
|---|---:|
| heuristic island | 35.1% |
| UV coverage | 55.8% |
| **heuristic gutter the mesh actually draws** | **24.3%** |
| heuristic island the mesh never draws | 3.6% |

So the colour test flattens a quarter of the atlas that the character is textured with. The rendered artifact
is mild — filling smooth skin with a blur of its own neighbours mostly reproduces it — so it reads as a faint
residual blotch rather than looking obviously broken. The mask images make it plain: the UV coverage is a clean body unwrap, while the heuristic's island mask is moth-eaten across the
whole torso and upper arms.

The last 3.6% is not a defect: an atlas can carry islands this particular mesh does not use, and the diffuse is
built per mesh, so nothing else samples them.

## Orientation is measured, not assumed

A V axis pointing the wrong way would invert the entire fix while still producing a plausible-looking mask, so
the report rasterizes both and prints them side by side. V **as-is** is correct, by a margin nothing could
misread:

| | V as-is | V flipped |
|---|---:|---:|
| coverage landing on the heuristic's islands | 56.5% | 43.9% |
| heuristic island the mesh never draws | 3.6% | 10.6% |

Open work tracked in Gitea #294.
