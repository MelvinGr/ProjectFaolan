using System.Collections.Generic;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// The from-scratch post-IIR streams. VALUE FACTORIES, not codec surface (#530 §2 rule 1 / the wave-3 precedent):
	// they name the semantic forms a spawn is built in, and SpawnPostIirCodec stays the one encoder.
	//
	// A FRESHLY-SPAWNED NO-MORPH NPC has the MINIMAL form: a single 41-byte stream (Npc(word2)), with a type-stable
	// word2 (1, 2 or 3 - #243/#475). APPEARANCE-bearing NPCs instead carry a morph table - the "*key=val" texscript
	// string (e.g. "*ii=-1;*bl=-1") followed by [u32 morphId][float32] pairs, decoded on Casilda (#243) and stored as
	// morph rows. Emitting the minimal form for such an NPC DROPS that table and the client renders it BALD
	// ([[npc_spawn_minimal_postiir_breaks_humanoids]]).
	public static class SpawnPostIirs
	{
		// The minimal post-IIR length, in bytes, for a freshly-spawned WORLD npc. Not the shortest form on the wire:
		// the 21 create-editor preview spawns carry a 40-byte one, identical except that its first record has no
		// move-task slot (firstRecordLen 4 rather than 5). Censused over all 11,632 captured streams (#475).
		public const int MinimalNpcLength = 41;

		public const uint MinimalHumanoidWord2 = 2;

		// The third observed value (23 captured streams). Named for its value like its two siblings, because what it
		// SELECTS is unknown - see SpawnPostIir.Word2 for why this word is not a record count.
		public const uint MinimalThirdWord2 = 3;

		// The word2 of the minimal (no-morph) form. A TYPE-STABLE constant whose meaning isn't decoded - NOT an
		// animal-vs-humanoid split (both values carry people AND creatures; the names here are historical, from the
		// beach-burst sample where the animals happened to be 1 and the no-morph humanoids 2).
		// ⚠️ It is 1, 2 or **3**, not "1 or 2": a corpus-wide census of the 3,272 captured 41-byte forms gives
		// 1 x757, 2 x2492 and 3 x23 (#475). The type's own value is stored in NpcStatCore.MinimalPostIirWord2 (#243).
		public const uint MinimalWord2 = 1;

		// A fresh NPC's stream: the move-task SLOT with no task in it (the one 0x00 byte that makes its first record
		// 5 bytes where a player's is 4), no buffs, `morphs` in the entry slot, and three empty closing pair lists.
		// The 29 bytes after the header used to be a baked `MinimalBody` array described as "the constant
		// terminators". They are not constants: they are an EMPTY buff list, an EMPTY morph table and three EMPTY
		// closing pair lists, four of whose framing words are ForCount(0) - which is 0x3F1, the exact face the 0x3F1
		// trap wears (see ListFrame).
		public static SpawnPostIir Npc(uint word2 = MinimalWord2, IReadOnlyList<NpcAppearanceMorph>? morphs = null)
		{
			var slot = new NpcEntrySlot();
			if (morphs != null)
				slot.Morphs.AddRange(morphs);

			return new SpawnPostIir
			{
				Word2 = word2,
				HasMoveTaskSlot = true,
				Slot = slot
			};
		}

		// A player's stream: no move-task slot at all, and the entry table / skill id list in the entry slot. Word2
		// is 2 on every captured player spawn - a PLAYER constant, not a protocol one, which is exactly how it and
		// the first-record length came to be bare literals in the old player codec.
		public static SpawnPostIir Player()
		{
			return new SpawnPostIir
			{
				Word2 = PlayerWord2,
				HasMoveTaskSlot = false,
				Slot = new PlayerEntrySlot()
			};
		}

		// The player's own value for the header's third word.
		public const uint PlayerWord2 = 2;
	}
}
