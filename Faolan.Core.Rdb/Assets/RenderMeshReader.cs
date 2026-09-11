using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Faolan.Core.Extensions;

namespace Faolan.Core.Rdb.Assets
{
	/// <summary>
	///     Decodes the Age of Conan RDB RenderMesh geometry blob (type 1010001), the geometry a MESHINDEXFILE points
	///     at. The grammar is shared with the dissector in tools/capture/decode_rendermesh.py and documented in
	///     docs/rdb/rendermesh-grammar.md.
	/// </summary>
	/// <remarks>
	///     Each sub-mesh is a standard Direct3D9 vertex declaration — a list of D3DVERTEXELEMENT9 terminated by
	///     D3DDECL_END, the bytes FF 00 00 00 11 00 00 00, where 0x11 is D3DDECLTYPE_UNUSED — immediately followed by
	///     [stride u32][vertexCount u32][vertex buffer], then [triCount u32][triCount * 3 u16 indices]. The declaration
	///     gives each attribute's offset and type within the stride; AoC meshes use POSITION (float3 at 0),
	///     TANGENT/BINORMAL/NORMAL (UBYTE4N) and TEXCOORD (float2). A blob may hold several sub-meshes, found by
	///     iterating the anchor.
	/// </remarks>
	public static class RenderMeshReader
	{
		private static readonly byte[] DeclEnd = [0xFF, 0x00, 0x00, 0x00, 0x11, 0x00, 0x00, 0x00];

		/// <summary>
		///     Texture types that can appear in a rendermesh header's ref table.
		/// </summary>
		private static readonly uint[] TextureTypes = [1010004, 1010006, 1010008, 1010013, 1010218, 1066606, 1066610];

		/// <summary>
		///     How a sub-mesh's diffuse alpha channel is rendered; see <see cref="SubMesh.Alpha" />.
		/// </summary>
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
					// A skinned section indexes the bone palette that precedes its declaration: the g_BoneN= block sits
					// in the section's material-param string, right before the vertex decl, so the nearest palette
					// before the section is its own.
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

		/// <summary>
		///     The textures referenced in the blob header, used when the manifest carries no materials, as on skinned
		///     bodies. Two encodings exist: static props (version 0x9003) store [type u32][id u32] pairs, while skinned
		///     bodies (version 0x7000) store a bare [id u32] with the type implied to be FCTX (1010004). They are told
		///     apart by whether the first word is a known type.
		/// </summary>
		public static IReadOnlyList<(uint Type, uint Id)> ReadHeaderTextureRefs(ReadOnlySpan<byte> blob)
		{
			if (blob.Length < 24)
				return [];

			var count = (int)blob.ReadU32Le(16);
			if (count is < 1 or > 64 || 20 + count * 4 > blob.Length)
				return [];

			var first = blob.ReadU32Le(20);
			var pairs = Array.IndexOf(TextureTypes, first) >= 0; // [type,id] vs bare [id]
			var refs = new List<(uint, uint)>();

			if (pairs)
			{
				if (20 + count * 8 > blob.Length)
					return [];

				for (var i = 0; i < count; i++)
					refs.Add((blob.ReadU32Le(20 + i * 8), blob.ReadU32Le(24 + i * 8)));
			}
			else
			{
				for (var i = 0; i < count; i++)
					refs.Add((1010004, blob.ReadU32Le(20 + i * 4)));
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

			// A mesh with a single palette, which is the common case, applies it to every section even when the string
			// sits after a section's anchor, so falling back to the first palette keeps single-material skinned bodies
			// bound.
			return best ?? (palettes.Count > 0
				? palettes[0].Names
				: null);
		}

		/// <summary>
		///     Collects the mesh's per-section bone palettes, which are exported as ASCII material params:
		///     <c>g_Bone0="rig_bone_Head" g_Bone1="..." ... g_NumberOfBones=N</c>. Each entry's local index is the
		///     BLENDINDICES value, already halved, and its value is the skeleton bone name. The whole blob is scanned
		///     and each block's byte offset recorded, so a section can bind to the palette that precedes it.
		/// </summary>
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

				// Collect the contiguous g_BoneK="name" run from here up to g_NumberOfBones, or the next non-bone token.
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

		/// <summary>
		///     Walks D3DVERTEXELEMENT9 records — 8 bytes each: stream u16, offset u16, type u8, method u8, usage u8,
		///     usageIndex u8 — backwards from the D3DDECL_END anchor for as long as they remain well-formed: stream 0,
		///     a known type and usage, and a sane offset.
		/// </summary>
		private static List<VertexElement> ParseDeclaration(ReadOnlySpan<byte> blob, int anchor)
		{
			var els = new List<VertexElement>();
			var p = anchor - 8;
			while (p >= 0)
			{
				var stream = blob.ReadU16Le(p);
				var offset = blob.ReadU16Le(p + 2);
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


		private static SubMesh? TryReadSection(ReadOnlySpan<byte> blob, int anchor, out int next)
		{
			next = anchor + DeclEnd.Length;

			var elements = ParseDeclaration(blob, anchor);
			var basePos = anchor + DeclEnd.Length;
			if (basePos + 8 > blob.Length)
				return null;

			var stride = (int)blob.ReadU32Le(basePos);
			var count = (int)blob.ReadU32Le(basePos + 4);
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
			// On skinned bodies and equipment BLENDINDICES is UBYTE4 (four register indices) and BLENDWEIGHT is
			// UBYTE4N (four weights over 255). Skin influences are only emitted when both are present in those
			// encodings.
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
				positions[i * 3 + 0] = v.ReadF32Le(posEl.Value.Offset + 0);
				positions[i * 3 + 1] = v.ReadF32Le(posEl.Value.Offset + 4);
				positions[i * 3 + 2] = v.ReadF32Le(posEl.Value.Offset + 8);

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
						uvs[i * 2 + 0] = v.ReadF32Le(o + 0);
						uvs[i * 2 + 1] = v.ReadF32Le(o + 4);
					}
					else // Short2N is a genuine signed D3D SHORT2N in [-1,1] (short / 32767), mapped into [0,1] texture
						// space by the standard * 0.5 + 0.5 bias. Decoding it unsigned (/ 65535) is a half-texel shift
						// that looks plausible mid-torso but tears every triangle crossing the centreline u=0 - 130
						// torn triangles unsigned against 0 biased - smearing the texture across the body.
					{
						uvs[i * 2 + 0] = v.ReadI16Le(o + 0) / 32767f * 0.5f + 0.5f;
						uvs[i * 2 + 1] = v.ReadI16Le(o + 2) / 32767f * 0.5f + 0.5f;
					}
				}

				if (bones != null && weights != null)
				{
					var io = biEl!.Value.Offset;
					var wo = bwEl!.Value.Offset;
					for (var k = 0; k < 4; k++)
					{
						bones[i * 4 + k] = v[io + k] >> 1; // The on-wire index is the bone's palette register, bone * 2.
						weights[i * 4 + k] = v[wo + k] / 255f;
					}
				}
			}

			var ibase = vbase + count * stride;
			var tri = (int)blob.ReadU32Le(ibase);
			if (tri < 1 || tri > 2_000_000 || (long)ibase + 4 + (long)tri * 6 > blob.Length)
				return null;

			var indices = new int[tri * 3];
			for (var i = 0; i < indices.Length; i++)
			{
				var idx = blob.ReadU16Le(ibase + 4 + i * 2);
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

		/// <summary>
		///     One drawable sub-mesh: flat XYZ positions, optional XYZ normals and UVs (absent UVs are empty), and
		///     triangle indices.
		/// </summary>
		/// <remarks>
		///     <see cref="BaseColorPng" />, which the caller sets, lets sub-meshes from different models carry their
		///     own diffuse. <see cref="BlendBones" /> and <see cref="BlendWeights" /> appear on skinned bodies and
		///     equipment only, four skin influences per vertex. The blend bones are logical skeleton bone indices: the
		///     on-wire BLENDINDICES are doubled because the engine's matrix-palette register stride is two per bone,
		///     and the reader has already halved them. The blend weights are normalised over 255 and sum to roughly 1.
		/// </remarks>
		public sealed record SubMesh(float[] Positions, float[]? Normals, float[]? Uvs, int[] Indices)
		{
			/// <summary>
			///     How the diffuse's alpha channel renders: Opaque ignores it, as for an alpha specular or gloss map;
			///     Cutout is alpha-test holes such as hair strands, which map to glTF MASK; Blend is semi-transparent,
			///     such as sheer cloth, which maps to glTF BLEND.
			/// </summary>
			public AlphaMode Alpha { get; init; }
			public bool IsSkinned => BlendBones != null && BonePalette != null;
			public byte[]? BaseColorPng { get; init; }

			/// <summary>
			///     An optional tangent-space normal map, as PNG, attached as the glTF material's normalTexture. For the
			///     body it is derived from the MIXD surface-detail/height map, which carries the muscle, ab and navel
			///     relief the flat diffuse omits.
			/// </summary>
			public byte[]? NormalPng { get; init; }
			public float[]? BlendWeights { get; init; }
			public int TriangleCount => Indices.Length / 3;
			public int VertexCount => Positions.Length / 3;

			/// <summary>
			///     Per-vertex skin influences, null on static props: four flat entries per vertex.
			///     <c>BlendBones[i * 4 + k]</c> is a local index into this sub-mesh's <see cref="BonePalette" /> and
			///     <c>BlendWeights[i * 4 + k]</c> is its weight. <c>BonePalette[local]</c> is the skeleton bone name
			///     (see <see cref="SkeletonReader" />): the mesh carries its own compact bone table, the g_BoneN=
			///     entries in the material string, so the consumer maps a palette name to a skeleton bone index to get
			///     the real skin joint.
			/// </summary>
			public int[]? BlendBones { get; init; }
			public string[]? BonePalette { get; init; }
		}
	}
}
