using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Spawn
{
	/// <summary>
	///     Classifies a captured spawn body: does it carry a liftable appearance (morph) table, and if not, what is its
	///     type-stable post-IIR movement mode? Not a codec - the encoding lives in <see cref="SpawnPostIirCodec" /> and
	///     everything here is a question about a decoded stream, for the importer's lift decisions.
	/// </summary>
	/// <remarks>
	///     Dropping a humanoid's morph table renders the NPC bald. Animals carry none: their slot is empty, the 41-byte
	///     minimal form <see cref="SpawnPostIirs" />.Npc builds.
	/// </remarks>
	public static class NpcSpawnAppearance
	{
		/// <summary>
		///     Lifts the appearance/morph table out of a body's post-IIR. Returns false, with an empty table, when the body
		///     is not an appearance stream at all: an animal's minimal no-morph form, a player's, or a mid-session capture
		///     whose entries carry runtime state - those are left to the minimal-scaffold path. This strict form
		///     additionally requires the stream to be fresh: a first record with nothing in front of the buff list's framing
		///     word, and no buffs in that list.
		/// </summary>
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

		/// <summary>
		///     The lenient variant: lifts the same morph table from a mid-session capture, where the stream also carries
		///     runtime state - a large first record holding movement and bounds state accrued in play, and the character's
		///     active buffs. A fresh spawn drops both, so the table alone is the canonical appearance, and the
		///     clean-appearance captures of the same type carry an identical one.
		/// </summary>
		/// <remarks>
		///     The table must be located by decoding the stream, never by arithmetic from the first record: each active buff
		///     pushes it 60 bytes further, so a buffed humanoid is otherwise classified no-morph and spawns bald.
		/// </remarks>
		public static bool TryLiftLenient(byte[] body, out List<AppearanceEntry> morphs)
		{
			morphs = [];
			if (!TryParsePost(body, out var post))
				return false;

			// An empty table is the minimal (no-morph) form rather than an appearance, and a non-empty closing list
			// means the stream is more than the appearance and is not what the from-database build reproduces. Both
			// keep this classifier narrower than the reader it runs on.
			//
			// Word2 is appearance data and must not disqualify a table: it is a dense ordinal over a contiguous prefix,
			// carried only on *texscript_* entries, on 13 of 3,440 captured sends with a morph list (--report
			// spawn-diff). NpcSpawnBuilder emits the column, so a lifted table carrying it still round-trips
			// byte-exact; the argument is on NpcSpawnMorph.Word2.
			//
			// A non-empty skill or pet list marks the stream as a player's - the slot grammar is shared, so the
			// appearance table alone does not say whose it is - and a player's appearance is CharacterBody data rather
			// than an NPC type's.
			if (post.Slot.Appearance.Count == 0 || post.Slot.Skills.Count > 0 || post.Slot.Pets.Count > 0)
				return false;
			if (post.EndPairs.Count > 0 || post.EndPairs2.Count > 0 || post.EndPairs3.Count > 0)
				return false;

			morphs = post.Slot.Appearance;
			return true;
		}

		/// <summary>
		///     The fresh post-IIR movement mode for a no-morph spawn: the type-stable third header word, 1, 2 or 3. It is
		///     read off the exact 41-byte minimal form and off a larger runtime-bearing no-morph capture alike, the word
		///     being stable per type across both (no no-morph type carries more than one distinct value), and the runtime
		///     records are dropped on a fresh spawn, so SpawnPostIirs.Npc of that word is the canonical fresh form.
		/// </summary>
		/// <remarks>
		///     Returns false when the body carries a liftable morph table - an appearance type, which builds from morph rows
		///     - when the head-word list is not empty, or when the body does not decode. The accepted set must stay
		///     <see cref="SpawnPostIirs" />.IsMovementMode rather than a narrower literal: 23 captured streams carry a 3, and
		///     declining them leaves those types with a null column.
		/// </remarks>
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

		/// <summary>
		///     The post-IIR of a captured body, or false when the body is not a decodable spawn. <paramref name="body" /> is
		///     the raw wire body including its leading u32 carrier length, which the codec does not own, so the length is
		///     stripped here, at the boundary between a captured body and the record.
		/// </summary>
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
