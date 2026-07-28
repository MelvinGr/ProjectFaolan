using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Constants;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// What the NPC post-IIR's record BODY actually contains (Gitea #475).
	//
	// It was carried as an opaque `byte[] Records` described as "session runtime state the simulation re-derives".
	// One walk consumes 11,399 of the 11,632 captured streams to their EXACT last byte:
	//
	//   [shared header]                          SpawnPostIirHeader
	//   [first record - firstRecordLen bytes]
	//   [k x 60-byte ScaffoldBuff]               the PLAYER scaffold's buff record, on NPCs too
	//   [u32 count][count x morph entry]         the entry-table slot; an NPC's entries are morph entries
	//   [u32][u32]                               the pair the player codec reads as its ID-list close
	//   [u32 c1][c1 x 8] [u32 c2][c2 x 8]        two PLAIN-counted pair lists
	//   [u32 ListFrame][n x 8]                   the count-framed third
	//
	// The last four lines are the player's own tail. The one genuine difference is the ENTRY shape: a player's
	// entries are 14 bytes, an NPC's are the variable-width morph records NpcSpawnAppearanceCodec already parses.
	// Same slot, different element - which is why the two looked like unrelated formats, and why the "single record"
	// forms turn out to be this shape with count == 0 rather than a form of their own.
	public class NpcPostIirBodyTests
	{
		[Fact]
		public void EveryNpcPostIirBody_WalksAsBuffsThenAnEntryTableThenThePlayersTail()
		{
			var census = new Dictionary<string, int>();

			foreach (var (_, body) in CorpusExtract.OfType((uint)GameServer0X2000Opcodes.SimpleCharFullUpdateIIR))
			{
				if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
					continue;

				var post = body[(read.Offset + read.Length)..];
				census[Walk(post, out _) ? "walks" : "unresolved"] = census.GetValueOrDefault(Walk(post, out _) ? "walks" : "unresolved") + 1;
			}

			// Pinned to the FULL corpus: 11,626 of 11,632 consume to their exact last byte - 99.95%.
			//
			// One of the six holdouts is a corrupt capture record rather than a wire shape: its ASCII is scrambled in
			// 4-byte groups ("uoY era ehtsohCuG", "dilanregof t") while other strings in the same body read cleanly,
			// which is a mis-reassembled TCP segment. Asserted as part of the count rather than excluded, so it stays
			// visible.
			//
			// The other five are the price of the LooksLikeBuff guard, and worth paying: without it the k-search found
			// runs of zero bytes that made the arithmetic work out and invented buff records with a zero target class.
			// Five streams that "walked" for the wrong reason are a worse result than five that do not walk.
			Assert.Equal(new Dictionary<string, int>
			{
				["walks"] = 11626,
				["unresolved"] = 6
			}, census);
		}

		// Every 60-byte record the walk finds, on NPCs and players alike, is a ScaffoldBuff. The spell ids are the
		// proof that this is a buff list and not an arithmetic coincidence: they resolve in our own catalog and read
		// as a buff bar - and nine of them turn up on NPCs, two of those being necromancer PET summons.
		[Fact]
		public void EverySixtyByteRecordTheWalkFinds_IsAScaffoldBuff()
		{
			var spellIds = new HashSet<uint>();
			var casterLevels = new List<uint>();
			var records = 0;

			foreach (var (_, body) in CorpusExtract.OfType((uint)GameServer0X2000Opcodes.SimpleCharFullUpdateIIR))
			{
				if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
					continue;

				var post = body[(read.Offset + read.Length)..];
				if (!Walk(post, out var buffs))
					continue;

				foreach (var buff in buffs)
				{
					records++;
					spellIds.Add(buff.SpellId);
					casterLevels.Add(buff.CasterLevel);
					Assert.Equal(ScaffoldBuff.DefaultKind, buff.Kind);
					Assert.Equal(EntityClass.Character, buff.TargetClass);
					Assert.True(buff.TimerRemaining <= buff.TimerTotal, "the remaining timer must not exceed the total");
				}
			}

			Assert.Equal(941, records);

			// The nine that appear on NPC spawns, all rows in Spells: Runed Flesh (Rank 5 / Rank 1), Ether Channel,
			// Undead Minion: Mutilator, Deathless Acolyte: Arcanist, Hearty Meal, Shrink, Stagger, Frozen Hatred. Two
			// are necromancer PET summons and one is the Frozen Hatred proc - corroboration from a direction this
			// walk knows nothing about (#307/#140).
			uint[] onNpcs = [222825, 1148789, 1151265, 1220007, 1366140, 3161312, 3774365, 3911037, 4377209];
			Assert.All(onNpcs, id => Assert.Contains(id, spellIds));
			Assert.DoesNotContain(0u, spellIds);

			// ⚠️ 0..82 across both families, and 82 is ABOVE the level cap. So the field is level-LIKE - #438
			// identified it as the caster's level by a separate route - but this corpus does not prove it IS the
			// level, and asserting 1..80 here would have written a wrong bound into a gate. (The NPC records alone
			// run 5..82; the 0s come from the player side.)
			Assert.Equal(0u, casterLevels.Min());
			Assert.Equal(82u, casterLevels.Max());
		}

		// True when the whole stream walks; `buffs` are the records found before the entry table. `k` is searched
		// 0..11 because the buff records are fixed-width and everything after them self-checks, so the first k whose
		// walk lands exactly on the stream's end is the answer rather than a guess.
		private static bool Walk(byte[] post, out List<ScaffoldBuff> buffs)
		{
			buffs = [];
			if (SpawnPostIirHeader.Read(post) is not { } header)
				return false;

			var start = header.ByteCount + (int)header.FirstRecordLen;
			if (start >= post.Length)
				return false;

			for (var k = 0; k < 12; k++)
			{
				// A buff record is RECOGNISABLE, so require it rather than inferring it from arithmetic. Without this
				// the k-search happily "found" runs of zero bytes that made the rest line up, and invented buff
				// records with a zero target class - a walk that lands on the right endpoint for the wrong reason.
				if (k > 0 && !LooksLikeBuff(post, start + ScaffoldBuff.ByteCount * (k - 1)))
					break;

				var at = start + ScaffoldBuff.ByteCount * k;

				// Two entry shapes share this slot. The NPC's is the morph table; the player's is its 14-byte entry
				// table with the ID list behind it. Whichever lands exactly on the stream's end is the answer - they
				// cannot both, because each self-checks against the same fixed endpoint.
				var walked = (MorphEntriesEnd(post, at) is { } m && TailEndsAtStreamEnd(post, m + 8))
							|| (PlayerEntriesEnd(post, at) is { } q && TailEndsAtStreamEnd(post, q));
				if (!walked)
					continue;

				for (var j = 0; j < k; j++)
					buffs.Add(ReadBuff(post, start + ScaffoldBuff.ByteCount * j));

				return true;
			}

			return false;
		}

		// [u32 count][count x ([u32 morphId][f32 value][u32 0][u8 0][u8 strLen][strLen bytes])]. count == 0 is the
		// "single record" form - the same slot, empty.
		private static int? MorphEntriesEnd(byte[] post, int at)
		{
			if (at + 4 > post.Length)
				return null;

			var count = post.ReadU32Be(at);
			if (count > 0x100)
				return null;

			var i = at + 4;
			for (var n = 0; n < count; n++)
			{
				if (i + 14 > post.Length || i + 14 + post[i + 13] > post.Length)
					return null;

				i += 14 + post[i + 13];
			}

			return i;
		}

		// The PLAYER's entry table in the same slot: [u32 count][u32 entryTableX], then - when count > 0 - the 1.0
		// weight word, `count` 14-byte entries, the 5-byte ID list, its [u8 rank][ListFrame] close and that many
		// 13-byte tail records. Returns where it ends, or null when this is not that shape.
		private static int? PlayerEntriesEnd(byte[] post, int at)
		{
			if (at + 8 > post.Length)
				return null;

			var count = post.ReadU32Be(at);
			if (count > 0x1000)
				return null;

			var i = at + 8; // count + entryTableX
			if (count == 0)
				return i + 4 <= post.Length ? i + 4 : null; // the count == 0 marker

			if (i + 4 > post.Length || post.ReadU32Be(i) != 0x3F800000)
				return null;

			i += 4 + 14 * (int)count;
			while (true) // the ID list: 5-byte groups until a ListFrame word turns up in the id slot
			{
				if (i + 5 > post.Length)
					return null;

				var id = post.ReadU32Be(i + 1);
				if (id != 0 && id % ListFrame.Unit == 0)
					break;

				i += 5;
			}

			i++; // the terminating group's rank byte
			if (!ListFrame.TryCountOf(post.ReadU32Be(i), out var refs))
				return null;

			i += 4 + 13 * refs;
			return i <= post.Length ? i : null;
		}

		// Two PLAIN-counted pair lists, then the ListFrame-counted third.
		private static bool TailEndsAtStreamEnd(byte[] post, int o)
		{
			var i = o;
			if (i > post.Length)
				return false;

			for (var list = 0; list < 2; list++)
			{
				if (i + 4 > post.Length)
					return false;

				var count = post.ReadU32Be(i);
				if (count > 4096)
					return false;

				i += 4 + 8 * (int)count;
			}

			if (i + 4 > post.Length || !ListFrame.TryCountOf(post.ReadU32Be(i), out var third))
				return false;

			return i + 4 + 8 * third == post.Length;
		}

		// The two fields every one of the verified records carries: the framing word and the target's entity class.
		private static bool LooksLikeBuff(byte[] post, int at)
		{
			return at + ScaffoldBuff.ByteCount <= post.Length
					&& post.ReadU32Be(at + 4) == ScaffoldBuff.DefaultKind
					&& post.ReadU32Be(at + 32) == EntityClass.Character;
		}

		private static ScaffoldBuff ReadBuff(byte[] post, int at)
		{
			var buff = new ScaffoldBuff
			{
				SpellId = post.ReadU32Be(at),
				Kind = post.ReadU32Be(at + 4),
				SourceHandle = post.ReadU32Be(at + 8),
				Stamp = post.ReadU32Be(at + 12),
				TimerTotal = post.ReadU32Be(at + 16),
				TimerRemaining = post.ReadU32Be(at + 20),
				Word6 = post.ReadU32Be(at + 24),
				CasterLevel = post.ReadU32Be(at + 28),
				TargetClass = post.ReadU32Be(at + 32),
				TargetHandle = post.ReadU32Be(at + 36)
			};
			for (var w = 0; w < ScaffoldBuff.TrailerWords; w++)
				buff.Trailer[w] = post.ReadU32Be(at + 40 + 4 * w);

			return buff;
		}
	}
}
