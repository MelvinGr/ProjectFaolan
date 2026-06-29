using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Faolan.Core.Rdb.Assets
{
	// Minimal binary-glTF (.glb) writer for decoded RenderMesh sub-meshes (#275 Phase 2). One glTF mesh with one
	// primitive per sub-mesh; each primitive gets POSITION (+ NORMAL/TEXCOORD_0 when present) and a u32 index buffer.
	// glb = 12-byte header + JSON chunk + BIN chunk; we pack all accessor data into the single BIN buffer. This is the
	// format the eventual Blazor three.js/Babylon viewer loads directly.
	public static class GlbWriter
	{
		private const uint GlbMagic = 0x46546C67; // "glTF"
		private const uint ChunkJson = 0x4E4F534A; // "JSON"
		private const uint ChunkBin = 0x004E4942; // "BIN\0"

		private const int Float = 5126, UInt = 5125; // glTF componentTypes
		private const int ArrayBuffer = 34962, ElementArrayBuffer = 34963; // bufferView targets

		// baseColorPng (optional): a fallback diffuse for sub-meshes that don't carry their own SubMesh.BaseColorPng.
		// Each distinct PNG becomes its own glTF material/texture, so a body + head (or multi-material prop) texture
		// independently.
		public static byte[] Build(IReadOnlyList<RenderMeshReader.SubMesh> subs, byte[]? baseColorPng = null)
		{
			var bin = new MemoryStream();
			var bufferViews = new List<object>();
			var accessors = new List<object>();
			var primitives = new List<object>();

			// Material 0 is the untextured default; each distinct (diffuse PNG, alpha-mode) pair gets its own material.
			var textures = new List<(byte[] Png, bool AlphaMask)>();
			var materialOf = new Dictionary<(byte[], bool), int>();

			foreach (var sub in subs)
			{
				var idxAcc = AddAccessor(bin, bufferViews, accessors, ElementArrayBuffer, UInt, sub.Indices.Length, "SCALAR",
					w => { foreach (var i in sub.Indices) WriteU32(w, (uint)i); });

				var attributes = new Dictionary<string, int>
				{
					["POSITION"] = AddAccessor(bin, bufferViews, accessors, ArrayBuffer, Float, sub.VertexCount, "VEC3",
						w => { foreach (var f in sub.Positions) WriteF32(w, f); }, Bounds(sub.Positions, 3))
				};

				// Always emit SMOOTH normals welded by position. The RDB's packed UByte4N normals are ambiguous (and the
				// browser's computeVertexNormals() facets the mesh because skinned bodies split vertices at every UV-island
				// seam). Welding by position re-fuses those seam-split vertices so curvature shades continuously.
				var normals = SmoothNormals(sub.Positions, sub.Indices);
				attributes["NORMAL"] = AddAccessor(bin, bufferViews, accessors, ArrayBuffer, Float, sub.VertexCount, "VEC3",
					w => { foreach (var f in normals) WriteF32(w, f); });

				if (sub.Uvs != null)
					attributes["TEXCOORD_0"] = AddAccessor(bin, bufferViews, accessors, ArrayBuffer, Float, sub.VertexCount, "VEC2",
						w => { foreach (var f in sub.Uvs) WriteF32(w, f); });

				var png = sub.BaseColorPng ?? baseColorPng;
				var material = 0;
				if (png is { Length: > 0 } && sub.Uvs != null)
				{
					var key = (png, sub.AlphaMask);
					if (!materialOf.TryGetValue(key, out material))
					{
						textures.Add(key);
						material = textures.Count; // 0 = default, textured materials start at 1
						materialOf[key] = material;
					}
				}

				primitives.Add(new Dictionary<string, object>
				{
					["attributes"] = attributes,
					["indices"] = idxAcc,
					["material"] = material,
					["mode"] = 4 // TRIANGLES
				});
			}

			// Embed each distinct diffuse PNG as an image bufferView and build the material/texture/image arrays.
			var materials = new List<object> { new Dictionary<string, object> { ["name"] = "default", ["pbrMetallicRoughness"] = new Dictionary<string, object> { ["baseColorFactor"] = new[] { 0.78f, 0.74f, 0.7f, 1f }, ["metallicFactor"] = 0.0f, ["roughnessFactor"] = 1.0f }, ["doubleSided"] = true } };
			var images = new List<object>();
			var glTextures = new List<object>();
			for (var i = 0; i < textures.Count; i++)
			{
				var (png, alphaMask) = textures[i];
				Align(bin);
				var imgOffset = (int)bin.Length;
				bin.Write(png);
				var imgBv = bufferViews.Count;
				bufferViews.Add(new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = imgOffset, ["byteLength"] = png.Length });
				images.Add(new Dictionary<string, object> { ["bufferView"] = imgBv, ["mimeType"] = "image/png" });
				glTextures.Add(new Dictionary<string, object> { ["source"] = i, ["sampler"] = 0 });
				var material = new Dictionary<string, object>
				{
					["name"] = $"mat{i}",
					["pbrMetallicRoughness"] = new Dictionary<string, object> { ["baseColorTexture"] = new Dictionary<string, object> { ["index"] = i, ["texCoord"] = 0 }, ["metallicFactor"] = 0.0f, ["roughnessFactor"] = 1.0f },
					["doubleSided"] = true
				};
				if (alphaMask)
				{
					material["alphaMode"] = "MASK";
					material["alphaCutoff"] = 0.5f;
				}

				materials.Add(material);
			}

			var binBytes = bin.ToArray();
			var gltf = new Dictionary<string, object>
			{
				["asset"] = new Dictionary<string, object> { ["version"] = "2.0", ["generator"] = "Faolan RDB RenderMesh extractor (#275)" },
				["scene"] = 0,
				["scenes"] = new[] { new Dictionary<string, object> { ["nodes"] = new[] { 0 } } },
				["nodes"] = new[] { new Dictionary<string, object> { ["mesh"] = 0 } },
				["meshes"] = new[] { new Dictionary<string, object> { ["primitives"] = primitives } },
				["materials"] = materials,
				["buffers"] = new[] { new Dictionary<string, object> { ["byteLength"] = binBytes.Length } },
				["bufferViews"] = bufferViews,
				["accessors"] = accessors
			};

			if (textures.Count > 0)
			{
				gltf["images"] = images;
				gltf["samplers"] = new[] { new Dictionary<string, object> { ["wrapS"] = 10497, ["wrapT"] = 10497, ["magFilter"] = 9729, ["minFilter"] = 9987 } };
				gltf["textures"] = glTextures;
			}

			var json = JsonSerializer.SerializeToUtf8Bytes(gltf);
			return Assemble(json, binBytes);
		}

		private static int AddAccessor(MemoryStream bin, List<object> bufferViews, List<object> accessors, int target,
			int componentType, int count, string type, Action<MemoryStream> write, (float[] min, float[] max)? bounds = null)
		{
			Align(bin);
			var offset = (int)bin.Length;
			write(bin);
			var len = (int)bin.Length - offset;

			var bvIndex = bufferViews.Count;
			bufferViews.Add(new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = len, ["target"] = target });

			var acc = new Dictionary<string, object> { ["bufferView"] = bvIndex, ["componentType"] = componentType, ["count"] = count, ["type"] = type };
			if (bounds is { } b)
			{
				acc["min"] = b.min;
				acc["max"] = b.max;
			}

			accessors.Add(acc);
			return accessors.Count - 1;
		}

		// Per-vertex smooth normals, area-weighted from face normals, with vertices welded by quantized position so the
		// smoothing crosses UV-island seams (where the mesh duplicates a vertex with the same XYZ but a different UV).
		// Without the weld, seam-split vertices each keep only their own faces' normals and the surface shades faceted.
		private static float[] SmoothNormals(float[] pos, int[] idx)
		{
			var vc = pos.Length / 3;
			var rep = new int[vc];
			var byPos = new Dictionary<(long, long, long), int>(vc);
			for (var i = 0; i < vc; i++)
			{
				var key = (Q(pos[i * 3]), Q(pos[i * 3 + 1]), Q(pos[i * 3 + 2]));
				if (!byPos.TryGetValue(key, out var r))
					byPos[key] = r = i;
				rep[i] = r;
			}

			var acc = new float[vc * 3];
			for (var t = 0; t + 2 < idx.Length; t += 3)
			{
				int a = idx[t], b = idx[t + 1], c = idx[t + 2];
				// Face normal = (B-A) x (C-A); magnitude is twice the triangle area, so the sum is area-weighted.
				float ex1 = pos[b * 3] - pos[a * 3], ey1 = pos[b * 3 + 1] - pos[a * 3 + 1], ez1 = pos[b * 3 + 2] - pos[a * 3 + 2];
				float ex2 = pos[c * 3] - pos[a * 3], ey2 = pos[c * 3 + 1] - pos[a * 3 + 1], ez2 = pos[c * 3 + 2] - pos[a * 3 + 2];
				float nx = ey1 * ez2 - ez1 * ey2, ny = ez1 * ex2 - ex1 * ez2, nz = ex1 * ey2 - ey1 * ex2;
				foreach (var v in (ReadOnlySpan<int>)[a, b, c])
				{
					var r = rep[v];
					acc[r * 3] += nx;
					acc[r * 3 + 1] += ny;
					acc[r * 3 + 2] += nz;
				}
			}

			var normals = new float[vc * 3];
			for (var i = 0; i < vc; i++)
			{
				var r = rep[i];
				float x = acc[r * 3], y = acc[r * 3 + 1], z = acc[r * 3 + 2];
				var len = MathF.Sqrt(x * x + y * y + z * z);
				if (len <= 1e-8f)
				{
					normals[i * 3 + 1] = 1f; // degenerate fallback (up)
					continue;
				}

				normals[i * 3] = x / len;
				normals[i * 3 + 1] = y / len;
				normals[i * 3 + 2] = z / len;
			}

			return normals;
		}

		private static long Q(float f) => (long)MathF.Round(f * 1000f); // weld tolerance: 1 unit = 1mm at AoC scale

		private static (float[] min, float[] max) Bounds(float[] data, int comps)
		{
			var min = Enumerable.Repeat(float.MaxValue, comps).ToArray();
			var max = Enumerable.Repeat(float.MinValue, comps).ToArray();
			for (var i = 0; i < data.Length; i += comps)
				for (var c = 0; c < comps; c++)
				{
					min[c] = Math.Min(min[c], data[i + c]);
					max[c] = Math.Max(max[c], data[i + c]);
				}

			return (min, max);
		}

		private static byte[] Assemble(byte[] json, byte[] bin)
		{
			var jsonPad = Pad(json.Length, 0x20);
			var binPad = Pad(bin.Length, 0x00);
			var total = 12 + 8 + json.Length + jsonPad.Length + 8 + bin.Length + binPad.Length;

			using var ms = new MemoryStream(total);
			WriteU32(ms, GlbMagic);
			WriteU32(ms, 2);
			WriteU32(ms, (uint)total);

			WriteU32(ms, (uint)(json.Length + jsonPad.Length));
			WriteU32(ms, ChunkJson);
			ms.Write(json);
			ms.Write(jsonPad);

			WriteU32(ms, (uint)(bin.Length + binPad.Length));
			WriteU32(ms, ChunkBin);
			ms.Write(bin);
			ms.Write(binPad);
			return ms.ToArray();
		}

		private static byte[] Pad(int len, byte fill)
		{
			var rem = (4 - len % 4) % 4;
			return rem == 0 ? [] : Enumerable.Repeat(fill, rem).ToArray();
		}

		private static void Align(MemoryStream s)
		{
			while (s.Length % 4 != 0)
				s.WriteByte(0);
		}

		private static void WriteU32(Stream s, uint v)
		{
			Span<byte> b = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32LittleEndian(b, v);
			s.Write(b);
		}

		private static void WriteF32(Stream s, float v)
		{
			Span<byte> b = stackalloc byte[4];
			BinaryPrimitives.WriteSingleLittleEndian(b, v);
			s.Write(b);
		}
	}
}
