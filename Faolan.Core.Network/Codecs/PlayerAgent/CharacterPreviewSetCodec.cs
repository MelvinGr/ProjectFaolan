using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.PlayerAgent
{
	/// <summary>
	///     <c>preview.bin</c> - the single member of the <c>UpdateCharacterPreview</c> / <c>SendCharacterPreview</c>
	///     archive, and the record that draws the posed, geared character on the select screen.
	/// </summary>
	/// <remarks>
	///     Every list is count-prefixed, so the record is total and can be built rather than only read:
	///     <code>
	///     [u8  version = 4]
	///     [u32 monsterDataId]                                     RDB 1010201, the body definition
	///     [u32 resourceCount]  resourceCount x PREVIEW RESOURCE (88 bytes)
	///     [u32 meshCount]      meshCount     x PREVIEW MESH     (16 bytes)
	///     [u32 morphCount]     morphCount    x PREVIEW MORPH    (variable)
	///     [u32 0]                                                 a fourth list, empty in every captured payload
	///
	///     PREVIEW RESOURCE  [u32 resourceType][u32 resourceId][u32 attractorType][16 x f32 transform]
	///                       [u32 itemTemplateId][u32 unknown1][u32 unknown2]
	///     PREVIEW MESH      [u32 meshIndexFileId][u32 unknown1][u32 unknown2][u32 slot]
	///     PREVIEW MORPH     [u32 nameLength][name, Latin1][f32 weight][u32 index]
	///     </code>
	///     <para>
	///         Every integer is little-endian and so is the float: this is a client-side file that happens to be shipped
	///         over a big-endian wire, exactly like the <c>IOz1</c> archive around it
	///         (<see cref="ClientSettingsBufferCodec" />).
	///     </para>
	///     <para>
	///         The record's vocabulary is the client's own <c>monsterdata</c> XML (RDB 1010201), which is what names the
	///         fields: a body record such as <c>1091339</c> = <c>StygianMaleCharacter</c> carries
	///         <c>&lt;previewset&gt;&lt;previewresource attractortype="" name="" resourceid=""
	///         resourcetype=""/&gt;&lt;/previewset&gt;</c>, so the header id is the monsterdata and each 88-byte entry is
	///         one <c>previewresource</c> plus a transform and an item template. <c>attractortype</c> is an attachment
	///         point (33 = head, 1 = main hand, 6 = shield, …), not a flags word.
	///     </para>
	///     <para>
	///         The mesh list is the worn look: every id resolves as RDB 1010030, a <c>MeshIndexFile</c> whose
	///         <c>&lt;MeshClusters&gt;</c> names the body part (<c>ItemNeck</c>, <c>ItemBelt</c>, <c>ItemLegs</c>), and
	///         its <c>slot</c> is the ordinary worn/vanity <c>CharacterEquipmentSlot</c> (1, 2, 5, 7, 11, 12, 14..18 and
	///         1002/1007/1011/1014/1018 are all attested). Slot <c>0</c> is the odd one out and carries an RDB 1010001 3D
	///         model rather than a mesh index - the hair or base model, which has no equip slot, which is why it is left
	///         as a plain <c>uint</c>.
	///     </para>
	///     <para>
	///         The morph list is the create editor's own stream: a resource ref at weight 1.0
	///         (<c>*texscript_...</c>, <c>*beardmesh_...</c>) or a <c>MorphTargets</c> body slider at a small signed
	///         weight (<c>arm</c>, <c>arse</c>, <c>bosom</c>, <c>chest</c>, <c>leg</c>, <c>stomach</c>, <c>thigh</c>).
	///     </para>
	///     <para>
	///         Building one is not the same as retail: retail sends no preview for a freshly created character, and a
	///         record the client rejects is worse than no preview at all. <c>Build</c> exists so a constructed preview can
	///         be gated in-client; nothing in the server authors one.
	///     </para>
	/// </remarks>
	public static class CharacterPreviewSetCodec
	{
		// The record's only observed version. Read as an identity check rather than as a field: a different version is a
		// different grammar and must fail loudly rather than be decoded as this one.
		private const byte Version = 4;

		// Canonical: the only method here that writes bytes.
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
				s.WriteUInt32LittleEndian(m.MeshIndexFileId).WriteUInt32LittleEndian(m.Layer).WriteUInt32LittleEndian(m.Unknown2).WriteUInt32LittleEndian(m.Slot);

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

			// The fourth list. It is empty in all four captured payloads, so its element grammar is unknown; reading a
			// non-zero count as "no more fields" would be a partial decode.
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

	/// <summary>
	///     One <c>preview.bin</c>: the character's body, what is attached to it, what it wears and how it is shaped.
	/// </summary>
	public sealed record CharacterPreviewSet(
		uint MonsterDataId,
		IReadOnlyList<CharacterPreviewResource> Resources,
		IReadOnlyList<CharacterPreviewMesh> Meshes,
		IReadOnlyList<CharacterPreviewMorph> Morphs);

	/// <summary>
	///     One <c>&lt;previewresource&gt;</c>: a resource hung off an attachment point, with the pose transform and, for a
	///     weapon, the item template it came from.
	/// </summary>
	/// <remarks>
	///     <c>Transform</c> is row-major with the translation in the last row, which is System.Numerics' own convention;
	///     most are the identity, and a 1010030 weapon carries a small Z offset. <c>Unknown1</c> and <c>Unknown2</c> are
	///     constant per <c>ResourceType</c> across every captured payload - (6, 58) on a 1010030 weapon, (0, 35) on the
	///     1010201 head - so nothing in the corpus varies them. The client's own record writer (<c>FUN_00fe9f60</c>) says
	///     where they come from: after the matrix it writes attachment <c>+0x80</c> (this record's <c>ItemTemplateId</c>),
	///     attachment <c>+0x84</c> (<c>Unknown1</c>) and the attached visual's <c>+0x4c</c> (<c>Unknown2</c>), so
	///     <c>Unknown2</c> is a property of the visual object rather than of the attachment, which fits a per-class
	///     constant (35 on the head's <c>VisualCATMesh_c</c>, 58 on the weapon's <c>VisualMesh_t</c>). Neither has a named
	///     reader. [V0]
	/// </remarks>
	public sealed record CharacterPreviewResource(
		uint ResourceType,
		uint ResourceId,
		uint AttractorType,
		Matrix4x4 Transform,
		uint ItemTemplateId,
		uint Unknown1,
		uint Unknown2);

	/// <summary>
	///     One worn piece: an RDB 1010030 MeshIndexFile at a <c>CharacterEquipmentSlot</c>. Slot 0 is the base or hair
	///     model and carries a 1010001 3D model instead.
	/// </summary>
	/// <remarks>
	///     <c>Layer</c> runs 0..3 and is a property of the mesh rather than of the slot - the same mesh id carries the same
	///     value at different slots and in different payloads - and <c>Unknown2</c> is zero in every captured entry.
	///     <para>
	///         Each entry is a 28-byte struct in the visual's mesh vector, of which words 0, 1, 5 and 6 go on the wire,
	///         and the preview loader hands every entry to <c>VisualCATMesh_c::AddSkinnedMesh(this, nIdxOrFmeId, layer,
	///         unknown2, slot, word2)</c> (<c>FUN_00fed5d0</c>, named by its own log line). There the layer is consumed as
	///         a float, and <c>MeshInstance::SetMesh</c> (<c>FUN_00f67b90</c>) stores it at instance <c>+0xd0</c> when it
	///         lies in [0, 6), setting bit 0x20 of <c>+0xe0</c> when it is non-zero; <c>Unknown2</c> is stored at instance
	///         <c>+0x1c</c> and defaults to the visual's own <c>+0x10</c> when 0, exactly as the slot defaults to
	///         <c>+0x14</c>.
	///     </para>
	///     <para>
	///         <c>Layer</c> is named by the client's own action pair. Two GameAction kinds attach a skinned mesh to a
	///         character and call the same function: <c>e_Type_GC_GameAction_AddSkinnedMesh</c> (53240,
	///         <c>FUN_008ac440</c>) has one key, <c>GameAction_MeshID</c>, and passes 0 for this argument, while
	///         <c>e_Type_GC_GameAction_AddLayeredSkinnedMesh</c> (53003, <c>FUN_008ac1f0</c>) has two keys,
	///         <c>GameAction_MeshID</c> and <c>GameAction_Value</c>, and passes the Value stat for it. Every other
	///         argument is identical between the two (docs/re/gameaction-keylists.tsv carries the key lists,
	///         gameaction-executors.tsv the executor pair), so this is the one word separating a skinned mesh from a
	///         layered one. A third caller (<c>FUN_00fecf20</c>) passes 0xffffffff, which as a float fails the range test
	///         and stores nothing: the "no layer" sentinel.
	///     </para>
	///     <para>
	///         <c>Unknown2</c> is not named by the same route, because both executors pass the same expression for it. It
	///         stays an optional override of a per-visual default, zero in every captured entry. [V0]
	///     </para>
	/// </remarks>
	public sealed record CharacterPreviewMesh(uint MeshIndexFileId, uint Layer, uint Unknown2, uint Slot);

	/// <summary>
	///     One create-editor morph callback: a <c>MorphTargets</c> name with its weight, plus the editor stream index it
	///     arrived at (the same ordering <c>CharacterBodyResource.Sequence</c> keeps).
	/// </summary>
	public sealed record CharacterPreviewMorph(string Name, float Weight, uint Index);
}
