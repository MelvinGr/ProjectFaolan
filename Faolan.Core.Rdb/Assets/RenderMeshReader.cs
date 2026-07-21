using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Faolan.Core.Rdb.Assets
{
	// Decodes the Age of Conan RDB RenderMesh geometry blob (type 1010001) - the geometry a MESHINDEXFILE points at.
	// C# port of the validated dissector (tools/capture/decode_rendermesh.py, docs/rdb/rendermesh-grammar.md, #275).
	//
	// Each sub-mesh is a standard Direct3D9 vertex declaration (a list of D3DVERTEXELEMENT9 terminated by D3DDECL_END
	// = bytes FF 00 00 00 11 00 00 00, where 0x11 = D3DDECLTYPE_UNUSED), immediately followed by [stride u32]
	// [vertexCount u32][vertex buffer], then [triCount u32][triCount*3 u16 indices]. The declaration gives each
	// attribute's offset+type within the stride; AoC meshes use POSITION(float3 @0), TANGENT/BINORMAL/NORMAL(UBYTE4N),
	// TEXCOORD(float2). A blob may hold several sub-meshes (iterate the anchor).
	public static class RenderMeshReader
	{
		private static readonly byte[] DeclEnd = [0xFF, 0x00, 0x00, 0x00, 0x11, 0x00, 0x00, 0x00];

		// Texture types that can appear in a rendermesh header's ref table.
		private static readonly uint[] TextureTypes = [1010004, 1010006, 1010008, 1010013, 1010218, 1066606, 1066610];

		// How a sub-mesh's diffuse alpha channel is rendered (see SubMesh.Alpha).
		public enum AlphaMode
		{
			Opaque,
			Cutout,
			Blend
		}

		public enum DeclType : byte
		{
			Float1 = 0,
			Float2 = 1,
			Float3 = 2,
			Float4 = 3,
			D3DColor = 4,
			UByte4 = 5,
			Short2 = 6,
			Short4 = 7,
			UByte4N = 8,
			Short2N = 9,
			Short4N = 10,
			UShort2N = 11,
			UShort4N = 12,
			UDec3 = 13,
			Dec3N = 14,
			Float16_2 = 15,
			Float16_4 = 16,
			Unused = 17
		}

		public enum DeclUsage : byte
		{
			Position = 0,
			BlendWeight = 1,
			BlendIndices = 2,
			Normal = 3,
			PSize = 4,
			TexCoord = 5,
			Tangent = 6,
			Binormal = 7,
			TessFactor = 8,
			PositionT = 9,
			Color = 10,
			Fog = 11,
			Depth = 12,
			Sample = 13
		}

		public static IReadOnlyList<SubMesh> Read(ReadOnlySpan<byte> blob)
		{
			var palettes = ParseBonePalettes(blob);
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
				{
					// A skinned section indexes the bone palette that precedes its declaration (the g_BoneN= block sits in
					// the section's material-param string, right before the vertex decl). Pick the nearest one before it.
					if (sub.BlendBones != null)
					{
						sub = sub with
						{
							BonePalette = NearestPaletteBefore(palettes, anchor)
						};
					}

					subs.Add(sub);
				}
			}

			return subs;
		}

		// The textures referenced in the blob header (used when the manifest has no materials, e.g. skinned bodies).
		// Two encodings: static props (ver 0x9003) store [type u32][id u32] pairs; skinned bodies (ver 0x7000) store
		// bare [id u32] with the type implied FCTX (1010004). We disambiguate by whether the first word is a known type.
		public static IReadOnlyList<(uint Type, uint Id)> ReadHeaderTextureRefs(ReadOnlySpan<byte> blob)
		{
			if (blob.Length < 24)
				return [];

			var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(16, 4));
			if (count is < 1 or > 64 || 20 + count * 4 > blob.Length)
				return [];

			var first = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(20, 4));
			var pairs = Array.IndexOf(TextureTypes, first) >= 0; // [type,id] vs bare [id]
			var refs = new List<(uint, uint)>();

			if (pairs)
			{
				if (20 + count * 8 > blob.Length)
					return [];

				for (var i = 0; i < count; i++)
					refs.Add((BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(20 + i * 8, 4)), BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(24 + i * 8, 4))));
			}
			else
			{
				for (var i = 0; i < count; i++)
					refs.Add((1010004, BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(20 + i * 4, 4))));
			}

			return refs;
		}

		private static VertexElement? Find(List<VertexElement> els, DeclUsage usage)
		{
			foreach (var e in els)
			{
				if (e.Usage == usage)
					return e;
			}

			return null;
		}

		private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle, int start)
		{
			for (var i = start; i + needle.Length <= haystack.Length; i++)
			{
				if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
					return i;
			}

			return -1;
		}

		private static string[]? NearestPaletteBefore(List<(int Offset, string[] Names)> palettes, int anchor)
		{
			string[]? best = null;
			var bestOff = int.MinValue;
			foreach (var (off, names) in palettes)
			{
				if (off <= anchor && off > bestOff)
				{
					bestOff = off;
					best = names;
				}
			}

			// A mesh with a single palette (the common case) applies it to every section even if the string sits after a
			// section's anchor; fall back to the first palette so single-material skinned bodies still bind.
			return best ?? (palettes.Count > 0
				? palettes[0].Names
				: null);
		}

		// The mesh's per-section bone palette is exported as ASCII material params: g_Bone0="rig_bone_Head" g_Bone1="..."
		// ... g_NumberOfBones=N. Each entry's local index is the BLENDINDICES value (already halved); the value is the
		// skeleton bone NAME. We scan the whole blob for these blocks and remember each block's byte offset so a section
		// can bind to the palette that precedes it.
		private static List<(int Offset, string[] Names)> ParseBonePalettes(ReadOnlySpan<byte> blob)
		{
			var text = Encoding.Latin1.GetString(blob);
			var result = new List<(int, string[])>();
			var rx = new Regex("g_Bone(\\d+)=\"([^\"]*)\"");
			var blockStart = 0;
			while (true)
			{
				var first = text.IndexOf("g_Bone0=\"", blockStart, StringComparison.Ordinal);
				if (first < 0)
					break;

				// Collect the contiguous g_BoneK="name" run from here up to g_NumberOfBones (or the next non-bone token).
				var end = text.IndexOf("g_NumberOfBones", first, StringComparison.Ordinal);
				var slice = end > first
					? text.Substring(first, end - first)
					: text.Substring(first, Math.Min(8192, text.Length - first));
				var byIndex = new SortedDictionary<int, string>();
				foreach (Match m in rx.Matches(slice))
					byIndex[int.Parse(m.Groups[1].Value)] = m.Groups[2].Value;

				if (byIndex.Count > 0 && byIndex.ContainsKey(0))
				{
					var names = new string[byIndex.Keys.Max() + 1];
					foreach (var kv in byIndex)
						names[kv.Key] = kv.Value;
					result.Add((first, names));
				}

				blockStart = first + 9;
			}

			return result;
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

		private static float ReadFloat(ReadOnlySpan<byte> v, int offset)
		{
			return BinaryPrimitives.ReadSingleLittleEndian(v.Slice(offset, 4));
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
			if (vbase + (long)count * stride + 4 > blob.Length)
				return null;

			var posEl = Find(elements, DeclUsage.Position);
			if (posEl == null)
				return null;

			var normEl = Find(elements, DeclUsage.Normal);
			var uvEl = Find(elements, DeclUsage.TexCoord);
			// Skinned bodies/equipment: BLENDINDICES is UBYTE4 (4 register indices), BLENDWEIGHT is UBYTE4N (4 weights
			// /255). Only emit skin influences when BOTH are present in the expected encodings.
			var biEl = Find(elements, DeclUsage.BlendIndices);
			var bwEl = Find(elements, DeclUsage.BlendWeight);
			var skinned = biEl is { Type: DeclType.UByte4 } && bwEl is { Type: DeclType.UByte4N };

			var positions = new float[count * 3];
			var normals = normEl is { Type: DeclType.UByte4N }
				? new float[count * 3]
				: null;
			// Props store UV as FLOAT2; skinned character meshes store it as SHORT2N (normalized shorts).
			var uvs = uvEl is { Type: DeclType.Float2 or DeclType.Short2N }
				? new float[count * 2]
				: null;
			var bones = skinned
				? new int[count * 4]
				: null;
			var weights = skinned
				? new float[count * 4]
				: null;

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
					else // Short2N - genuine signed D3D SHORT2N in [-1,1] (short/32767), mapped to [0,1] texture space
						// via the standard *0.5+0.5 bias. Decoding it unsigned (/65535) is a half-texel shift that LOOKS
						// plausible mid-torso but tears every triangle crossing the centerline u=0 (verified: 130 torn
						// tris unsigned vs 0 biased), smearing the texture across the body. The bias keeps it continuous.
					{
						uvs[i * 2 + 0] = BinaryPrimitives.ReadInt16LittleEndian(v.Slice(o + 0, 2)) / 32767f * 0.5f + 0.5f;
						uvs[i * 2 + 1] = BinaryPrimitives.ReadInt16LittleEndian(v.Slice(o + 2, 2)) / 32767f * 0.5f + 0.5f;
					}
				}

				if (bones != null && weights != null)
				{
					var io = biEl!.Value.Offset;
					var wo = bwEl!.Value.Offset;
					for (var k = 0; k < 4; k++)
					{
						bones[i * 4 + k] = v[io + k] >> 1; // on-wire index is the bone's palette register (bone * 2)
						weights[i * 4 + k] = v[wo + k] / 255f;
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
			return new SubMesh(positions, normals, uvs, indices)
			{
				BlendBones = bones,
				BlendWeights = weights
			};
		}

		public readonly record struct VertexElement(int Offset, DeclType Type, DeclUsage Usage, int UsageIndex);

		// One drawable sub-mesh: flat XYZ positions, optional XYZ normals + UV (UVs absent => empty), triangle indices.
		// BaseColorPng (optional, set by the caller) lets sub-meshes from different models carry their own diffuse.
		// BlendBones/BlendWeights (skinned bodies & equipment only): 4 skin influences per vertex. BlendBones are the
		// LOGICAL skeleton bone indices (the on-wire BLENDINDICES are doubled - the engine's matrix-palette register
		// stride is 2 per bone - so the reader has already halved them); BlendWeights are /255-normalised and sum to ~1.
		public sealed record SubMesh(float[] Positions, float[]? Normals, float[]? Uvs, int[] Indices)
		{
			// How the diffuse's alpha channel renders: Opaque (ignore it - e.g. an alpha specular/gloss map), Cutout
			// (alpha-test holes, e.g. hair strands -> glTF MASK) or Blend (semi-transparent, e.g. sheer cloth -> BLEND).
			public AlphaMode Alpha { get; init; }
			public bool IsSkinned => BlendBones != null && BonePalette != null;
			public byte[]? BaseColorPng { get; init; }

			// Optional tangent-space normal map (PNG) attached as the glTF material's normalTexture - for the body it's
			// derived from the MIXD surface-detail/height map (muscle/ab/navel relief the flat diffuse omits).
			public byte[]? NormalPng { get; init; }
			public float[]? BlendWeights { get; init; }
			public int TriangleCount => Indices.Length / 3;
			public int VertexCount => Positions.Length / 3;

			// Per-vertex skin influences (null on static props): 4 entries per vertex, flat. BlendBones[i*4+k] is a LOCAL
			// index into this sub-mesh's BonePalette; the matching BlendWeights[i*4+k] is its weight. BonePalette[local] is
			// the skeleton bone NAME ([[SkeletonReader]]) - the mesh carries its own compact bone table (g_BoneN= in the
			// material string), so the consumer maps palette name -> skeleton bone index to get the real skin joint.
			public int[]? BlendBones { get; init; }
			public string[]? BonePalette { get; init; }
		}
	}
}
