using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Faolan.Core.Rdb.Assets;
using Xunit;

namespace Faolan.Tests
{
	// Validates the skinned-glb path (#275 rig import) WITHOUT the client RDB: a synthetic skeleton + a synthetic
	// skinned sub-mesh exercise GlbWriter's skin emission, the local-palette -> skeleton-by-name remap (the bug where
	// the mesh's compact bone palette was treated as a direct skeleton index), and rest-pose identity.
	public class RigSkinningTests
	{
		// Build a SkeletonReader blob: [12-byte header][u32 ver][u32 count] then per bone
		// [u32 nameLen][name][quat xyzw][trans xyz][u32 childCount][children...].
		private static byte[] SkeletonBlob((string Name, float[] Quat, float[] Trans, int[] Kids)[] bones)
		{
			var ms = new MemoryStream();
			void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b); }
			void F32(float v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); ms.Write(b); }
			ms.Write(new byte[12]);
			U32(0x10000);
			U32((uint)bones.Length);
			foreach (var (name, quat, trans, kids) in bones)
			{
				var nb = Encoding.Latin1.GetBytes(name);
				U32((uint)nb.Length);
				ms.Write(nb);
				foreach (var f in quat) F32(f);
				foreach (var f in trans) F32(f);
				U32((uint)kids.Length);
				foreach (var k in kids) U32((uint)k);
			}

			return ms.ToArray();
		}

		[Fact]
		public void SkinnedGlb_RemapsPaletteByName_AndReproducesBindPose()
		{
			// 3-bone chain along +Y: root -> mid -> tip. Identity rotations, each child +1.0 in Y from its parent's local.
			var skeleton = SkeletonReader.Read(SkeletonBlob(
			[
				("rig_root", [0, 0, 0, 1], [0, 0, 0], [1]),
				("rig_mid", [0, 0, 0, 1], [0, 1, 0], [2]),
				("rig_tip", [0, 0, 0, 1], [0, 1, 0], [])
			]));
			Assert.Equal(3, skeleton.Count);
			Assert.Equal(2f, skeleton[2].WorldPosition.Y, 3); // tip world Y = 0+1+1

			// Sub-mesh whose LOCAL palette deliberately reverses skeleton order: local 0 -> "rig_tip" (skel 2),
			// local 1 -> "rig_root" (skel 0). v0 binds local 0 (=> joint 2), v1 binds local 1 (=> joint 0).
			var sub = new RenderMeshReader.SubMesh(
				Positions: [0f, 2f, 0f, 0f, 0f, 0f],
				Normals: null,
				Uvs: null,
				Indices: [0, 1, 0])
			{
				BlendBones = [0, 0, 0, 0, 1, 0, 0, 0],
				BlendWeights = [1, 0, 0, 0, 1, 0, 0, 0],
				BonePalette = ["rig_tip", "rig_root"]
			};
			Assert.True(sub.IsSkinned);

			var glb = GlbWriter.Build([sub], null, skeleton);
			var (gltf, bin) = ParseGlb(glb);

			// Structure: a skin with one joint per bone + the mesh node references it.
			var skins = gltf.GetProperty("skins");
			Assert.Equal(1, skins.GetArrayLength());
			Assert.Equal(3, skins[0].GetProperty("joints").GetArrayLength());
			Assert.Equal(0, gltf.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes").GetProperty("POSITION").GetInt32() >= 0 ? 0 : -1);

			var prim = gltf.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes");
			var joints = ReadAccessorBytes(gltf, bin, prim.GetProperty("JOINTS_0").GetInt32());

			// THE remap assertion: v0's first joint must be skeleton index 2 (rig_tip), v1's must be 0 (rig_root) -
			// proving the local palette index was resolved through the bone NAME, not used as a direct skeleton index.
			Assert.Equal(2, joints[0]);
			Assert.Equal(0, joints[4]);
		}

		private static (JsonElement Gltf, byte[] Bin) ParseGlb(byte[] glb)
		{
			Assert.Equal(0x46546C67u, BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(0, 4)));
			var p = 12;
			byte[]? json = null, bin = null;
			while (p < glb.Length)
			{
				var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(p, 4));
				var type = BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(p + 4, 4));
				var chunk = glb[(p + 8)..(p + 8 + len)];
				if (type == 0x4E4F534A) json = chunk;
				else if (type == 0x004E4942) bin = chunk;
				p += 8 + len;
			}

			return (JsonDocument.Parse(json!).RootElement, bin!);
		}

		// Read a UNSIGNED_BYTE accessor's raw values (used for JOINTS_0).
		private static byte[] ReadAccessorBytes(JsonElement gltf, byte[] bin, int accessor)
		{
			var acc = gltf.GetProperty("accessors")[accessor];
			var bv = gltf.GetProperty("bufferViews")[acc.GetProperty("bufferView").GetInt32()];
			var off = (bv.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0) + (acc.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
			var comps = acc.GetProperty("type").GetString() == "VEC4" ? 4 : 1;
			var n = acc.GetProperty("count").GetInt32() * comps;
			return bin[off..(off + n)];
		}
	}
}
