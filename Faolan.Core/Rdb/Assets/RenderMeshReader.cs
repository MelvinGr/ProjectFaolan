using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Faolan.Core.Rdb.Assets
{
	// Decodes the Age of Conan RDB RenderMesh geometry blob (type 1010001) - the geometry a MESHINDEXFILE points at.
	// C# port of the validated dissector (Captures/scripts/decode_rendermesh.py, docs/rendermesh-grammar.md, #275).
	//
	// Each sub-mesh is a standard Direct3D9 vertex declaration (a list of D3DVERTEXELEMENT9 terminated by D3DDECL_END
	// = bytes FF 00 00 00 11 00 00 00, where 0x11 = D3DDECLTYPE_UNUSED), immediately followed by [stride u32]
	// [vertexCount u32][vertex buffer], then [triCount u32][triCount*3 u16 indices]. The declaration gives each
	// attribute's offset+type within the stride; AoC meshes use POSITION(float3 @0), TANGENT/BINORMAL/NORMAL(UBYTE4N),
	// TEXCOORD(float2). A blob may hold several sub-meshes (iterate the anchor).
	public static class RenderMeshReader
	{
		private static readonly byte[] DeclEnd = [0xFF, 0x00, 0x00, 0x00, 0x11, 0x00, 0x00, 0x00];

		public enum DeclType : byte
		{
			Float1 = 0, Float2 = 1, Float3 = 2, Float4 = 3, D3DColor = 4, UByte4 = 5, Short2 = 6, Short4 = 7,
			UByte4N = 8, Short2N = 9, Short4N = 10, UShort2N = 11, UShort4N = 12, UDec3 = 13, Dec3N = 14,
			Float16_2 = 15, Float16_4 = 16, Unused = 17
		}

		public enum DeclUsage : byte
		{
			Position = 0, BlendWeight = 1, BlendIndices = 2, Normal = 3, PSize = 4, TexCoord = 5, Tangent = 6,
			Binormal = 7, TessFactor = 8, PositionT = 9, Color = 10, Fog = 11, Depth = 12, Sample = 13
		}

		public readonly record struct VertexElement(int Offset, DeclType Type, DeclUsage Usage, int UsageIndex);

		// One drawable sub-mesh: flat XYZ positions, optional XYZ normals + UV (UVs absent => empty), triangle indices.
		public sealed record SubMesh(float[] Positions, float[]? Normals, float[]? Uvs, int[] Indices)
		{
			public int VertexCount => Positions.Length / 3;
			public int TriangleCount => Indices.Length / 3;
		}

		public static IReadOnlyList<SubMesh> Read(ReadOnlySpan<byte> blob)
		{
			var subs = new List<SubMesh>();
			var pos = 0;
			while (true)
			{
				var anchor = IndexOf(blob, DeclEnd, pos);
				if (anchor < 0)
					break;

				var sub = TryReadSection(blob, anchor, out var next);
				pos = next;
				if (sub != null)
					subs.Add(sub);
			}

			return subs;
		}

		private static SubMesh? TryReadSection(ReadOnlySpan<byte> blob, int anchor, out int next)
		{
			next = anchor + DeclEnd.Length;

			var elements = ParseDeclaration(blob, anchor);
			var basePos = anchor + DeclEnd.Length;
			if (basePos + 8 > blob.Length)
				return null;

			var stride = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(basePos, 4));
			var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(basePos + 4, 4));
			if (stride < 12 || stride > 256 || stride % 4 != 0 || count < 1 || count > 2_000_000)
				return null;

			var vbase = basePos + 8;
			if ((long)vbase + (long)count * stride + 4 > blob.Length)
				return null;

			var posEl = Find(elements, DeclUsage.Position);
			if (posEl is null)
				return null;

			var normEl = Find(elements, DeclUsage.Normal);
			var uvEl = Find(elements, DeclUsage.TexCoord);

			var positions = new float[count * 3];
			var normals = normEl is { Type: DeclType.UByte4N } ? new float[count * 3] : null;
			// Props store UV as FLOAT2; skinned character meshes store it as SHORT2N (normalized shorts).
			var uvs = uvEl is { Type: DeclType.Float2 or DeclType.Short2N } ? new float[count * 2] : null;

			for (var i = 0; i < count; i++)
			{
				var v = blob.Slice(vbase + i * stride, stride);
				positions[i * 3 + 0] = ReadFloat(v, posEl.Value.Offset + 0);
				positions[i * 3 + 1] = ReadFloat(v, posEl.Value.Offset + 4);
				positions[i * 3 + 2] = ReadFloat(v, posEl.Value.Offset + 8);

				if (normals != null)
				{
					var o = normEl!.Value.Offset;
					normals[i * 3 + 0] = v[o + 0] / 255f * 2f - 1f;
					normals[i * 3 + 1] = v[o + 1] / 255f * 2f - 1f;
					normals[i * 3 + 2] = v[o + 2] / 255f * 2f - 1f;
				}

				if (uvs != null)
				{
					var o = uvEl!.Value.Offset;
					if (uvEl.Value.Type == DeclType.Float2)
					{
						uvs[i * 2 + 0] = ReadFloat(v, o + 0);
						uvs[i * 2 + 1] = ReadFloat(v, o + 4);
					}
					else // Short2N
					{
						uvs[i * 2 + 0] = BinaryPrimitives.ReadInt16LittleEndian(v.Slice(o + 0, 2)) / 32767f;
						uvs[i * 2 + 1] = BinaryPrimitives.ReadInt16LittleEndian(v.Slice(o + 2, 2)) / 32767f;
					}
				}
			}

			var ibase = vbase + count * stride;
			var tri = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(ibase, 4));
			if (tri < 1 || tri > 2_000_000 || (long)ibase + 4 + (long)tri * 6 > blob.Length)
				return null;

			var indices = new int[tri * 3];
			for (var i = 0; i < indices.Length; i++)
			{
				var idx = BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(ibase + 4 + i * 2, 2));
				if (idx >= count)
					return null; // not a real section - bail so the caller keeps scanning
				indices[i] = idx;
			}

			next = ibase + 4 + tri * 6;
			return new SubMesh(positions, normals, uvs, indices);
		}

		// Walk D3DVERTEXELEMENT9 records (8 bytes: stream u16, offset u16, type u8, method u8, usage u8, usageIndex u8)
		// backward from the D3DDECL_END anchor while they remain well-formed (stream 0, known type/usage, sane offset).
		private static List<VertexElement> ParseDeclaration(ReadOnlySpan<byte> blob, int anchor)
		{
			var els = new List<VertexElement>();
			var p = anchor - 8;
			while (p >= 0)
			{
				var stream = BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(p, 2));
				var offset = BinaryPrimitives.ReadUInt16LittleEndian(blob.Slice(p + 2, 2));
				var type = blob[p + 4];
				var usage = blob[p + 6];
				if (stream != 0 || type > 17 || usage > 13 || offset > 256)
					break;

				els.Add(new VertexElement(offset, (DeclType)type, (DeclUsage)usage, blob[p + 7]));
				p -= 8;
			}

			els.Reverse();
			return els;
		}

		private static VertexElement? Find(List<VertexElement> els, DeclUsage usage)
		{
			foreach (var e in els)
				if (e.Usage == usage)
					return e;
			return null;
		}

		private static float ReadFloat(ReadOnlySpan<byte> v, int offset) =>
			BinaryPrimitives.ReadSingleLittleEndian(v.Slice(offset, 4));

		private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle, int start)
		{
			for (var i = start; i + needle.Length <= haystack.Length; i++)
				if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
					return i;
			return -1;
		}
	}
}
