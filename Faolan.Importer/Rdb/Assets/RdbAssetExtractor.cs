using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Faolan.Importer.Rdb.Assets
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
						File.WriteAllBytes(Path.Combine(outDir, glb), GlbWriter.Build(subs));
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
