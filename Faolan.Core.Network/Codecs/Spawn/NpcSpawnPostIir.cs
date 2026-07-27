using System;
using System.Diagnostics.CodeAnalysis;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// Typed, mutable view of the NPC post-IIR scaffold header (the stream after the embedded CoreCharacterIIR):
	// [u32 word0][u32 firstRecordLen][u32 recordCount] followed by the per-record body. The header is decoded
	// into typed fields; the record body is kept RAW because it is session runtime state (movement anchors /
	// buff-timer pairs) the simulation re-derives at the tick, not template data (NpcSpawnScaffold above). The
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
