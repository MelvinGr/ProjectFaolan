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

		private const int Float = 5126, UInt = 5125, UByte = 5121; // glTF componentTypes
		private const int ArrayBuffer = 34962, ElementArrayBuffer = 34963; // bufferView targets

		// baseColorPng (optional): a fallback diffuse for sub-meshes that don't carry their own SubMesh.BaseColorPng.
		// Each distinct PNG becomes its own glTF material/texture, so a body + head (or multi-material prop) texture
		// independently.
		//
		// skeleton (optional): when supplied AND every sub-mesh carries skin influences, the mesh is written as a glTF
		// skin - a node per bone (TRS = the bone's local rotation+translation, parented per the hierarchy), a skin with
		// inverseBindMatrices, and JOINTS_0/WEIGHTS_0 on each primitive. The bone bind-pose world and the mesh bind-pose
		// vertices share one frame, so at rest the SkinnedMesh is pixel-identical to the static mesh - and now posable.
		public static byte[] Build(IReadOnlyList<RenderMeshReader.SubMesh> subs, byte[]? baseColorPng = null,
			IReadOnlyList<SkeletonReader.Bone>? skeleton = null)
		{
			var skinned = skeleton is { Count: > 0 } && subs.Count > 0 && subs.All(s => s.IsSkinned);
			// Map a skeleton bone NAME to its index, so a sub-mesh's local bone-palette name resolves to the real joint.
			var boneIndexByName = skinned
				? skeleton!.Select((b, i) => (b.Name, i)).GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First().i)
				: null;
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

				if (skinned)
				{
					// JOINTS_0 is UNSIGNED_BYTE VEC4 (71 bones < 256); WEIGHTS_0 is FLOAT VEC4, renormalised so the four
					// influences sum to 1 (the /255 weights sum to ~1 already, but a degenerate all-zero vertex would make
					// a renderer divide by zero, so guard it to a single full weight on joint 0).
					var bones = sub.BlendBones!;
					var wts = sub.BlendWeights!;
					var palette = sub.BonePalette!;
					// Resolve each local palette index to a real skeleton joint via the bone name (unknown -> joint 0).
					var local2joint = palette
						.Select(n => n != null && boneIndexByName!.TryGetValue(n, out var ji) ? ji : 0)
						.ToArray();
					attributes["JOINTS_0"] = AddAccessor(bin, bufferViews, accessors, ArrayBuffer, UByte, sub.VertexCount, "VEC4",
						w => { foreach (var b in bones) w.WriteByte((byte)(b >= 0 && b < local2joint.Length ? local2joint[b] : 0)); });
					attributes["WEIGHTS_0"] = AddAccessor(bin, bufferViews, accessors, ArrayBuffer, Float, sub.VertexCount, "VEC4",
						w =>
						{
							for (var i = 0; i < sub.VertexCount; i++)
							{
								float w0 = wts[i * 4], w1 = wts[i * 4 + 1], w2 = wts[i * 4 + 2], w3 = wts[i * 4 + 3];
								var sum = w0 + w1 + w2 + w3;
								if (sum <= 1e-6f) { WriteF32(w, 1f); WriteF32(w, 0f); WriteF32(w, 0f); WriteF32(w, 0f); }
								else { WriteF32(w, w0 / sum); WriteF32(w, w1 / sum); WriteF32(w, w2 / sum); WriteF32(w, w3 / sum); }
							}
						});
				}

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

			// Skin: a node per bone (child of node 0 = the mesh node, so bone node index = 1 + bone index), plus the
			// inverseBindMatrices accessor. inverseBindMatrices[j] = inverse(bone[j].bindWorld); written in System.Numerics
			// natural field order, which is exactly glTF's column-major encoding of the equivalent column-vector matrix.
			var nodes = new List<object> { skinned
				? new Dictionary<string, object> { ["mesh"] = 0, ["skin"] = 0 }
				: new Dictionary<string, object> { ["mesh"] = 0 } };
			object? skins = null;
			var sceneNodes = new List<int> { 0 };
			if (skinned)
			{
				var bones = skeleton!;
				var ibmAcc = AddAccessor(bin, bufferViews, accessors, -1, Float, bones.Count, "MAT4", w =>
				{
					foreach (var b in bones)
					{
						System.Numerics.Matrix4x4.Invert(b.World, out var inv);
						foreach (var f in MatFields(inv)) WriteF32(w, f);
					}
				});

				for (var j = 0; j < bones.Count; j++)
				{
					var b = bones[j];
					var node = new Dictionary<string, object>
					{
						["name"] = b.Name,
						["translation"] = new[] { b.LocalTranslation.X, b.LocalTranslation.Y, b.LocalTranslation.Z },
						["rotation"] = new[] { b.LocalRotation.X, b.LocalRotation.Y, b.LocalRotation.Z, b.LocalRotation.W }
					};
					if (b.Children is { Length: > 0 })
						node["children"] = b.Children.Where(c => c >= 0 && c < bones.Count).Select(c => 1 + c).ToArray();
					nodes.Add(node);
					if (b.Parent < 0)
						sceneNodes.Add(1 + j); // a root bone hangs off the scene alongside the mesh node
				}

				var rootBone = bones.Select((b, i) => (b, i)).FirstOrDefault(x => x.b.Parent < 0).i;
				skins = new[] { new Dictionary<string, object>
				{
					["joints"] = Enumerable.Range(0, bones.Count).Select(j => 1 + j).ToArray(),
					["inverseBindMatrices"] = ibmAcc,
					["skeleton"] = 1 + rootBone
				} };
			}

			var binBytes = bin.ToArray();
			var gltf = new Dictionary<string, object>
			{
				["asset"] = new Dictionary<string, object> { ["version"] = "2.0", ["generator"] = "Faolan RDB RenderMesh extractor (#275)" },
				["scene"] = 0,
				["scenes"] = new[] { new Dictionary<string, object> { ["nodes"] = sceneNodes.ToArray() } },
				["nodes"] = nodes,
				["meshes"] = new[] { new Dictionary<string, object> { ["primitives"] = primitives } },
				["materials"] = materials,
				["buffers"] = new[] { new Dictionary<string, object> { ["byteLength"] = binBytes.Length } },
				["bufferViews"] = bufferViews,
				["accessors"] = accessors
			};
			if (skins != null)
				gltf["skins"] = skins;

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
			var bv = new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = len };
			if (target >= 0) // inverseBindMatrices (target -1) must NOT carry a bufferView target per the glTF spec
				bv["target"] = target;
			bufferViews.Add(bv);

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

		// System.Numerics.Matrix4x4 in natural (row-major) field order. For a row-vector matrix M this byte layout is
		// identical to glTF's column-major encoding of M's column-vector equivalent (M^T), so an inverse-bind matrix
		// computed with System.Numerics serialises straight into a glTF inverseBindMatrices accessor.
		private static IEnumerable<float> MatFields(System.Numerics.Matrix4x4 m) =>
		[
			m.M11, m.M12, m.M13, m.M14,
			m.M21, m.M22, m.M23, m.M24,
			m.M31, m.M32, m.M33, m.M34,
			m.M41, m.M42, m.M43, m.M44
		];

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
