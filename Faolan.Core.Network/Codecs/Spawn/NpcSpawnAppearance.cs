using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Faolan.Core.Network.Codecs
{
	// One entry in a HUMANOID NPC's FA015807 post-IIR APPEARANCE / morph table: a morph id, its float value, and
	// an optional texscript string (e.g. the "*ii=-1;*bl=-1" colour ref carried on the id 0x10D entry). This is
	// the data the minimal post-IIR drops, which renders the NPC bald (Gitea #243). Lifting it into rows lets the
	// post-IIR generate from the DB so NpcSpawn.Data can be retired (#185). Grammar + round-trip oracle:
	// Captures/scripts/decode_npc_morphs.py.
	public sealed class NpcAppearanceMorph
	{
		public float Value { get; set; }
		public string Text { get; set; } = "";
		public uint MorphId { get; set; }
	}

	// Decodes / re-encodes the humanoid NPC post-IIR appearance table byte-exact. The post-IIR (the stream after
	// the embedded CoreCharacterIIR) of a humanoid is its morph table, NOT runtime state - dropping it renders the
	// NPC bald (#243). Animals carry no table (they ship the 41-byte minimal form, NpcSpawnScaffold.BuildMinimalPostIir);
	// TryDecodeAppearance returns false for them and for the larger runtime-state forms, so only the clean
	// appearance shape is lifted. Wire grammar (all u32 big-endian, value a big-endian raw-body float):
	//   [u32 word0=0][u32 firstRecordLen=5][u32 recordCount=2]
	//   record1: 00 00 00 03 F1
	//   [u32 entryCount]
	//   entryCount x [u32 morphId][f32 value][u32 0][u8 0][u8 strLen][strLen bytes]   (strLen 0 on plain entries)
	//   terminator (constant 20 bytes)
	public static class NpcSpawnAppearanceCodec
	{
		// One fixed-shape entry minus its string: [u32 id][f32 value][u32 0][u8 0] = 13 bytes, then [u8 strLen][str].
		private const int EntryFixedLength = 13;
		private const uint FirstRecordLen = 5;
		private const uint HeaderWord0 = 0;
		private const uint RecordCount = 2;

		// record1 = the empty leading list ([u8 0][u32 0x3F1]).
		private static readonly byte[] Record1 = [0x00, 0x00, 0x00, 0x03, 0xF1];

		// The constant tail after the entry list: empty pair list + 0x3F1 markers (same terminators the minimal
		// form ends on). 00 00 03 F1 | 00 00 03 F1 | 00 00 00 00 00 00 00 00 | 00 00 03 F1.
		private static readonly byte[] Terminator =
		[
			0x00, 0x00, 0x03, 0xF1,
			0x00, 0x00, 0x03, 0xF1,
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
			0x00, 0x00, 0x03, 0xF1
		];

		// Builds the post-IIR bytes for a humanoid from its morph rows (the inverse of TryDecodeAppearance).
		public static byte[] BuildPostIir(IReadOnlyList<NpcAppearanceMorph> morphs)
		{
			var stream = new FaolanStream().WriteUInt32(HeaderWord0).WriteUInt32(FirstRecordLen).WriteUInt32(RecordCount).WriteArray(Record1).WriteUInt32(morphs.Count);

			foreach (var morph in morphs)
				stream.WriteUInt32(morph.MorphId).WriteFloat(morph.Value).WriteUInt32(0).WriteByte(0).WriteArrayByteLength(Encoding.ASCII.GetBytes(morph.Text ?? ""));

			return stream.WriteArray(Terminator).ToArray();
		}

		// Decodes the appearance/morph table out of an FA015807 body's post-IIR. Returns false (morphs = empty) if
		// the body has no CharacterIIR, or its post-IIR is not the clean humanoid appearance shape (animal minimal
		// form, or a larger runtime-state form) - those are left to the minimal-scaffold path. The clean shape has the
		// empty 5-byte record1 (Record1) followed immediately by the morph table; use TryDecodeAppearanceLenient to
		// also lift the table when it sits behind a larger RUNTIME record1 (a mid-session humanoid capture).
		public static bool TryDecodeAppearance(byte[] body, out List<NpcAppearanceMorph> morphs)
		{
			morphs = [];
			if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
				return false;

			var post = body.AsSpan(read.Offset + read.Length);
			if (post.Length < 21 || BinaryPrimitives.ReadUInt32BigEndian(post) != HeaderWord0)
				return false;
			if (!post.Slice(12, Record1.Length).SequenceEqual(Record1))
				return false;

			return TryDecodeMorphTable(post, 12 + Record1.Length, out morphs);
		}

		// Lenient variant (#243): decodes the SAME morph table even when it is preceded by a large RUNTIME record1
		// (movement/bounds state accrued mid-session), not the empty 5-byte record1 the clean shape has. RE of the
		// humanoid tail: the table always sits at 12 + firstRecordLen (skip the header + the first record group) and
		// ends at the shared terminator, regardless of recordCount. A FRESH spawn drops the runtime record1, so
		// lifting just this table gives the canonical appearance (the clean-appearance captures of the same type carry
		// an identical table). Returns false for the minimal/no-morph forms and any body whose trailing bytes are not
		// a clean morph table ending exactly at the terminator. A clean-shape body decodes identically here
		// (firstRecordLen == 5, record1 == Record1), so this is a strict superset of TryDecodeAppearance.
		public static bool TryDecodeAppearanceLenient(byte[] body, out List<NpcAppearanceMorph> morphs)
		{
			morphs = [];
			if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
				return false;

			var post = body.AsSpan(read.Offset + read.Length);
			if (post.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(post) != HeaderWord0)
				return false;

			var firstRecordLen = BinaryPrimitives.ReadUInt32BigEndian(post.Slice(4, 4));
			var tableStart = 12L + firstRecordLen;
			if (tableStart + 4 > post.Length)
				return false;

			return TryDecodeMorphTable(post, (int)tableStart, out morphs);
		}

		// Parses the morph table [u32 count][count x entry][terminator] starting at `o`, requiring it to end EXACTLY
		// at the shared Terminator (post end) with every entry in the clean shape (field2/field3 == 0). This is the
		// grammar shared by the strict and lenient decoders; the only difference is where the table starts (right
		// after the empty record1 vs. after a runtime record1).
		private static bool TryDecodeMorphTable(ReadOnlySpan<byte> post, int o, out List<NpcAppearanceMorph> morphs)
		{
			morphs = [];
			var count = BinaryPrimitives.ReadUInt32BigEndian(post.Slice(o, 4));
			o += 4;
			if (count is 0 or > 0x100)
				return false;

			var result = new List<NpcAppearanceMorph>((int)count);
			for (var n = 0; n < count; n++)
			{
				if (o + EntryFixedLength + 1 > post.Length)
					return false;

				var morphId = BinaryPrimitives.ReadUInt32BigEndian(post.Slice(o, 4));
				var value = BinaryPrimitives.ReadSingleBigEndian(post.Slice(o + 4, 4));
				if (BinaryPrimitives.ReadUInt32BigEndian(post.Slice(o + 8, 4)) != 0 || post[o + 12] != 0)
					return false; // not the clean appearance shape (field2/field3 carry runtime state)

				var strLen = post[o + 13];
				o += EntryFixedLength + 1;
				if (o + strLen > post.Length)
					return false;

				var text = strLen == 0
					? ""
					: Encoding.ASCII.GetString(post.Slice(o, strLen));
				o += strLen;
				result.Add(new NpcAppearanceMorph
				{
					MorphId = morphId,
					Value = value,
					Text = text
				});
			}

			// What remains must be exactly the constant terminator - else this is a runtime-bearing form, skip it.
			if (!post[o..].SequenceEqual(Terminator))
				return false;

			morphs = result;
			return true;
		}
	}
}
