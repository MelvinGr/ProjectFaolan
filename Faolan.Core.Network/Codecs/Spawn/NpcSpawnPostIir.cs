using System;
using System.Diagnostics.CodeAnalysis;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// Typed, mutable view of the NPC post-IIR scaffold header (the stream after the embedded CoreCharacterIIR):
	// [u32 word0][u32 firstRecordLen][u32 recordCount] followed by the per-record body. The header is decoded
	// into typed fields; the record body is kept RAW here, but it is NOT opaque - see the census below. The
	//
	// ⚠️ TWO CORRECTIONS from the corpus-wide walk (#475, SpawnPostIirGrammarTests):
	//
	// 1. This is the SAME grammar as the PLAYER post-IIR. What PlayerSpawnScaffoldCodec writes as the bare
	//    constants `[u32 4][u32 2]` is this exact header pair, and what it writes after them is what this type
	//    calls `Records`. 3,882 captured streams - every NPC minimal form and every create-editor preview - walk
	//    to their exact last byte under one shared reader. The create-editor preview's 40-byte stream and the NPC's
	//    41-byte one differ by exactly ONE leading byte inside the first record, and that byte is the whole reason
	//    they were modelled as different formats.
	//
	// 3. THE BODY IS NOT RUNTIME NOISE. It was documented as "session runtime state the simulation re-derives".
	//    ONE walk consumes 11,399 of the 11,632 captured streams to their exact last byte:
	//
	//        [header][first record][k x 60-byte ScaffoldBuff][u32 count][count x morph entry][player tail]
	//
	//    Everything from the entry table down is the PLAYER's own tail. The single genuine difference is the ENTRY
	//    shape - a player's entries are 14 bytes, an NPC's are the variable-width morph records
	//    NpcSpawnAppearanceCodec already parses. Same slot, different element; and the "single record" forms are
	//    this shape with count == 0 rather than a form of their own.
	//
	//    | walks, 0 buffs | 10,773 |   | walks, 1 buff | 584 |
	//    | walks, 2 buffs |     43 |   | unresolved    | 232 |
	//
	//    The 60-byte records are the PLAYER scaffold's buff records: all 670 carry the same Kind and
	//    EntityClass.Character, and their nine spell ids all resolve in our catalog (Stagger, Hearty Meal, Shrink,
	//    Frozen Hatred, two necromancer pet summons, ...). So "runtime state" was right about the buffs and wrong
	//    about the rest. Gate: NpcPostIirBodyTests. The unresolved 232 are what is genuinely left.
	//
	// 2. The third header word DOES NOT COUNT RECORDS, which is what it used to be called. Streams of identical
	//    length carry 1, 2 and 3 in it, so it cannot be counting anything that occupies bytes; it is a type-stable
	//    enum whose meaning is still unknown, and it is now named `Word2` for its POSITION - the house style for a
	//    word whose place is known and whose meaning is not (cf. Word0 beside it, EntryTableX, FieldA).
	// minimal freshly-spawned form (word0=0, word2=1, the constant 0x3F1-terminated body) is what a
	// from-scratch construction emits; this typed view round-trips both that and the larger runtime-state forms
	// byte-exact, so the from-DB path owns the header grammar in code (Gitea #185).
	public sealed class NpcSpawnPostIir
	{
		public byte[] Records { get; set; } = [];

		// The stream's opening header, shared with the player scaffold (SpawnPostIirHeader).
		public SpawnPostIirHeader Header { get; set; } = new()
		{
			FirstRecordLen = 5,
			Word2 = 1
		};

		public uint FirstRecordLen
		{
			get => Header.FirstRecordLen;
			set => Header.FirstRecordLen = value;
		}

		public uint Word2
		{
			get => Header.Word2;
			set => Header.Word2 = value;
		}

		// Serializes the scaffold to its wire bytes (the 12-byte header + the raw record body).
		public byte[] Encode()
		{
			return new FaolanStream().WriteArray(Header.ToArray()).WriteArray(Records).ToArray();
		}

		// Decodes the post-IIR scaffold from an FA015807 body (the bytes after the embedded CoreCharacterIIR,
		// located via PlayerSpawnReader). Returns false if no CharacterIIR is found or the post-IIR is too short
		// to hold the header.
		public static bool TryDecode(byte[] body, [NotNullWhen(true)] out NpcSpawnPostIir? scaffold)
		{
			scaffold = null;
			if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
				return false;

			var start = read.Offset + read.Length;
			if (SpawnPostIirHeader.Read(body, start) is not { } header)
				return false;

			scaffold = new NpcSpawnPostIir
			{
				Header = header,
				Records = body[(start + header.ByteCount)..]
			};
			return true;
		}
	}
}
