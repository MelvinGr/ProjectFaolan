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
		// The minimal post-IIR length, in bytes, for a freshly-spawned WORLD npc. Not the shortest form on the wire:
		// the 21 create-editor preview spawns carry a 40-byte one, identical except that it lacks the leading rank
		// byte before the first list word (and so has firstRecordLen 4 rather than 5). Censused over all 11,632
		// captured post-IIR streams (#475).
		public const int MinimalPostIirLength = 41;
		public const uint MinimalHumanoidWord2 = 2;

		// The third observed value (23 captured streams). Named for its value like its two siblings, because what it
		// SELECTS is unknown - see NpcSpawnPostIir for why this word is not a record count.
		public const uint MinimalThirdWord2 = 3;

		// The record-group count in the minimal (no-morph) form. A TYPE-STABLE constant whose meaning isn't decoded -
		// NOT an animal-vs-humanoid split (both values carry people AND creatures; the names here are historical, from
		// the beach-burst sample where the animals happened to be 1 and the no-morph humanoids 2).
		// ⚠️ It is 1, 2 or **3**, not "1 or 2" as this said: a corpus-wide census of the 3,272 captured 41-byte forms
		// gives 1 x757, 2 x2492 and 3 x23 (#475). Nothing here rejects a 3 - BuildMinimalPostIir(rc) takes the count
		// and the type's own value is stored in NpcStatCore.MinimalPostIirWord2 (#243) - but the claim that only
		// two values exist was wrong, and it is the kind of claim a later "simplification" would have leaned on.
		public const uint MinimalWord2 = 1;

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

		// Builds the minimal post-IIR scaffold for a freshly-spawned NPC: [u32 0][u32 len-36][u32 word2] + the
		// constant terminator body. word2 defaults to 1 (the animal form, byte-identical to the 23 minimal-form
		// NPCs in the beach burst); pass MinimalHumanoidWord2 (2) for a no-morph humanoid (#243 tail).
		public static byte[] BuildMinimalPostIir(uint word2 = MinimalWord2)
		{
			return new FaolanStream().WriteUInt32(HeaderWord0).WriteUInt32(MinimalPostIirLength - RecordLenBias).WriteUInt32(word2).WriteArray(MinimalBody).ToArray();
		}

		// The fresh post-IIR word2 for a NO-MORPH spawn (#243): the type-stable third header word (1, 2 or 3).
		// Reads it off the exact 41-byte minimal form AND a larger RUNTIME-BEARING no-morph capture - the recordCount
		// word is stable per type across minimal and runtime captures
		// (validated: 0/107 no-morph types carry >1 distinct value), and the runtime records are dropped on a fresh
		// spawn, so BuildMinimalPostIir(word2) is the canonical fresh form. Returns false when the body carries a
		// liftable morph table (an APPEARANCE type - it builds from morph rows, not the minimal form), when word0 isn't
		// the constant 0, or when no CharacterIIR is present. This is what lets the ~24 runtime-bearing no-morph types
		// (whose type has no minimal-form capture) derive their word2 without a guess.
		public static bool TryReadNoMorphWord2(byte[] body, out uint word2)
		{
			word2 = 0;
			if (NpcSpawnAppearanceCodec.TryDecodeAppearance(body, out _) || NpcSpawnAppearanceCodec.TryDecodeAppearanceLenient(body, out _))
				return false; // an appearance type - builds from morph rows, not the minimal form
			if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
				return false;

			var post = body.AsSpan(read.Offset + read.Length);
			if (post.Length < 12 || post.ReadU32Be(0) != HeaderWord0)
				return false;

			word2 = post.ReadU32Be(8);
			// 1, 2 or 3. The old check accepted only 1 or 2, which is the stale "observed as 1 or 2" claim acting as a
			// FILTER: a type whose word2 is 3 was declined here, left with a null column, and so kept splicing its
			// captured Data blob instead of building from scratch. 23 captured streams carry a 3.
			return word2 is MinimalWord2 or MinimalHumanoidWord2 or MinimalThirdWord2;
		}
	}
}
