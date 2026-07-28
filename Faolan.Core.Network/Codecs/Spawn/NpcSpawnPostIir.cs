using System;
using System.Diagnostics.CodeAnalysis;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// Typed, mutable view of the NPC post-IIR scaffold header (the stream after the embedded CoreCharacterIIR):
	// [u32 word0][u32 firstRecordLen][u32 recordCount] followed by the per-record body. The header is decoded
	// into typed fields; the record body is kept RAW because it is session runtime state (movement anchors /
	// buff-timer pairs) the simulation re-derives at the tick, not template data (NpcSpawnScaffold above). The
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
	// 2. `RecordCount` DOES NOT COUNT RECORDS. Streams of identical length carry 1, 2 and 3 in it, so it cannot be
	//    counting anything that occupies bytes; it is a type-stable enum whose meaning is still unknown. The name is
	//    kept for now only because the persisted column NpcStatCore.MinimalPostIirRecordCount shares it and renaming
	//    that needs a migration - it is wrong, and this note is here so it does not get believed.
	// minimal freshly-spawned form (word0=0, recordCount=1, the constant 0x3F1-terminated body) is what a
	// from-scratch construction emits; this typed view round-trips both that and the larger runtime-state forms
	// byte-exact, so the from-DB path owns the header grammar in code (Gitea #185).
	public sealed class NpcSpawnPostIir
	{
		// The 12-byte header ([u32 word0][u32 firstRecordLen][u32 recordCount]).
		private const int HeaderLength = 12;
		public byte[] Records { get; set; } = [];
		public uint FirstRecordLen { get; set; }
		public uint RecordCount { get; set; } = 1;

		public uint Word0 { get; set; }

		// Serializes the scaffold to its wire bytes (the 12-byte header + the raw record body).
		public byte[] Encode()
		{
			return new FaolanStream().WriteUInt32(Word0).WriteUInt32(FirstRecordLen).WriteUInt32(RecordCount).WriteArray(Records).ToArray();
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
			if (start + HeaderLength > body.Length)
				return false;

			scaffold = new NpcSpawnPostIir
			{
				Word0 = body.ReadU32Be(start),
				FirstRecordLen = body.ReadU32Be(start + 4),
				RecordCount = body.ReadU32Be(start + 8),
				Records = body[(start + HeaderLength)..]
			};
			return true;
		}
	}
}
