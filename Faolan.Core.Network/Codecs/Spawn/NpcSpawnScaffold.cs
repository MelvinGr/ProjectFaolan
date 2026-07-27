using System;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// The NPC SimpleCharFullUpdate (FA015807) post-IIR scaffold - the stream that follows the embedded
	// ProtoCoreCharacterIIR. Unlike the player's post-IIR (PlayerSpawnScaffoldCodec, keyed on the 0x3F1
	// buff-table multiple), the NPC post-IIR is its own grammar (dissected 2026-06-12, workbench
	// tools/capture/dissect_npc_spawn.py over the Strie beach burst, 42 NPCs):
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
		public const uint MinimalHumanoidRecordCount = 2;

		// The record-group count in the minimal (no-morph) form: observed as 1 or 2. A TYPE-STABLE constant (no type
		// ships both) whose meaning isn't decoded - NOT an animal-vs-humanoid split (both values carry people AND
		// creatures; the names below are historical, from the beach-burst sample where the animals happened to be 1 and
		// the no-morph humanoids 2). BuildMinimalPostIir(rc) reproduces either; the type's value is stored in
		// NpcStatCore.MinimalPostIirRecordCount (#243).
		public const uint MinimalRecordCount = 1;

		// firstRecordLen = len(post) - 36 (the header's self-length of the first record group).
		private const int RecordLenBias = 36;

		// word0 is constant 0 across the corpus (the player spawn has 2 in this slot).
		private const uint HeaderWord0 = 0;

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

		// The fresh post-IIR recordCount for a NO-MORPH spawn (#243): the type-stable header recordCount word (1 or 2).
		// Reads it off the exact 41-byte minimal form AND a larger RUNTIME-BEARING no-morph capture - the recordCount
		// word is stable per type across minimal and runtime captures
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
			if (post.Length < 12 || post.ReadU32Be(0) != HeaderWord0)
				return false;

			recordCount = post.ReadU32Be(8);
			return recordCount is MinimalRecordCount or MinimalHumanoidRecordCount;
		}
	}
}
