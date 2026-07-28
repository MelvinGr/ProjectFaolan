using System;
using System.Collections.Generic;
using System.Text;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// The MORPH TABLE that fills a humanoid NPC's entry slot in the FA015807 post-IIR, and the classification of
	// which captured bodies carry one. Dropping the table renders the NPC bald (#243); animals carry none (their
	// slot is empty - the 41-byte minimal form, NpcSpawnScaffold.BuildMinimalPostIir).
	//
	// One entry: [u32 morphId][f32 value][u32 word2][u8 flag][u8 strLen][strLen bytes] - the value a big-endian
	// raw-body float, the string the "*ii=-1;*bl=-1" texscript colour ref carried on the id 0x10D entry. word2 and
	// flag are 0 on a clean appearance and carry runtime state mid-session.
	//
	// The stream AROUND the table is not this codec's - it is the same one the player's spawn uses
	// (SpawnPostIirCommon / NpcSpawnPostIir), which is what the two decoders below now read rather than computing
	// the table's offset from the header.
	public static class NpcSpawnAppearanceCodec
	{
		// One fixed-shape entry minus its string: [u32 id][f32 value][u32 0][u8 0] = 13 bytes, then [u8 strLen][str].
		private const int EntryFixedLength = 13;
		private const uint Word2 = 2; // the post-IIR header's third word - not a record count (#475)

		// Builds the post-IIR bytes for a humanoid from its morph rows (the inverse of TryDecodeAppearance). The
		// stream around the table is the shared one, so this is the fresh NPC scaffold with the table dropped in -
		// the same construction NpcSpawnScaffold.BuildMinimalPostIir makes with no morphs at all.
		public static byte[] BuildPostIir(IReadOnlyList<NpcAppearanceMorph> morphs)
		{
			var post = new NpcSpawnPostIir
			{
				Word2 = Word2
			};
			post.Morphs.AddRange(morphs);
			return post.Encode();
		}

		// Writes the entry slot's NPC element: [u32 count][count x entry]. The two words that follow it belong to
		// the stream, not the table - see NpcSpawnPostIir.
		public static void WriteMorphTable(FaolanStream stream, IReadOnlyList<NpcAppearanceMorph> morphs)
		{
			stream.WriteUInt32(morphs.Count);
			foreach (var morph in morphs)
				stream.WriteUInt32(morph.MorphId).WriteFloat(morph.Value).WriteUInt32(morph.Word2).WriteByte(morph.Flag)
					.WriteArrayByteLength(Encoding.Latin1.GetBytes(morph.Text ?? ""));
		}

		// Reads that same slot at `i`, advancing it. Unlike the appearance decoders below this one CARRIES an entry's
		// Word2/Flag instead of requiring them to be zero: a mid-session capture puts runtime state there, and a
		// reader that rejected it would lose the rest of the stream with it. Returns false only when the table does
		// not fit - it is the walk's landing on the stream's last byte that says whether this was a morph table at
		// all, not a per-field guess.
		public static bool TryReadMorphTable(byte[] post, ref int i, List<NpcAppearanceMorph> morphs)
		{
			if (i + 4 > post.Length)
				return false;

			var count = post.ReadU32Be(i);
			i += 4;
			if (count > 0x100)
				return false;

			for (var n = 0; n < count; n++)
			{
				if (i + EntryFixedLength + 1 > post.Length)
					return false;

				var strLen = post[i + EntryFixedLength];
				if (i + EntryFixedLength + 1 + strLen > post.Length)
					return false;

				morphs.Add(new NpcAppearanceMorph
				{
					MorphId = post.ReadU32Be(i),
					Value = post.ReadF32Be(i + 4),
					Word2 = post.ReadU32Be(i + 8),
					Flag = post[i + 12],
					Text = strLen == 0 ? "" : Encoding.Latin1.GetString(post, i + EntryFixedLength + 1, strLen)
				});
				i += EntryFixedLength + 1 + strLen;
			}

			return true;
		}

		// Decodes the appearance/morph table out of an FA015807 body's post-IIR. Returns false (morphs = empty) when
		// the body has no CharacterIIR, or its post-IIR is not an appearance stream at all: an animal's minimal
		// no-morph form, a player's, or a mid-session capture whose entries carry runtime state. Those are left to
		// the minimal-scaffold path. STRICT additionally requires the stream to be FRESH - a first record with
		// nothing in front of the buff list's framing word, and no buffs in that list.
		public static bool TryDecodeAppearance(byte[] body, out List<NpcAppearanceMorph> morphs)
		{
			morphs = [];
			if (!TryDecodeAppearanceLenient(body, out var table))
				return false;
			if (!NpcSpawnPostIir.TryDecode(body, out var post))
				return false;
			if (post.Common.BuffRecords.Count > 0 || !post.Common.FirstRecordPrefix.AsSpan().SequenceEqual(NpcSpawnPostIir.FreshFirstRecordPrefix))
				return false;

			morphs = table;
			return true;
		}

		// Lenient variant (#243): lifts the SAME morph table from a mid-session capture, where the stream also carries
		// runtime state - a large first record (movement/bounds state accrued in play) and the character's active
		// buffs. A FRESH spawn drops both, so the table alone is the canonical appearance, and the clean-appearance
		// captures of the same type carry an identical one.
		//
		// ⚠️ It used to find the table by ARITHMETIC - at 12 + firstRecordLen, i.e. straight after the first record -
		// which is where the table sits only when the character has NO active buffs, because each buff pushes it 60
		// bytes further. A buffed humanoid was therefore classified no-morph and would spawn BALD (#243's own failure
		// mode). Reading the stream instead of computing an offset lifts 269 more captured bodies and loses none;
		// nothing else about the classification changed, which is what the corpus census asserts.
		public static bool TryDecodeAppearanceLenient(byte[] body, out List<NpcAppearanceMorph> morphs)
		{
			morphs = [];
			if (!NpcSpawnPostIir.TryDecode(body, out var post))
				return false;

			// An empty table is the minimal (no-morph) form, not an appearance; an entry carrying runtime state, or a
			// non-empty closing list, means this stream is more than the appearance and is not what the from-DB build
			// reproduces. Both keep this decoder NARROWER than the reader it now runs on.
			if (post.Morphs.Count == 0 || post.Morphs.Exists(m => m.Word2 != 0 || m.Flag != 0))
				return false;
			if (post.EntryTableX != ListFrame.Empty || post.EntryTableMarker != ListFrame.Empty)
				return false;
			if (post.Common.EndPairs.Count > 0 || post.Common.EndPairs2.Count > 0 || post.Common.EndPairs3.Count > 0)
				return false;

			morphs = post.Morphs;
			return true;
		}

	}

	// One entry in a HUMANOID NPC's FA015807 post-IIR APPEARANCE / morph table: a morph id, its float value, and
	// an optional texscript string (e.g. the "*ii=-1;*bl=-1" colour ref carried on the id 0x10D entry). This is
	// the data the minimal post-IIR drops, which renders the NPC bald (Gitea #243). Lifting it into rows lets the
	// post-IIR generate from the DB so NpcSpawn.Data can be retired (#185). Grammar + round-trip oracle:
	// tools/capture/decode_npc_morphs.py.
	public sealed class NpcAppearanceMorph
	{
		public uint MorphId { get; set; }
		public float Value { get; set; }

		// Zero on every CLEAN appearance entry, which is what the two decoders below require to classify a body as
		// an appearance-bearing type. A mid-session capture carries runtime state here, so the general reader
		// (NpcSpawnPostIir) carries these instead of asserting them and those streams round-trip byte-exact.
		public uint Word2 { get; set; }
		public byte Flag { get; set; }
		public string Text { get; set; } = "";
	}
}
