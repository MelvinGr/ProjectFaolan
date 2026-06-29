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
			if (mesh.RenderMeshId is { } rmId && rdb.TryGet(RenderMeshType, rmId, out var rmEntry) && rmEntry.IsLocal)
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
						File.WriteAllBytes(Path.Combine(outDir, glb), GlbWriter.Build(subs, DecodeDiffusePng(rdb, mesh, blob)));
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
		private static byte[]? DecodeDiffusePng(RdbReader rdb, MeshIndexFile mesh, ReadOnlySpan<byte> blob = default)
		{
			foreach (var t in mesh.UniqueTextures)
				if (TryDecodeTexture(rdb, t.Type!.Value, t.Id, out var png))
					return png;

			foreach (var (type, id) in RenderMeshReader.ReadHeaderTextureRefs(blob))
				if (TryDecodeTexture(rdb, type, id, out var png))
					return png;

			return null;
		}

		private static bool TryDecodeTexture(RdbReader rdb, uint type, uint id, out byte[]? png)
		{
			png = null;
			if (!rdb.TryGet(type, id, out var e) || !e.IsLocal)
				return false;

			var payload = rdb.ReadDecompressed(e);
			if (!FctxConverter.TryDecodeTopMip(payload, out var rgba, out var w, out var h))
				return false;

			png = PngWriter.Encode(rgba, w, h);
			return true;
		}

		// Decode a model's geometry AND attach its own diffuse to every sub-mesh, so an assembled character textures each
		// part (body skin, head) independently. Returns null when the mesh-index or its local geometry is missing.
		private static IReadOnlyList<RenderMeshReader.SubMesh>? ReadTexturedModel(RdbReader rdb, uint meshIndexId)
		{
			if (!rdb.TryGet(MeshIndexFileType, meshIndexId, out var idxEntry))
				return null;

			var mesh = MeshIndexFile.Parse(meshIndexId, rdb.ReadDecompressed(idxEntry));
			if (mesh.RenderMeshId is not { } rmId || !rdb.TryGet(RenderMeshType, rmId, out var rmEntry) || !rmEntry.IsLocal)
				return null;

			var blob = rdb.ReadDecompressed(rmEntry);
			var subs = RenderMeshReader.Read(blob);
			if (subs.Count == 0)
				return null;

			var diffuse = DecodeDiffusePng(rdb, mesh, blob);
			return diffuse == null ? subs : subs.Select(s => s with { BaseColorPng = diffuse }).ToList();
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
				if (mesh.RenderMeshId is not { } rmId || !rdb.TryGet(RenderMeshType, rmId, out var rmEntry) || !rmEntry.IsLocal)
					continue;

				var subs = RenderMeshReader.Read(rdb.ReadDecompressed(rmEntry));
				if (subs.Count == 0)
					continue;

				all.AddRange(subs);
				used.Add(meshIndexId);
				firstDiffuse ??= DecodeDiffusePng(rdb, mesh);
			}

			if (all.Count > 0)
				File.WriteAllBytes(outPath, GlbWriter.Build(all, firstDiffuse));

			return new AssembleResult(used, all.Sum(s => s.VertexCount), all.Sum(s => s.TriangleCount));
		}

		public readonly record struct AssembleResult(IReadOnlyList<uint> Used, int Vertices, int Triangles);

		// Decode a RenderMesh (type 1010001) DIRECTLY by its own id - no MESHINDEXFILE wrapper. Character appearance
		// resources reference geometry this way: a *hairmesh_NNN ref is the rendermesh id NNN itself. Diffuse comes from
		// the blob's header texture refs (these have no manifest/materials). Null if missing/non-local/empty.
		public static IReadOnlyList<RenderMeshReader.SubMesh>? ReadRenderMeshDirect(RdbReader rdb, uint renderMeshId)
		{
			if (!rdb.TryGet(RenderMeshType, renderMeshId, out var rmEntry) || !rmEntry.IsLocal)
				return null;

			var blob = rdb.ReadDecompressed(rmEntry);
			var subs = RenderMeshReader.Read(blob);
			if (subs.Count == 0)
				return null;

			byte[]? diffuse = null;
			foreach (var (type, id) in RenderMeshReader.ReadHeaderTextureRefs(blob))
				if (TryDecodeTexture(rdb, type, id, out diffuse))
					break;

			return diffuse == null ? subs : subs.Select(s => s with { BaseColorPng = diffuse }).ToList();
		}

		// Decode one model's geometry to its sub-meshes (null if the mesh-index or its local geometry is missing).
		public static IReadOnlyList<RenderMeshReader.SubMesh>? ReadModel(RdbReader rdb, uint meshIndexId)
		{
			if (!rdb.TryGet(MeshIndexFileType, meshIndexId, out var idxEntry))
				return null;

			var mesh = MeshIndexFile.Parse(meshIndexId, rdb.ReadDecompressed(idxEntry));
			if (mesh.RenderMeshId is not { } rmId || !rdb.TryGet(RenderMeshType, rmId, out var rmEntry) || !rmEntry.IsLocal)
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
			if (!rdb.TryGet(MonsterDataFile.Type, monsterDataId, out var e) || !e.IsLocal)
				return [];

			return MonsterDataFile.Parse(monsterDataId, rdb.ReadDecompressed(e)).MeshIds;
		}

		// Assemble a character from decoded parts: body (meshindex) + head-local RenderMeshes (head/eyes/lashes from a
		// MonsterData, seated together at the neck) + an optional world-space hair RenderMesh (added untranslated).
		public static byte[]? BuildCharacterFromParts(RdbReader rdb, uint bodyMeshIndexId,
			IReadOnlyList<uint> headLocalRenderMeshIds, uint? hairWorldRenderMeshId)
		{
			var body = ReadTexturedModel(rdb, bodyMeshIndexId);
			if (body == null)
				return null;

			var all = new List<RenderMeshReader.SubMesh>(body);

			var headParts = new List<RenderMeshReader.SubMesh>();
			foreach (var rm in headLocalRenderMeshIds)
				if (ReadRenderMeshDirect(rdb, rm) is { } part)
					headParts.AddRange(part);

			// Hair is authored in world space (head at ~y1.7); it's the ground truth for where the head sits.
			var hair = hairWorldRenderMeshId is { } hr ? ReadRenderMeshDirect(rdb, hr) : null;

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
					var hc = Center(hair);
					var sc = Center(seated);
					seated = seated.Select(s => Translate(s, hc.x - sc.x, 0, hc.z - sc.z)).ToList();
				}

				all.AddRange(seated);
			}

			if (hair is { Count: > 0 })
				all.AddRange(hair.Select(s => s with { AlphaMask = true }));

			return GlbWriter.Build(all, null);
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
