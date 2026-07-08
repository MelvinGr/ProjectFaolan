using System;
using System.Buffers.Binary;

namespace Faolan.Core.Network.Codecs
{
	// The NPC SimpleCharFullUpdate (FA015807) post-IIR scaffold - the stream that follows the embedded
	// ProtoCoreCharacterIIR. Unlike the player's post-IIR (PlayerSpawnScaffoldCodec, keyed on the 0x3F1
	// buff-table multiple), the NPC post-IIR is its own grammar (dissected 2026-06-12, workbench
	// Captures/scripts/dissect_npc_spawn.py over the Strie beach burst, 42 NPCs):
	//
	//   header:  [u32 0][u32 firstRecordLen = len(post) - 36][u32 recordCount]
	//            - word0 is 0 on ALL 42 (the player has 2 here).
	//            - word1 == len(post) - 36 on the single-record blocks (18/42); the larger blocks concatenate
	//              additional RUNTIME-STATE records (current movement / waypoint vec3s, active buff/timer pairs)
	//              past the first, so word1 covers only the first record group, not the whole stream.
	//            - word2 (recordCount) is 1 on 41/42.
	//   body:    per-record groups (movement anchors as big-endian vec3 float triples, buff/timer pairs), all
	//            terminated by the shared 0x3F1-marked empty pair lists.
	//
	// A FRESHLY-SPAWNED NO-MORPH NPC has the MINIMAL form: a single constant 41-byte block (see BuildMinimalPostIir),
	// with a type-stable recordCount (1 or 2, #243). APPEARANCE-bearing NPCs instead carry a morph table - the
	// "*key=val" texscript string (e.g. "*ii=-1;*bl=-1") followed by [u32 morphId][float32] pairs, decoded on Casilda
	// (Gitea #243) and stored as morph rows. Emitting the minimal form for such an NPC DROPS that table and the client
	// renders it BALD ([[npc_spawn_minimal_postiir_breaks_humanoids]]) - so the from-scratch build (NpcSpawnBuilder.
	// BuildBody) emits NpcSpawnAppearanceCodec.BuildPostIir(morphs) when the type has morph rows, else this minimal
	// form with the type's recordCount. Both are CONSTRUCTED from typed storage; the captured post-IIR is never
	// replayed. The codecs below are the validated from-scratch foundation, byte-exact here.
	public static class NpcSpawnScaffold
	{
		// The minimal post-IIR length, in bytes (the constant freshly-spawned form).
		public const int MinimalPostIirLength = 41;

		// firstRecordLen = len(post) - 36 (the header's self-length of the first record group).
		private const int RecordLenBias = 36;

		// word0 is constant 0 across the corpus (the player spawn has 2 in this slot).
		private const uint HeaderWord0 = 0;

		// The record-group count in the minimal (no-morph) form: observed as 1 or 2. A TYPE-STABLE constant (no type
		// ships both) whose meaning isn't decoded - NOT an animal-vs-humanoid split (both values carry people AND
		// creatures; the names below are historical, from the beach-burst sample where the animals happened to be 1 and
		// the no-morph humanoids 2). BuildMinimalPostIir(rc) reproduces either; the type's value is stored in
		// NpcStatCore.MinimalPostIirRecordCount (#243).
		public const uint MinimalRecordCount = 1;
		public const uint MinimalHumanoidRecordCount = 2;

		// The minimal record body + the shared 0x3F1 terminators (everything after the 12-byte header). Constant
		// across all 23 NPCs that carry the minimal form (verified single distinct value, dissect_npc_spawn.py).
		private static readonly byte[] MinimalBody =
		[
			0x00, 0x00, 0x00, 0x03, 0xF1, // [u8 0][u32 0x3F1] - empty ID-list terminator
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xF1, // empty pair list, 0x3F1 terminator
			0x00, 0x00, 0x03, 0xF1, // 0x3F1 marker
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xF1 // end pairs, final 0x3F1 marker
		];

		// Builds the minimal post-IIR scaffold for a freshly-spawned NPC: [u32 0][u32 len-36][u32 recordCount] + the
		// constant terminator body. recordCount defaults to 1 (the animal form, byte-identical to the 23 minimal-form
		// NPCs in the beach burst); pass MinimalHumanoidRecordCount (2) for a no-morph humanoid (#243 tail).
		public static byte[] BuildMinimalPostIir(uint recordCount = MinimalRecordCount)
		{
			return new FaolanStream().WriteUInt32(HeaderWord0).WriteUInt32(MinimalPostIirLength - RecordLenBias).WriteUInt32(recordCount).WriteArray(MinimalBody).ToArray();
		}

		// True iff `body`'s post-IIR is EXACTLY the minimal no-morph form (the constant 41-byte body) - the fresh
		// shape a no-morph NPC ships. `recordCount` is the type-stable 1 or 2 (see MinimalRecordCount); a from-scratch
		// build re-emits BuildMinimalPostIir(recordCount) for such a spawn (#243). Returns false for appearance-bearing
		// post-IIRs (they carry a morph table) and runtime-bearing forms (larger than the minimal body) - those aren't
		// the minimal shape, so their fresh recordCount isn't derivable from this capture.
		public static bool TryReadMinimalRecordCount(byte[] body, out uint recordCount)
		{
			recordCount = 0;
			if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
				return false;

			var post = body.AsSpan(read.Offset + read.Length);
			if (post.Length != MinimalPostIirLength)
				return false;

			var rc = BinaryPrimitives.ReadUInt32BigEndian(post.Slice(8, 4));
			if (rc is not (MinimalRecordCount or MinimalHumanoidRecordCount) || !post.SequenceEqual(BuildMinimalPostIir(rc)))
				return false;

			recordCount = rc;
			return true;
		}

		// The fresh post-IIR recordCount for a NO-MORPH spawn (#243): the type-stable header recordCount word (1 or 2).
		// Unlike TryReadMinimalRecordCount (which only accepts the exact 41-byte minimal form), this ALSO reads it off a
		// RUNTIME-BEARING no-morph capture - the recordCount word is stable per type across minimal and runtime captures
		// (validated: 0/107 no-morph types carry >1 distinct value), and the runtime records are dropped on a fresh
		// spawn, so BuildMinimalPostIir(recordCount) is the canonical fresh form. Returns false when the body carries a
		// liftable morph table (an APPEARANCE type - it builds from morph rows, not the minimal form), when word0 isn't
		// the constant 0, or when no CharacterIIR is present. This is what lets the ~24 runtime-bearing no-morph types
		// (whose type has no minimal-form capture) derive their recordCount without a guess.
		public static bool TryReadNoMorphRecordCount(byte[] body, out uint recordCount)
		{
			recordCount = 0;
			if (NpcSpawnAppearanceCodec.TryDecodeAppearance(body, out _) || NpcSpawnAppearanceCodec.TryDecodeAppearanceLenient(body, out _))
				return false; // an appearance type - builds from morph rows, not the minimal form
			if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
				return false;

			var post = body.AsSpan(read.Offset + read.Length);
			if (post.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(post) != HeaderWord0)
				return false;

			recordCount = BinaryPrimitives.ReadUInt32BigEndian(post.Slice(8, 4));
			return recordCount is MinimalRecordCount or MinimalHumanoidRecordCount;
		}

	}

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
		public static bool TryDecode(byte[] body, out NpcSpawnPostIir scaffold)
		{
			scaffold = null;
			if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
				return false;

			var start = read.Offset + read.Length;
			if (start + HeaderLength > body.Length)
				return false;

			scaffold = new NpcSpawnPostIir
			{
				Word0 = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(start, 4)),
				FirstRecordLen = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(start + 4, 4)),
				RecordCount = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(start + 8, 4)),
				Records = body[(start + HeaderLength)..]
			};
			return true;
		}
	}
}
