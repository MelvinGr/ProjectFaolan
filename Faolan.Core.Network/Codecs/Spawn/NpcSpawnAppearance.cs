using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// CLASSIFIES a captured FA015807 body: does it carry a liftable APPEARANCE (morph) table, and if not, what is
	// its type-stable post-IIR word2? Not a codec - the encoding lives in SpawnPostIirCodec, and everything here is
	// a question ABOUT a decoded stream (the importer's lift decisions, #185/#243).
	//
	// Dropping a humanoid's morph table renders the NPC bald (#243); animals carry none (their slot is empty - the
	// 41-byte minimal form, SpawnPostIirs.Npc).
	public static class NpcSpawnAppearance
	{
		// Lifts the appearance/morph table out of a body's post-IIR. Returns false (morphs = empty) when the body is
		// not an appearance stream at all: an animal's minimal no-morph form, a player's, or a mid-session capture
		// whose entries carry runtime state. Those are left to the minimal-scaffold path. STRICT additionally
		// requires the stream to be FRESH - a first record with nothing in front of the buff list's framing word,
		// and no buffs in that list.
		public static bool TryLift(byte[] body, out List<AppearanceEntry> morphs)
		{
			morphs = [];
			if (!TryLiftLenient(body, out var table) || !TryParsePost(body, out var post))
				return false;
			if (post.BuffRecords.Count > 0 || post.MoveTask != null || !post.HasMoveTaskSlot)
				return false;

			morphs = table;
			return true;
		}

		// Lenient variant (#243): lifts the SAME morph table from a mid-session capture, where the stream also
		// carries runtime state - a large first record (movement/bounds state accrued in play) and the character's
		// active buffs. A FRESH spawn drops both, so the table alone is the canonical appearance, and the
		// clean-appearance captures of the same type carry an identical one.
		//
		// ⚠️ It used to find the table by ARITHMETIC - straight after the first record - which is where the table
		// sits only when the character has NO active buffs, because each buff pushes it 60 bytes further. A buffed
		// humanoid was therefore classified no-morph and would spawn BALD (#243's own failure mode).
		public static bool TryLiftLenient(byte[] body, out List<AppearanceEntry> morphs)
		{
			morphs = [];
			if (!TryParsePost(body, out var post))
				return false;

			// An empty table is the minimal (no-morph) form, not an appearance; an entry carrying runtime state, or
			// a non-empty closing list, means this stream is more than the appearance and is not what the from-DB
			// build reproduces. Both keep this classifier NARROWER than the reader it runs on.
			//
			// ⚠️ Word2 USED TO DISQUALIFY a table here, as "an entry carrying runtime state", and that cost three NPCs
			// their whole appearance - they spawned BALD (#672). It is APPEARANCE DATA: a dense ordinal over a
			// contiguous prefix, carried only on `*texscript_*` entries, on 13 of 3,440 captured sends with a morph
			// list (`--report spawn-diff`, the Word2 footer). The full argument is on `NpcSpawnMorph.Word2`.
			//
			// It is now CARRIED (`NpcSpawnMorph.Word2` / `NpcMorph.Word2`) rather than rejected, which is what makes
			// accepting it safe: `NpcSpawnBuilder` emits the column, so a lifted table still round-trips byte-exact.
			// (The old `Flag` veto died with the field: the byte was the u16 string length's high half, #466.)
			//
			// A non-empty SKILL or PET list marks the stream as a PLAYER's - the slot grammar is shared (#466), so
			// the appearance table alone no longer says whose it is. A player's appearance is CharacterBody data,
			// not an NPC type's.
			if (post.Slot.Appearance.Count == 0 || post.Slot.Skills.Count > 0 || post.Slot.Pets.Count > 0)
				return false;
			if (post.EndPairs.Count > 0 || post.EndPairs2.Count > 0 || post.EndPairs3.Count > 0)
				return false;

			morphs = post.Slot.Appearance;
			return true;
		}

		// The fresh post-IIR word2 for a NO-MORPH spawn (#243): the type-stable third header word (1, 2 or 3). Reads
		// it off the exact 41-byte minimal form AND a larger RUNTIME-BEARING no-morph capture - the word is stable
		// per type across minimal and runtime captures (validated: 0/107 no-morph types carry >1 distinct value),
		// and the runtime records are dropped on a fresh spawn, so SpawnPostIirs.Npc(word2) is the canonical fresh
		// form. Returns false when the body carries a liftable morph table (an APPEARANCE type - it builds from
		// morph rows), when the head-word list is not empty, or when the body does not decode.
		//
		// ⚠️ The old check accepted only 1 or 2, which is the stale "observed as 1 or 2" claim acting as a FILTER: a
		// type whose word2 is 3 was declined here, left with a null column, and so kept splicing its captured Data
		// blob instead of building from scratch. 23 captured streams carry a 3.
		public static bool TryReadNoMorphWord2(byte[] body, out uint word2)
		{
			word2 = 0;
			if (TryLift(body, out _) || TryLiftLenient(body, out _))
				return false; // an appearance type - builds from morph rows, not the minimal form
			if (!TryParsePost(body, out var post) || post.HeadWords.Count > 0)
				return false;

			word2 = post.MovementMode;
			return SpawnPostIirs.IsMovementMode(word2);
		}

		// The post-IIR of a captured body, or false when the body is not a decodable spawn. `body` is the raw wire
		// body INCLUDING its leading u32 carrier length, which the codec does not own (#530 rule 2) - so the length
		// is stripped here, at the boundary between a captured body and the record.
		private static bool TryParsePost(byte[] body, out SpawnPostIir post)
		{
			post = null!;
			if (body == null || body.Length < 4)
				return false;

			try
			{
				post = SimpleCharFullUpdateCodec.Parse(body.AsSpan(4)).Post;
				return true;
			}
			catch (CodecParseException)
			{
				return false;
			}
		}
	}
}
