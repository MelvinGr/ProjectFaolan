using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Faolan.Core.Rdb.Assets
{
	// Phase 0 of the RDB -> 3D-model pipeline (Gitea #275): resolve a model's asset graph from its MESHINDEXFILE
	// manifest and extract what is decodable today - the FCTX textures (-> DDS) and, on request, the raw geometry blob
	// (RDB type 1010001) so it is in hand for the Phase 1 geometry-decode spike. The geometry format itself is still
	// undecoded; this stage gives us the texture set + reference tree + the bytes to reverse next.
	public static class RdbAssetExtractor
	{
		public const uint MeshIndexFileType = 1010030;
		public const uint RenderMeshType = 1010001;
		public const uint SkeletonType = 1010002;

		// A humanoid rig used as a last-resort fallback when auto-selection finds no skeleton covering a mesh's palette.
		public const uint HumanoidSkeletonId = 14853;

		// Load + parse a skeleton (type 1010002) by id, or null if missing/non-local.
		public static IReadOnlyList<SkeletonReader.Bone>? LoadSkeleton(RdbReader rdb, uint skeletonId)
		{
			if (skeletonId == 0 || !rdb.TryGet(SkeletonType, skeletonId, out var e) || !rdb.CanRead(e))
				return null;

			var bones = SkeletonReader.Read(rdb.ReadDecompressed(e));
			return bones.Count > 0 ? bones : null;
		}

		// Build a skinned glb for one skinned model (body or equipment piece): its geometry + the character skeleton as a
		// glTF skin, so the result is posable and (at rest) identical to the static extract. Diffuse comes from the mesh's
		// own materials / header texture refs. Null if the mesh-index/geometry is missing or the geometry isn't skinned.
		// skeletonId: explicit rig, or null to AUTO-SELECT the skeleton whose bone names cover the mesh's bone palette
		// (different bodies need different rigs - male has forearm-twist+thumb bones, female has breast bones - so a
		// single hardcoded skeleton sends the missing bones' vertices to the root and mangles them when posed).
		public static byte[]? BuildSkinnedModelGlb(RdbReader rdb, uint meshIndexId, uint? skeletonId = null)
		{
			var subs = ReadTexturedModel(rdb, meshIndexId);
			if (subs is not { Count: > 0 } || !subs.All(s => s.IsSkinned))
				return null;

			var skeleton = skeletonId is { } sid ? LoadSkeleton(rdb, sid) : SelectSkeletonForMesh(rdb, subs);
			if (skeleton == null)
				return null;

			return GlbWriter.Build(subs, null, skeleton);
		}

		// id -> the skeleton's bone names (built once from every local 1010002), to find rigs covering a mesh palette.
		private static Dictionary<uint, HashSet<string>>? _skeletonNames;
		// palette signature -> chosen skeleton id, so the per-mesh scan only happens once per distinct palette.
		private static readonly Dictionary<string, uint> _skeletonForPalette = [];

		private static Dictionary<uint, HashSet<string>> SkeletonNameIndex(RdbReader rdb)
		{
			if (_skeletonNames != null)
				return _skeletonNames;

			var idx = new Dictionary<uint, HashSet<string>>();
			foreach (var e in rdb.EntriesOfType(SkeletonType))
			{
				if (!e.IsLocal || e.Size == 0)
					continue;
				try
				{
					var bones = SkeletonReader.Read(rdb.ReadDecompressed(e));
					if (bones.Count > 0)
						idx[e.Id] = bones.Select(b => b.Name).ToHashSet();
				}
				catch { /* skip unparseable skeletons */ }
			}

			return _skeletonNames = idx;
		}

		// Pick the skeleton that best fits a skinned mesh: among rigs whose bone names cover the mesh's whole bone palette
		// (so no influence falls back to the root), the one whose bones sit closest to the vertices weighted to them.
		public static IReadOnlyList<SkeletonReader.Bone>? SelectSkeletonForMesh(RdbReader rdb, IReadOnlyList<RenderMeshReader.SubMesh> subs)
		{
			// The mesh's bone palette + the weighted centroid of the vertices bound to each bone (by name).
			var palette = new HashSet<string>();
			var sum = new Dictionary<string, System.Numerics.Vector3>();
			var wsum = new Dictionary<string, float>();
			foreach (var s in subs)
			{
				if (s.BonePalette is not { } pal || s.BlendBones is not { } bones || s.BlendWeights is not { } weights)
					continue;
				foreach (var n in pal)
					if (n != null)
						palette.Add(n);
				for (var i = 0; i < s.VertexCount; i++)
					for (var k = 0; k < 4; k++)
					{
						var w = weights[i * 4 + k];
						var li = bones[i * 4 + k];
						if (w <= 0 || li < 0 || li >= pal.Length)
							continue;
						var name = pal[li];
						if (name == null)
							continue;
						var p = new System.Numerics.Vector3(s.Positions[i * 3], s.Positions[i * 3 + 1], s.Positions[i * 3 + 2]);
						sum[name] = (sum.TryGetValue(name, out var v) ? v : default) + p * w;
						wsum[name] = (wsum.TryGetValue(name, out var ww) ? ww : 0) + w;
					}
			}

			if (palette.Count == 0)
				return null;

			var centroid = wsum.ToDictionary(kv => kv.Key, kv => sum[kv.Key] / kv.Value);
			var sig = string.Join('\n', palette.OrderBy(n => n, StringComparer.Ordinal));

			var index = SkeletonNameIndex(rdb);
			if (_skeletonForPalette.TryGetValue(sig, out var cached))
				return LoadSkeleton(rdb, cached);

			uint bestId = 0;
			var bestScore = float.MaxValue;
			foreach (var (id, names) in index)
			{
				if (!palette.IsSubsetOf(names) || LoadSkeleton(rdb, id) is not { } bones)
					continue;

				var world = new Dictionary<string, System.Numerics.Vector3>();
				foreach (var b in bones)
					world.TryAdd(b.Name, b.WorldPosition); // some skeletons repeat a bone name; first wins
				double d = 0;
				var n = 0;
				foreach (var (name, c) in centroid)
					if (world.TryGetValue(name, out var w))
					{
						d += (w - c).Length();
						n++;
					}

				var score = n > 0 ? (float)(d / n) : float.MaxValue;
				if (score < bestScore)
				{
					bestScore = score;
					bestId = id;
				}
			}

			if (bestId == 0)
				return LoadSkeleton(rdb, HumanoidSkeletonId); // no covering rig found - fall back to the humanoid base

			_skeletonForPalette[sig] = bestId;
			return LoadSkeleton(rdb, bestId);
		}

		// Texture container type ids worth converting/dumping (FCTX variants + the PNG/JPEG types from rdbreader's enum).
		private static readonly uint[] FctxTypes = [1010004, 1010006, 1010218, 1066606, 1066610];

		public static Result Extract(RdbReader rdb, uint meshIndexId, string? outDir, bool dumpMesh)
		{
			if (!rdb.TryGet(MeshIndexFileType, meshIndexId, out var indexEntry))
				return Result.NotFound(meshIndexId);

			var mesh = MeshIndexFile.Parse(meshIndexId, rdb.ReadDecompressed(indexEntry));

			if (outDir != null)
				Directory.CreateDirectory(outDir);

			var textures = new List<TextureResult>();
			foreach (var t in mesh.UniqueTextures)
			{
				var type = t.Type!.Value;
				var (status, size, outFile) = ExtractTexture(rdb, type, t.Id, outDir);
				textures.Add(new TextureResult(t.ToString(), status, size, outFile));
			}

			var renderMesh = DescribeResource(rdb, RenderMeshType, mesh.RenderMeshId);
			string? meshDump = null;
			string? glb = null;
			int glbVerts = 0, glbTris = 0;
			if (mesh.RenderMeshId is { } rmId && rdb.TryGet(RenderMeshType, rmId, out var rmEntry) && rdb.CanRead(rmEntry))
			{
				var blob = rdb.ReadDecompressed(rmEntry);

				if (dumpMesh && outDir != null)
				{
					meshDump = $"rendermesh_{rmId}.bin";
					File.WriteAllBytes(Path.Combine(outDir, meshDump), blob);
				}

				var subs = RenderMeshReader.Read(blob);
				if (subs.Count > 0)
				{
					glbVerts = subs.Sum(s => s.VertexCount);
					glbTris = subs.Sum(s => s.TriangleCount);
					if (outDir != null)
					{
						glb = $"model_{rmId}.glb";
						File.WriteAllBytes(Path.Combine(outDir, glb), GlbWriter.Build(subs, DecodeDiffusePng(rdb, mesh, blob).Png));
					}
				}
			}

			var manifest = new Manifest(
				meshIndexId,
				mesh.SourceFile,
				mesh.Bounds?.ToString(),
				renderMesh,
				DescribeResource(rdb, 0, mesh.CollisionMeshId), // type unknown for collision; reported by id only
				meshDump,
				glb,
				glb != null || glbVerts > 0 ? new GeometryResult(glbVerts, glbTris) : null,
				mesh.Materials.Select(m => new MaterialResult(
					m.Name,
					m.Textures.Select(x => x.ToString()).ToList())).ToList(),
				textures);

			if (outDir != null)
				File.WriteAllText(Path.Combine(outDir, $"{meshIndexId}.manifest.json"),
					JsonSerializer.Serialize(manifest, JsonOpts));

			return new Result(true, mesh, manifest);
		}

		// The model's base-colour texture for the glTF material: the first local, BC1/BC3-decodable texture. We try the
		// manifest's material list first (static props), then fall back to the RenderMesh header's own texture ref table
		// (skinned character bodies have empty <MaterialSets/> and instead carry [id] refs in the geometry blob header).
		// Normal/specular maps are MIXD (BC4) and return false from TryDecodeTopMip, so they're skipped and the diffuse
		// (DXT1, listed first via g_DiffuseTex=0) is what gets picked.
		private static (byte[]? Png, bool AlphaMask) DecodeDiffusePng(RdbReader rdb, MeshIndexFile mesh, ReadOnlySpan<byte> blob = default, Tint? tint = null)
		{
			foreach (var t in mesh.UniqueTextures)
				if (TryDecodeTexture(rdb, t.Type!.Value, t.Id, out var png, out var alpha, tint))
					return (png, alpha);

			foreach (var (type, id) in RenderMeshReader.ReadHeaderTextureRefs(blob))
				if (TryDecodeTexture(rdb, type, id, out var png, out var alpha, tint))
					return (png, alpha);

			return (null, false);
		}

		// A per-character colour multiply from a TexScript "tinting" op (e.g. hair colour).
		public readonly record struct Tint(byte R, byte G, byte B);

		// A body tattoo/marking: an FCTX texture (1010004) projected onto the body atlas at (U,V) in 0..1, sized Scale
		// percent of its native size, rotated Rotation degrees, alpha-blended by Blend. From a mesh -1/-2 TexScript.
		public readonly record struct BodyDecal(uint TextureId, float U, float V, float Scale, float Rotation, float Blend);

		private static bool TryDecodeTexture(RdbReader rdb, uint type, uint id, out byte[]? png, out bool alphaMask,
			Tint? tint = null, bool includeSingleChannel = false)
		{
			png = null;
			alphaMask = false;
			if (!rdb.TryGet(type, id, out var e) || !rdb.CanRead(e))
				return false;

			var payload = rdb.ReadDecompressed(e);
			if (!FctxConverter.TryDecodeTopMip(payload, out var rgba, out var w, out var h, includeSingleChannel))
				return false;

			alphaMask = HasTransparency(rgba);
			png = SkiaImage.EncodePng(rgba, w, h, tint);
			return true;
		}

		// A diffuse is treated as alpha-tested (hair, eyelashes, ...) when a meaningful fraction of its texels are
		// transparent. The 1% floor ignores stray anti-aliased edge pixels on otherwise-opaque skin maps.
		private static bool HasTransparency(byte[] rgba)
		{
			var total = rgba.Length / 4;
			if (total == 0)
				return false;

			var transparent = 0;
			for (var i = 3; i < rgba.Length; i += 4)
				if (rgba[i] < 128)
					transparent++;

			return transparent > total / 100;
		}

		// Decode a model's geometry AND attach its own diffuse to every sub-mesh, so an assembled character textures each
		// part (body skin, head) independently. Returns null when the mesh-index or its local geometry is missing.
		private static IReadOnlyList<RenderMeshReader.SubMesh>? ReadTexturedModel(RdbReader rdb, uint meshIndexId,
			Tint? tint = null, IReadOnlyList<BodyDecal>? decals = null, BodyMorphDeformer.Morphs? morphs = null)
		{
			if (!rdb.TryGet(MeshIndexFileType, meshIndexId, out var idxEntry))
				return null;

			var mesh = MeshIndexFile.Parse(meshIndexId, rdb.ReadDecompressed(idxEntry));
			if (mesh.RenderMeshId is not { } rmId || !rdb.TryGet(RenderMeshType, rmId, out var rmEntry) || !rdb.CanRead(rmEntry))
				return null;

			var blob = rdb.ReadDecompressed(rmEntry);
			var subs = RenderMeshReader.Read(blob);
			if (subs.Count == 0)
				return null;

			// Apply body-proportion morphs (geometric region deform) to the body geometry in place before texturing.
			if (morphs is { IsNeutral: false } m)
				foreach (var sub in subs)
					BodyMorphDeformer.Apply(sub.Positions, m);

			// With body decals (mesh -1/-2 tattoos), paste them into the body atlas (texture space); else plain diffuse.
			var (diffuse, alpha) = decals is { Count: > 0 }
				? (BuildBodyDiffuse(rdb, mesh, blob, tint, decals), false)
				: DecodeDiffusePng(rdb, mesh, blob, tint);
			return diffuse == null ? subs : subs.Select(s => s with { BaseColorPng = diffuse, AlphaMask = alpha }).ToList();
		}

		// Decode the body's base diffuse to RGBA, paste the character's tattoo decals onto it (texscript projecttexture is
		// a 2D texture-space placement, not a 3D projection - Ghidra ProjectTextureOperation_c), then encode (+ skin tint).
		private static byte[]? BuildBodyDiffuse(RdbReader rdb, MeshIndexFile mesh, ReadOnlySpan<byte> blob,
			Tint? tint, IReadOnlyList<BodyDecal> decals)
		{
			byte[]? rgba = null;
			int w = 0, h = 0;
			foreach (var t in mesh.UniqueTextures)
				if (TryDecodeRgba(rdb, t.Type!.Value, t.Id, out rgba, out w, out h))
					break;
			if (rgba is null)
				foreach (var (type, id) in RenderMeshReader.ReadHeaderTextureRefs(blob))
					if (TryDecodeRgba(rdb, type, id, out rgba, out w, out h))
						break;
			if (rgba is null)
				return null;

			// Ghidra (ProjectTextureOperation_c / FUN_00d88d50, projecttextureoperation.cpp): the decal is pasted into the
			// base diffuse's TEXTURE space as a rect centred at (ucoord, vcoord), half-size = decalDim * scale*0.01 * 0.5 /
			// atlasDim (scale is a percent; 0.01 = DAT_01f2800c, 0.5 = _DAT_02019f54), rotation in degrees. NOT a 3D
			// projection. So ScaleFraction (full drawn width / atlas width) = decalW * scale * 0.01 / atlasW.
			// Our body atlas U runs opposite to the client's authoring frame (the medallion otherwise lands on the wrong
			// breast vs in-game), so mirror the centre U: 1-ucoord. Centred decals (ucoord 0.5) are unaffected.
			var placed = new List<SkiaImage.Decal>();
			foreach (var d in decals)
				if (TryDecodeRgba(rdb, 1010004, d.TextureId, out var dec, out var dw, out var dh) && dw > 0)
					placed.Add(new SkiaImage.Decal(dec, dw, dh, 1f - d.U, d.V, dw * d.Scale * 0.01f / w, d.Rotation, d.Blend));

			return placed.Count == 0
				? SkiaImage.EncodePng(rgba, w, h, tint)
				: SkiaImage.CompositeDecals(rgba, w, h, placed, tint);
		}

		// Assemble several models (e.g. a character's body + head + equipment, all authored in the same bind-pose space)
		// into one glb by concatenating their sub-meshes. Per-mesh diffuse is embedded when the mesh carries a material;
		// skinned character bodies have none (item-system bound) so render untextured for now. No skinning/pose applied -
		// a static bind-pose assembly, which is all a "view my character" page needs.
		public static AssembleResult Assemble(RdbReader rdb, IReadOnlyList<uint> meshIndexIds, string outPath)
		{
			var all = new List<RenderMeshReader.SubMesh>();
			byte[]? firstDiffuse = null;
			var used = new List<uint>();
			foreach (var meshIndexId in meshIndexIds)
			{
				if (!rdb.TryGet(MeshIndexFileType, meshIndexId, out var idxEntry))
					continue;

				var mesh = MeshIndexFile.Parse(meshIndexId, rdb.ReadDecompressed(idxEntry));
				if (mesh.RenderMeshId is not { } rmId || !rdb.TryGet(RenderMeshType, rmId, out var rmEntry) || !rdb.CanRead(rmEntry))
					continue;

				var subs = RenderMeshReader.Read(rdb.ReadDecompressed(rmEntry));
				if (subs.Count == 0)
					continue;

				all.AddRange(subs);
				used.Add(meshIndexId);
				firstDiffuse ??= DecodeDiffusePng(rdb, mesh).Png;
			}

			if (all.Count > 0)
				File.WriteAllBytes(outPath, GlbWriter.Build(all, firstDiffuse));

			return new AssembleResult(used, all.Sum(s => s.VertexCount), all.Sum(s => s.TriangleCount));
		}

		public readonly record struct AssembleResult(IReadOnlyList<uint> Used, int Vertices, int Triangles);

		// Decode a RenderMesh (type 1010001) DIRECTLY by its own id - no MESHINDEXFILE wrapper. Character appearance
		// resources reference geometry this way: a *hairmesh_NNN ref is the rendermesh id NNN itself. Diffuse comes from
		// the blob's header texture refs (these have no manifest/materials). Null if missing/non-local/empty.
		// tint: a per-character colour multiply (TexScript tinting). overrideTextureId: use this FCTX as the diffuse
		// instead of the mesh's own header texture (TexScript projecttexture, e.g. the chosen eye iris).
		public static IReadOnlyList<RenderMeshReader.SubMesh>? ReadRenderMeshDirect(RdbReader rdb, uint renderMeshId,
			Tint? tint = null, uint? overrideTextureId = null)
		{
			if (!rdb.TryGet(RenderMeshType, renderMeshId, out var rmEntry) || !rdb.CanRead(rmEntry))
				return null;

			var blob = rdb.ReadDecompressed(rmEntry);
			var subs = RenderMeshReader.Read(blob);
			if (subs.Count == 0)
				return null;

			byte[]? diffuse = null;
			var alpha = false;
			if (overrideTextureId is { } ot)
				diffuse = CompositeOverlay(rdb, blob, ot, tint);
			else
				foreach (var (type, id) in RenderMeshReader.ReadHeaderTextureRefs(blob))
					if (TryDecodeTexture(rdb, type, id, out diffuse, out alpha, tint))
						break;

			return diffuse == null ? subs : subs.Select(s => s with { BaseColorPng = diffuse, AlphaMask = alpha }).ToList();
		}

		// TexScript projecttexture for the eye: overlay the iris texture ON TOP of the mesh's own base (the eyeball
		// sclera), alpha-blending where the iris is opaque, so the white of the eye is kept (replacing it outright
		// leaves a floating iris in an empty socket). Returns an opaque PNG. Falls back to the base or the iris alone.
		private static byte[]? CompositeOverlay(RdbReader rdb, ReadOnlySpan<byte> blob, uint overlayTextureId, Tint? tint)
		{
			byte[]? baseRgba = null;
			int bw = 0, bh = 0;
			foreach (var (type, id) in RenderMeshReader.ReadHeaderTextureRefs(blob))
				if (TryDecodeRgba(rdb, type, id, out baseRgba, out bw, out bh))
					break;

			if (!TryDecodeRgba(rdb, 1010004, overlayTextureId, out var over, out var ow, out var oh))
				return baseRgba is null ? null : SkiaImage.EncodePng(baseRgba, bw, bh, tint);

			if (baseRgba is null)
				return SkiaImage.EncodePng(over, ow, oh, tint); // no base: just use the iris

			return SkiaImage.CompositeOver(baseRgba, bw, bh, over, ow, oh, tint);
		}

		private static bool TryDecodeRgba(RdbReader rdb, uint type, uint id, out byte[] rgba, out int w, out int h)
		{
			rgba = [];
			w = h = 0;
			if (!rdb.TryGet(type, id, out var e) || !rdb.CanRead(e))
				return false;

			return FctxConverter.TryDecodeTopMip(rdb.ReadDecompressed(e), out rgba, out w, out h);
		}

		// Decode one model's geometry to its sub-meshes (null if the mesh-index or its local geometry is missing).
		public static IReadOnlyList<RenderMeshReader.SubMesh>? ReadModel(RdbReader rdb, uint meshIndexId)
		{
			if (!rdb.TryGet(MeshIndexFileType, meshIndexId, out var idxEntry))
				return null;

			var mesh = MeshIndexFile.Parse(meshIndexId, rdb.ReadDecompressed(idxEntry));
			if (mesh.RenderMeshId is not { } rmId || !rdb.TryGet(RenderMeshType, rmId, out var rmEntry) || !rdb.CanRead(rmEntry))
				return null;

			var subs = RenderMeshReader.Read(rdb.ReadDecompressed(rmEntry));
			return subs.Count > 0 ? subs : null;
		}

		// Build a character glb in memory: a body mesh + (optionally) a head seated at the body's neck. Heads are
		// authored in head-local space, so the head is translated so its base aligns with the body's top ring centroid -
		// a geometric stand-in for the skeleton's head-bone transform ([[SkeletonReader]]), good enough for a static view.
		public static byte[]? BuildCharacterGlb(RdbReader rdb, uint bodyMeshIndexId, uint? headMeshIndexId,
			uint? hairRenderMeshId = null)
		{
			var body = ReadTexturedModel(rdb, bodyMeshIndexId);
			if (body == null)
				return null;

			var all = new List<RenderMeshReader.SubMesh>(body);

			if (headMeshIndexId is { } hid && ReadTexturedModel(rdb, hid) is { } head)
			{
				var neck = NeckTop(body);
				var anchor = BottomCenter(head);
				var (dx, dy, dz) = (neck.x - anchor.x, neck.y - anchor.y, neck.z - anchor.z);
				foreach (var s in head)
					all.Add(Translate(s, dx, dy, dz));
			}

			// Hair is authored in full-body/world space (head at ~y1.7), not head-local, so it's added untranslated -
			// it already sits at the head's world position (verified: hair y-range ~1.58-1.95 overlaps the seated head).
			if (hairRenderMeshId is { } hr && ReadRenderMeshDirect(rdb, hr) is { } hair)
				all.AddRange(hair);

			return GlbWriter.Build(all, null);
		}

		// Resolve a CHARACTERCREATOR head monsterdataid to its component RenderMesh ids (head + eyes + lashes) via the
		// MonsterData (1010201) record. Empty if missing/non-local. These meshes are head-local (seat at the neck).
		public static IReadOnlyList<uint> ResolveHeadMeshes(RdbReader rdb, uint monsterDataId)
		{
			if (!rdb.TryGet(MonsterDataFile.Type, monsterDataId, out var e) || !rdb.CanRead(e))
				return [];

			return MonsterDataFile.Parse(monsterDataId, rdb.ReadDecompressed(e)).MeshIds;
		}

		// Diagnostic: resolve a MESHINDEXFILE to its RenderMesh and report where each lives (local rdbdata vs CDN) and
		// whether it's currently readable (CDN entries are readable only if a mirror source is configured).
		public static object DescribeMesh(RdbReader rdb, uint meshIndexId)
		{
			if (meshIndexId == 0 || !rdb.TryGet(MeshIndexFileType, meshIndexId, out var idx))
				return new { meshIndexId, found = false };

			uint? rmId = null;
			string rmSource = "n/a";
			var rmReadable = false;
			object[] textureRefs = [];
			try
			{
				var mesh = MeshIndexFile.Parse(meshIndexId, rdb.ReadDecompressed(idx));
				rmId = mesh.RenderMeshId;
				if (rmId is { } id && rdb.TryGet(RenderMeshType, id, out var rm))
				{
					rmSource = rm.IsLocal ? "local" : "cdn";
					rmReadable = rdb.CanRead(rm);
					if (rmReadable)
						textureRefs = [.. RenderMeshReader.ReadHeaderTextureRefs(rdb.ReadDecompressed(rm))
							.Select(r => (object)new { type = r.Type, id = r.Id })];
				}
			}
			catch { /* parse failed - report what we have */ }

			return new
			{
				meshIndexId,
				found = true,
				meshIndexSource = idx.IsLocal ? "local" : "cdn",
				meshIndexReadable = rdb.CanRead(idx),
				renderMeshId = rmId,
				renderMeshSource = rmSource,
				renderMeshReadable = rmReadable,
				textureRefs
			};
		}

		// Diagnostic: is the body's front UV mirrored left/right? Splits the body rendermesh vertices by sign(X) and
		// reports each half's U range. Overlapping ranges => left and right share atlas texels (mirrored) => a tattoo
		// can't be baked onto one side only. Disjoint ranges => unique unwrap => atlas placement can target one side.
		public static object DescribeBodyUv(RdbReader rdb, uint bodyMeshIndexId)
		{
			if (!rdb.TryGet(MeshIndexFileType, bodyMeshIndexId, out var idx))
				return new { found = false };
			var mesh = MeshIndexFile.Parse(bodyMeshIndexId, rdb.ReadDecompressed(idx));
			if (mesh.RenderMeshId is not { } rmId || !rdb.TryGet(RenderMeshType, rmId, out var rm) || !rdb.CanRead(rm))
				return new { found = false, renderMeshId = mesh.RenderMeshId };

			var subs = RenderMeshReader.Read(rdb.ReadDecompressed(rm));
			float lMinU = 1, lMaxU = 0, rMinU = 1, rMaxU = 0;
			int lN = 0, rN = 0;
			foreach (var s in subs)
			{
				if (s.Uvs is null)
					continue;
				for (var i = 0; i < s.VertexCount; i++)
				{
					float x = s.Positions[i * 3], u = s.Uvs[i * 2];
					if (x < -0.02f) { lMinU = Math.Min(lMinU, u); lMaxU = Math.Max(lMaxU, u); lN++; }
					else if (x > 0.02f) { rMinU = Math.Min(rMinU, u); rMaxU = Math.Max(rMaxU, u); rN++; }
				}
			}

			var overlap = Math.Min(lMaxU, rMaxU) - Math.Max(lMinU, rMinU);
			return new
			{
				found = true, submeshes = subs.Count,
				leftXverts = lN, leftU = new[] { lMinU, lMaxU },
				rightXverts = rN, rightU = new[] { rMinU, rMaxU },
				uOverlap = overlap, mirrored = overlap > 0.25f
			};
		}

		// Diagnostic: decode an FCTX/texture resource (type, id) straight to PNG (no tint/decal), or null if absent.
		public static byte[]? DecodeTexturePng(RdbReader rdb, uint type, uint id) =>
			TryDecodeTexture(rdb, type, id, out var png, out _, null, includeSingleChannel: true) ? png : null;

		// Diagnostic: the decompressed payload of a resource as Latin1 text (XML resources read straight; binary is mojibake).
		public static string? RawText(RdbReader rdb, uint type, uint id) =>
			rdb.TryGet(type, id, out var e) && rdb.CanRead(e)
				? System.Text.Encoding.Latin1.GetString(rdb.ReadDecompressed(e))
				: null;

		// Diagnostic: the raw decompressed bytes of a resource (for binary blob analysis, e.g. the rendermesh DPPM
		// morph-target chunk), or null if absent/unreadable.
		public static byte[]? RawBytes(RdbReader rdb, uint type, uint id) =>
			rdb.TryGet(type, id, out var e) && rdb.CanRead(e)
				? rdb.ReadDecompressed(e)
				: null;

		// Load + parse a TexScript (type 1010035) by id - the per-character colour op (skin/hair tint, eye iris).
		public static TexScriptFile? LoadTexScript(RdbReader rdb, uint id) =>
			rdb.TryGet(TexScriptFile.Type, id, out var e) && rdb.CanRead(e)
				? TexScriptFile.Parse(id, rdb.ReadDecompressed(e))
				: null;

		// Assemble a character from decoded parts: body (meshindex) + head-local RenderMeshes (head/eyes/lashes from a
		// MonsterData, seated together at the neck) + an optional world-space hair RenderMesh (added untranslated).
		// skinTint/hairTint: per-character colour multiplies from their TexScripts. eyeTextureId: the projected iris
		// texture (TexScript projecttexture) used as the eyeball mesh's diffuse. MonsterData lists [head, lashes, eyeball]
		// but the order isn't guaranteed, so parts are identified by content: index 0 = head skin (largest), the eyeball
		// is the OPAQUE small part (the lashes are an alpha cutout), and the iris goes there.
		public static byte[]? BuildCharacterFromParts(RdbReader rdb, uint bodyMeshIndexId,
			IReadOnlyList<uint> headLocalRenderMeshIds, uint? hairWorldRenderMeshId,
			Tint? skinTint = null, Tint? hairTint = null, uint? eyeTextureId = null,
			IReadOnlyList<BodyDecal>? bodyDecals = null, BodyMorphDeformer.Morphs? morphs = null,
			IReadOnlyList<uint>? equipmentMeshIndexIds = null, bool rig = false)
		{
			var body = ReadTexturedModel(rdb, bodyMeshIndexId, skinTint, bodyDecals, morphs);
			if (body == null)
				return null;

			var all = new List<RenderMeshReader.SubMesh>(body);

			// Worn equipment: skinned armor meshes are authored in the SAME body-world bind-pose space as the body, so
			// they overlay directly with no transform (the visible half of "wearing gear" - the item->mesh look mapping
			// that picks WHICH meshindex is the separate, still-open RE in #275). Each piece carries its own diffuse via
			// the mesh-index materials / header refs; pieces with none render untextured.
			if (equipmentMeshIndexIds is { Count: > 0 })
				foreach (var equipId in equipmentMeshIndexIds)
					if (ReadTexturedModel(rdb, equipId) is { } worn)
						all.AddRange(worn);

			var headParts = new List<RenderMeshReader.SubMesh>();
			for (var i = 0; i < headLocalRenderMeshIds.Count; i++)
			{
				var rm = headLocalRenderMeshIds[i];
				if (i == 0)
				{
					// The head skin itself (largest mesh) - apply the skin tint.
					if (ReadRenderMeshDirect(rdb, rm, skinTint) is { } headSkin)
						headParts.AddRange(headSkin);
					continue;
				}

				// Eyeball vs eyelashes: load plain, then if it's the opaque part and we have an iris, reload with it.
				var part = ReadRenderMeshDirect(rdb, rm);
				if (part is { Count: > 0 } && eyeTextureId is { } eye && !part[0].AlphaMask
				    && ReadRenderMeshDirect(rdb, rm, null, eye) is { } eyeball)
					part = eyeball;
				if (part is not null)
					headParts.AddRange(part);
			}

			// Hair is authored in world space (head at ~y1.7); it's the ground truth for where the head sits.
			var hair = hairWorldRenderMeshId is { } hr ? ReadRenderMeshDirect(rdb, hr, hairTint) : null;

			// The head/eyes/hair are rigid world-space parts (collected here, then either added as static geometry or -
			// for a rig - bound to the Head bone so they're part of the same skinned model).
			var decor = new List<RenderMeshReader.SubMesh>();
			if (headParts.Count > 0)
			{
				// Seat the head-local meshes (head/eyes/lashes) at the body's neck for Y, then - when we have hair -
				// snap their X/Z to the hair's frame so the head sits inside the hairstyle (the geometric neck-seat alone
				// lands the head a few cm off in Z relative to the world-space hair).
				var neck = NeckTop(body);
				var anchor = BottomCenter(headParts);
				var seated = headParts.Select(s => Translate(s, neck.x - anchor.x, neck.y - anchor.y, neck.z - anchor.z)).ToList();

				if (hair is { Count: > 0 })
				{
					// Snap the head's X/Z bbox centre to the hair's bbox centre so the skull sits inside the hair cap.
					// (Measured against captured bodies: this co-locates head and hair for both a compact cap and a
					// ponytail style — the trailing braid doesn't skew the hair bbox centre enough to pull the head out.)
					var hc = Center(hair);
					var sc = Center(seated);
					seated = seated.Select(s => Translate(s, hc.x - sc.x, 0, hc.z - sc.z)).ToList();
				}

				decor.AddRange(seated);
			}

			if (hair is { Count: > 0 })
				decor.AddRange(hair); // AlphaMask already set from the hair texture's transparency

			// Rig: the body (+ worn gear) are skinned to their own bones; bind the rigid head/hair to the Head bone so
			// the whole character is one posable skinned model (head follows the head bone; the elbow slider leaves it put).
			if (rig && SelectSkeletonForMesh(rdb, all) is { } skeleton)
			{
				all.AddRange(decor.Select(s => BindToBone(s, "rig_bone_Head")));
				return GlbWriter.Build(all, null, skeleton);
			}

			all.AddRange(decor);
			return GlbWriter.Build(all, null);
		}

		// Make a static sub-mesh follow a single skeleton bone: bind every vertex 100% to that bone (palette slot 0). At
		// rest the inverse-bind cancels the bone's bind transform so the geometry stays where it is; when the bone moves
		// the whole part moves rigidly with it. Used to attach the rigid head/hair to the Head bone of a character rig.
		private static RenderMeshReader.SubMesh BindToBone(RenderMeshReader.SubMesh s, string boneName)
		{
			var bones = new int[s.VertexCount * 4]; // all slots = palette index 0
			var weights = new float[s.VertexCount * 4];
			for (var i = 0; i < s.VertexCount; i++)
				weights[i * 4] = 1f;
			return s with { BlendBones = bones, BlendWeights = weights, BonePalette = [boneName] };
		}

		// Axis-aligned bounding-box centre of a set of sub-meshes.
		private static (float x, float y, float z) Center(IReadOnlyList<RenderMeshReader.SubMesh> model)
		{
			float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
			float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
			foreach (var s in model)
				for (var i = 0; i + 2 < s.Positions.Length; i += 3)
				{
					minX = Math.Min(minX, s.Positions[i]); maxX = Math.Max(maxX, s.Positions[i]);
					minY = Math.Min(minY, s.Positions[i + 1]); maxY = Math.Max(maxY, s.Positions[i + 1]);
					minZ = Math.Min(minZ, s.Positions[i + 2]); maxZ = Math.Max(maxZ, s.Positions[i + 2]);
				}

			return ((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
		}

		// Centroid of the body's top ring (near max-Y) = where the head's base should sit (the neck).
		private static (float x, float y, float z) NeckTop(IReadOnlyList<RenderMeshReader.SubMesh> model)
		{
			var maxY = float.MinValue;
			foreach (var s in model)
				for (var i = 1; i < s.Positions.Length; i += 3)
					maxY = Math.Max(maxY, s.Positions[i]);

			var band = maxY * 0.03f + 0.03f;
			double sx = 0, sy = 0, sz = 0;
			var n = 0;
			foreach (var s in model)
				for (var i = 0; i + 2 < s.Positions.Length; i += 3)
					if (s.Positions[i + 1] >= maxY - band)
					{
						sx += s.Positions[i];
						sy += s.Positions[i + 1];
						sz += s.Positions[i + 2];
						n++;
					}

			return n == 0 ? (0, maxY, 0) : ((float)(sx / n), (float)(sy / n), (float)(sz / n));
		}

		// Centroid of a model's lowest band (its base) - for a head, the neck stub.
		private static (float x, float y, float z) BottomCenter(IReadOnlyList<RenderMeshReader.SubMesh> model)
		{
			var minY = float.MaxValue;
			var maxY = float.MinValue;
			foreach (var s in model)
				for (var i = 1; i < s.Positions.Length; i += 3)
				{
					minY = Math.Min(minY, s.Positions[i]);
					maxY = Math.Max(maxY, s.Positions[i]);
				}

			var band = (maxY - minY) * 0.1f + 1e-4f;
			double sx = 0, sy = 0, sz = 0;
			var n = 0;
			foreach (var s in model)
				for (var i = 0; i + 2 < s.Positions.Length; i += 3)
					if (s.Positions[i + 1] <= minY + band)
					{
						sx += s.Positions[i];
						sy += s.Positions[i + 1];
						sz += s.Positions[i + 2];
						n++;
					}

			return n == 0 ? (0, minY, 0) : ((float)(sx / n), (float)(sy / n), (float)(sz / n));
		}

		private static RenderMeshReader.SubMesh Translate(RenderMeshReader.SubMesh s, float dx, float dy, float dz)
		{
			var p = (float[])s.Positions.Clone();
			for (var i = 0; i + 2 < p.Length; i += 3)
			{
				p[i] += dx;
				p[i + 1] += dy;
				p[i + 2] += dz;
			}

			return s with { Positions = p };
		}

		private static (string Status, long Size, string? OutFile) ExtractTexture(RdbReader rdb, uint type, uint id, string? outDir)
		{
			if (!rdb.TryGet(type, id, out var e))
				return ("missing", 0, null);
			if (!e.IsLocal)
				return ("cdn", e.Size, null); // on-demand CDN resource; fetch-by-hash is a later step

			var payload = rdb.ReadDecompressed(e);

			// FCTX container -> DDS.
			if (FctxConverter.IsFctx(payload) || FctxTypes.Contains(type))
			{
				var conv = FctxConverter.Convert(payload);
				if (!conv.Ok)
					return ($"fctx-error: {conv.Warning}", payload.Length, null);

				var name = $"tex_{type}_{id}.dds";
				if (outDir != null)
					File.WriteAllBytes(Path.Combine(outDir, name), conv.Dds);
				return ($"dds {conv.Header.FourCc} {conv.Header.Width}x{conv.Header.Height} mips={conv.Header.MipCount}", conv.Dds.Length, outDir != null ? name : null);
			}

			// Embedded PNG (the RDB record wraps a standard PNG after a small header): slice from the signature.
			var png = FindPng(payload);
			if (png != null)
			{
				var name = $"tex_{type}_{id}.png";
				if (outDir != null)
					File.WriteAllBytes(Path.Combine(outDir, name), png);
				return ("png", png.Length, outDir != null ? name : null);
			}

			// Unknown container - dump raw so nothing is lost.
			var raw = $"tex_{type}_{id}.bin";
			if (outDir != null)
				File.WriteAllBytes(Path.Combine(outDir, raw), payload);
			return ("raw", payload.Length, outDir != null ? raw : null);
		}

		private static ResourceResult? DescribeResource(RdbReader rdb, uint type, uint? id)
		{
			if (id is not { } rid)
				return null;
			if (type != 0 && rdb.TryGet(type, rid, out var e))
				return new ResourceResult(rid, type, e.IsLocal ? "local" : "cdn", e.Size);

			// Collision (type unknown): probe the two known collision payload types so the report still says where it is.
			foreach (var probe in new uint[] { 1010034, 1000028 })
				if (rdb.TryGet(probe, rid, out var ce))
					return new ResourceResult(rid, probe, ce.IsLocal ? "local" : "cdn", ce.Size);

			return new ResourceResult(rid, type, "missing", 0);
		}

		private static readonly byte[] PngSig = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

		private static byte[]? FindPng(byte[] payload)
		{
			for (var i = 0; i + PngSig.Length <= payload.Length && i < 64; i++)
			{
				if (payload.AsSpan(i, PngSig.Length).SequenceEqual(PngSig))
					return payload[i..];
			}

			return null;
		}

		private static readonly JsonSerializerOptions JsonOpts = new()
		{
			WriteIndented = true,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
		};

		public readonly record struct Result(bool Ok, MeshIndexFile? Mesh, Manifest? Manifest)
		{
			public static Result NotFound(uint id) => new(false, null, null);
		}

		public sealed record Manifest(
			uint MeshIndexId,
			string? Source,
			string? Bounds,
			ResourceResult? RenderMesh,
			ResourceResult? CollisionMesh,
			string? RenderMeshDump,
			string? Glb,
			GeometryResult? Geometry,
			IReadOnlyList<MaterialResult> Materials,
			IReadOnlyList<TextureResult> Textures);

		public sealed record GeometryResult(int Vertices, int Triangles);

		public sealed record ResourceResult(uint Id, uint Type, string Availability, long Size);

		public sealed record MaterialResult(string Name, IReadOnlyList<string> Textures);

		public sealed record TextureResult(string Ref, string Status, long Size, string? Output);
	}
}
