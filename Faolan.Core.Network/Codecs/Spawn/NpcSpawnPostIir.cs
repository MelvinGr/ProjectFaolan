using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// The NPC side of the FA015807 post-IIR stream - the bytes after the embedded CoreCharacterIIR (Gitea #475).
	//
	// It is the SAME grammar as the player's, and the entry slot in the middle is the only place the two differ:
	//
	//   [SpawnPostIirCommon prologue]      the header, the first record and the active buffs - shared
	//   [u32 count][count x morph entry]   THE ENTRY SLOT: the NPC's is the morph table (NpcSpawnAppearanceCodec
	//                                      owns the element; a player's slot holds 14-byte entries + an ID list)
	//   [u32][u32]                         two words, ForCount(0) on all 11,394 captured NPC-shaped streams
	//   [SpawnPostIirCommon tail]          the three closing pair lists - shared
	//
	// ⚠️ TWO CORRECTIONS, both of which this type used to state the other way round:
	//
	// 1. THE BODY IS NOT RUNTIME NOISE. Everything after the header was carried as an opaque `byte[] Records`
	//    documented as "session runtime state the simulation re-derives". The buffs are runtime state; the rest is
	//    the player's own grammar, and the create-editor preview's 40-byte stream differs from the NPC's 41-byte one
	//    by exactly ONE leading byte inside the first record - which is the whole reason they were modelled as two
	//    formats. The one genuinely undecoded region left is that first record's PREFIX
	//    (SpawnPostIirCommon.FirstRecordPrefix): empty on a player, one 0x00 byte on a fresh NPC, and 20..1,273
	//    bytes of accrued state on a mid-session capture. A from-scratch build emits the fresh form.
	//
	// 2. The third header word DOES NOT COUNT RECORDS, which is what it used to be called. Streams of identical
	//    length carry 1, 2 and 3 in it, so it cannot be counting anything that occupies bytes; it is a type-stable
	//    enum whose meaning is still unknown, and it is named `Word2` for its POSITION - the house style for a word
	//    whose place is known and whose meaning is not (cf. EntryTableX, FieldA).
	//
	// Gates: SpawnPostIirGrammarTests walks every captured stream to its exact last byte; NpcSpawnPreIirTests and
	// NpcSpawnFromScratchTests hold the decode/encode and the from-scratch builds byte-exact against captures.
	public sealed class NpcSpawnPostIir
	{
		// The fresh (no runtime state) first record: one 0x00 byte in front of the buff list's framing word. The
		// player's record is the framing word alone, which is why its length is 4 where this one's is 5.
		public static readonly byte[] FreshFirstRecordPrefix = [0x00];

		// The stream minus its entry slot - shared with the player verbatim.
		public SpawnPostIirCommon Common { get; set; } = new()
		{
			Header = new SpawnPostIirHeader
			{
				FirstRecordLen = (uint)FreshFirstRecordPrefix.Length + 4,
				Word2 = NpcSpawnScaffold.MinimalWord2
			},
			FirstRecordPrefix = FreshFirstRecordPrefix
		};

		// The entry slot: a humanoid's morph table, empty on an animal and on the minimal fresh form. Dropping it
		// renders the NPC bald (#243), which is why the minimal form is not a safe default for a humanoid.
		public List<NpcAppearanceMorph> Morphs { get; } = [];

		// The two words between the morph table and the closing pair lists. ForCount(0) on every captured NPC-shaped
		// stream - which per ListFrame's own warning most likely makes them two EMPTY LISTS rather than constants, so
		// they are carried per stream instead of asserted. The first sits where the player's EntryTableX does.
		public uint EntryTableX { get; set; } = ListFrame.Empty;
		public uint EntryTableMarker { get; set; } = ListFrame.Empty;

		public uint FirstRecordLen
		{
			get => Common.Header.FirstRecordLen;
			set => Common.Header.FirstRecordLen = value;
		}

		public SpawnPostIirHeader Header => Common.Header;

		public uint Word2
		{
			get => Common.Header.Word2;
			set => Common.Header.Word2 = value;
		}

		// Serializes the stream to its wire bytes.
		public byte[] Encode()
		{
			var stream = new FaolanStream();
			Common.WritePrologue(stream);
			NpcSpawnAppearanceCodec.WriteMorphTable(stream, Morphs);
			stream.WriteUInt32(EntryTableX).WriteUInt32(EntryTableMarker);
			Common.WriteTail(stream);
			return stream.ToArray();
		}

		// Decodes the post-IIR out of an FA015807 body (locating the embedded CharacterIIR the way PlayerSpawnReader
		// does). Returns false when the body carries no CharacterIIR, or when what follows it is not this shape -
		// a PLAYER body is the ordinary case of the latter, since its entry slot holds a different element.
		public static bool TryDecode(byte[] body, [NotNullWhen(true)] out NpcSpawnPostIir? scaffold)
		{
			scaffold = null;
			if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
				return false;

			return TryDecodePost(body[(read.Offset + read.Length)..], out scaffold);
		}

		// As TryDecode, for a stream already sliced out of its body. The walk must consume `post` to its LAST byte -
		// that end-exactness is what proves the entry slot was read as the right element, since a player's slot and
		// an NPC's cannot both land on the same endpoint (they only agree when the slot is EMPTY, where the two
		// readings are the same twelve bytes).
		public static bool TryDecodePost(byte[] post, [NotNullWhen(true)] out NpcSpawnPostIir? scaffold)
		{
			scaffold = null;
			try
			{
				var decoded = new NpcSpawnPostIir
				{
					Common = SpawnPostIirCommon.ReadPrologue(post, 0, out var i)
				};
				if (!NpcSpawnAppearanceCodec.TryReadMorphTable(post, ref i, decoded.Morphs))
					return false;
				if (i + 8 > post.Length)
					return false;

				decoded.EntryTableX = post.ReadU32Be(i);
				decoded.EntryTableMarker = post.ReadU32Be(i + 4);
				decoded.Common.ReadTail(post, i + 8);
				scaffold = decoded;
				return true;
			}
			catch (FormatException)
			{
				return false;
			}
		}
	}
}
