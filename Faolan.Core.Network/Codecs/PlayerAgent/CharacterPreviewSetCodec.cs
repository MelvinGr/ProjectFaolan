using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.PlayerAgent
{
	// `preview.bin` - the single member of the `UpdateCharacterPreview` / `SendCharacterPreview` archive, and the
	// record that draws the posed, geared character on the select screen. Gitea #612 decoded it partially; #788
	// finished it: EVERY list is count-prefixed, so the record is total and can be BUILT, not only read.
	//
	//   [u8  version = 4]
	//   [u32 monsterDataId]                                     RDB 1010201, the body definition
	//   [u32 resourceCount]  resourceCount x PREVIEW RESOURCE (88 bytes)
	//   [u32 meshCount]      meshCount     x PREVIEW MESH     (16 bytes)
	//   [u32 morphCount]     morphCount    x PREVIEW MORPH    (variable)
	//   [u32 0]                                                 a fourth list, empty in every captured payload
	//
	//   PREVIEW RESOURCE  [u32 resourceType][u32 resourceId][u32 attractorType][16 x f32 transform]
	//                     [u32 itemTemplateId][u32 unknown1][u32 unknown2]
	//   PREVIEW MESH      [u32 meshIndexFileId][u32 unknown1][u32 unknown2][u32 slot]
	//   PREVIEW MORPH     [u32 nameLength][name, LATIN1][f32 weight][u32 index]
	//
	// ⚠️ Every integer is LITTLE-endian and so is the float: this is a client-side FILE that happens to be shipped
	// over a big-endian wire, exactly like the `IOz1` archive around it (`ClientSettingsBufferCodec`).
	//
	// ⭐ The record's vocabulary is the client's own `monsterdata` XML (RDB 1010201), which is what names the fields:
	// a body record such as `1091339` = `StygianMaleCharacter` carries `<previewset><previewresource attractortype=""
	// name="" resourceid="" resourcetype=""/></previewset>` - so the header id is the monsterdata, and each 88-byte
	// entry is one `previewresource` plus a transform and an item template. `attractortype` is an attachment POINT
	// (33 = Head, 1 = main hand, 6 = shield, ...), not a flags word.
	//
	// The mesh list is the worn look: every id resolves as RDB **1010030** (a `MeshIndexFile`, whose `<MeshClusters>`
	// names the body part - `ItemNeck`, `ItemBelt`, `ItemLegs`), and its `slot` is the ordinary worn/vanity
	// `CharacterEquipmentSlot` (1, 2, 5, 7, 11, 12, 14..18 and 1002/1007/1011/1014/1018 are all attested). Slot `0`
	// is the odd one out and carries an RDB **1010001** 3D model rather than a mesh index - the hair/base model, which
	// has no equip slot; it is left as a plain `uint` for exactly that reason.
	//
	// The morph list is the create editor's own stream: a resource ref at weight 1.0 (`*texscript_...`,
	// `*beardmesh_...`) or a `MorphTargets` body slider at a small signed weight (`arm`, `arse`, `bosom`, `chest`,
	// `leg`, `stomach`, `thigh`).
	//
	// ⚠️ Building one is NOT the same as retail: retail sends no preview for a freshly created character, and a record
	// the client REJECTS is worse than no preview at all. `Build` exists so a constructed preview can be gated
	// in-client; nothing in the server authors one today.
	public static class CharacterPreviewSetCodec
	{
		// The record's only observed version. Read as an identity check, not as a field - a different version is a
		// different grammar and must fail loudly rather than be decoded as this one.
		private const byte Version = 4;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(CharacterPreviewSet value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteByte(Version).WriteUInt32LittleEndian(value.MonsterDataId);

			s.WriteUInt32LittleEndian((uint)value.Resources.Count);
			foreach (var r in value.Resources)
				Write(s, r);

			s.WriteUInt32LittleEndian((uint)value.Meshes.Count);
			foreach (var m in value.Meshes)
				s.WriteUInt32LittleEndian(m.MeshIndexFileId).WriteUInt32LittleEndian(m.Unknown1).WriteUInt32LittleEndian(m.Unknown2).WriteUInt32LittleEndian(m.Slot);

			s.WriteUInt32LittleEndian((uint)value.Morphs.Count);
			foreach (var m in value.Morphs)
			{
				var name = Encoding.Latin1.GetBytes(m.Name);
				s.WriteUInt32LittleEndian((uint)name.Length).WriteArray(name).WriteFloatLittleEndian(m.Weight).WriteUInt32LittleEndian(m.Index);
			}

			return s.WriteUInt32LittleEndian(0).ToArray();
		}

		public static CharacterPreviewSet Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(CharacterPreviewSetCodec));
			r.ExpectU8(Version, "the preview.bin version byte");
			var monsterDataId = r.ReadU32Le();

			var resources = new List<CharacterPreviewResource>();
			for (var i = r.ReadU32Le(); i > 0; i--)
				resources.Add(ReadResource(ref r));

			var meshes = new List<CharacterPreviewMesh>();
			for (var i = r.ReadU32Le(); i > 0; i--)
				meshes.Add(new CharacterPreviewMesh(r.ReadU32Le(), r.ReadU32Le(), r.ReadU32Le(), r.ReadU32Le()));

			var morphs = new List<CharacterPreviewMorph>();
			for (var i = r.ReadU32Le(); i > 0; i--)
				morphs.Add(new CharacterPreviewMorph(Encoding.Latin1.GetString(r.ReadBytes(checked((int)r.ReadU32Le()))), r.ReadF32Le(), r.ReadU32Le()));

			// The fourth list. It is empty in all four captured payloads, so its element grammar is unknown - reading
			// a non-zero count as "no more fields" would be exactly the partial decode rule 3 exists to catch.
			var trailing = r.ReadU32Le();
			if (trailing != 0)
				throw CodecParseException.Unexpected(nameof(CharacterPreviewSetCodec), r.Position - 4, "an empty fourth list", trailing.ToString());

			r.EnsureAtEnd();

			return new CharacterPreviewSet(monsterDataId, resources, meshes, morphs);
		}

		private static CharacterPreviewResource ReadResource(ref SpanReader r)
		{
			var resourceType = r.ReadU32Le();
			var resourceId = r.ReadU32Le();
			var attractorType = r.ReadU32Le();
			var transform = new Matrix4x4(
				r.ReadF32Le(), r.ReadF32Le(), r.ReadF32Le(), r.ReadF32Le(),
				r.ReadF32Le(), r.ReadF32Le(), r.ReadF32Le(), r.ReadF32Le(),
				r.ReadF32Le(), r.ReadF32Le(), r.ReadF32Le(), r.ReadF32Le(),
				r.ReadF32Le(), r.ReadF32Le(), r.ReadF32Le(), r.ReadF32Le());

			return new CharacterPreviewResource(resourceType, resourceId, attractorType, transform, r.ReadU32Le(), r.ReadU32Le(), r.ReadU32Le());
		}

		private static void Write(FaolanStream s, CharacterPreviewResource value)
		{
			s.WriteUInt32LittleEndian(value.ResourceType).WriteUInt32LittleEndian(value.ResourceId).WriteUInt32LittleEndian(value.AttractorType);

			var t = value.Transform;
			s.WriteFloatLittleEndian(t.M11).WriteFloatLittleEndian(t.M12).WriteFloatLittleEndian(t.M13).WriteFloatLittleEndian(t.M14);
			s.WriteFloatLittleEndian(t.M21).WriteFloatLittleEndian(t.M22).WriteFloatLittleEndian(t.M23).WriteFloatLittleEndian(t.M24);
			s.WriteFloatLittleEndian(t.M31).WriteFloatLittleEndian(t.M32).WriteFloatLittleEndian(t.M33).WriteFloatLittleEndian(t.M34);
			s.WriteFloatLittleEndian(t.M41).WriteFloatLittleEndian(t.M42).WriteFloatLittleEndian(t.M43).WriteFloatLittleEndian(t.M44);

			s.WriteUInt32LittleEndian(value.ItemTemplateId).WriteUInt32LittleEndian(value.Unknown1).WriteUInt32LittleEndian(value.Unknown2);
		}
	}

	// One `preview.bin`: the character's body, what is attached to it, what it wears and how it is shaped.
	public sealed record CharacterPreviewSet(
		uint MonsterDataId,
		IReadOnlyList<CharacterPreviewResource> Resources,
		IReadOnlyList<CharacterPreviewMesh> Meshes,
		IReadOnlyList<CharacterPreviewMorph> Morphs);

	// One `<previewresource>`: a resource hung off an attachment point, with the pose transform and (for a weapon)
	// the item template it came from. `Transform` is row-major with the translation in the LAST row, which is
	// System.Numerics' own convention - most are the identity, and a `1010030` weapon carries a small Z offset.
	// `Unknown1`/`Unknown2` are constant per `ResourceType` across every captured payload - (6, 58) on a 1010030
	// weapon, (0, 35) on the 1010201 head - so nothing in the corpus varies them.
	public sealed record CharacterPreviewResource(
		uint ResourceType,
		uint ResourceId,
		uint AttractorType,
		Matrix4x4 Transform,
		uint ItemTemplateId,
		uint Unknown1,
		uint Unknown2);

	// One worn piece: an RDB 1010030 MeshIndexFile at a `CharacterEquipmentSlot` (slot 0 is the base/hair model and
	// carries a 1010001 3D model instead). `Unknown1` runs 0..3 and is a property of the MESH, not of the slot - the
	// same mesh id carries the same value at different slots and in different payloads - which reads like a material
	// or colour set index; `Unknown2` is zero in every captured entry.
	public sealed record CharacterPreviewMesh(uint MeshIndexFileId, uint Unknown1, uint Unknown2, uint Slot);

	// One create-editor morph callback: a `MorphTargets` name with its weight, plus the editor stream index it
	// arrived at (the same ordering `CharacterBodyResource.Sequence` keeps).
	public sealed record CharacterPreviewMorph(string Name, float Weight, uint Index);
}
