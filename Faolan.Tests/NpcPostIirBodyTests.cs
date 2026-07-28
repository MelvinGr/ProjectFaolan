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
				var key = Walk(post, out var buffs)
					? $"walks, {buffs.Count} buff record(s)"
					: "unresolved";
				census[key] = census.GetValueOrDefault(key) + 1;
			}

			// Pinned to the FULL corpus: 98% consume to the exact last byte. The unresolved remainder is what is
			// genuinely left, and naming its size is the point - a change that quietly narrows the walk moves this
			// number instead of passing.
			Assert.Equal(new Dictionary<string, int>
			{
				["walks, 0 buff record(s)"] = 10773,
				["walks, 1 buff record(s)"] = 584,
				["walks, 2 buff record(s)"] = 43,
				["unresolved"] = 232
			}, census);
		}

		// The 60-byte records the NPC stream carries ahead of its entry table are ScaffoldBuffs - the same shape the
		// PLAYER scaffold carries. The spell ids are the proof: all nine resolve in our own catalog, and they read as
		// a creature's buff bar rather than as numbers that merely parse.
		[Fact]
		public void TheNpcsSixtyByteRecords_AreTheSameBuffRecordsThePlayerCarries()
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

			Assert.Equal(670, records);

			// Every id is a row in Spells. Two are necromancer PET summons and one is the Frozen Hatred proc, which is
			// corroboration from a direction this walk knows nothing about (#307/#140): Stagger, Hearty Meal, Shrink,
			// Ether Channel, Runed Flesh (Rank 1/5), Undead Minion: Mutilator, Deathless Acolyte: Arcanist.
			Assert.Equal([222825u, 1148789u, 1151265u, 1220007u, 1366140u, 3161312u, 3774365u, 3911037u, 4377209u], spellIds.Order().ToArray());

			// ⚠️ 5..82, and 82 is ABOVE the level cap. So the field is level-LIKE - #438 identified it as the caster's
			// level by a separate route - but this corpus does not prove it IS the level, and asserting 1..80 here
			// would have written a wrong bound into a gate.
			Assert.Equal(5u, casterLevels.Min());
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
				var at = start + ScaffoldBuff.ByteCount * k;
				if (EntryTableEnd(post, at) is not { } afterEntries || !TailEndsAtStreamEnd(post, afterEntries))
					continue;

				for (var j = 0; j < k; j++)
					buffs.Add(ReadBuff(post, start + ScaffoldBuff.ByteCount * j));

				return true;
			}

			return false;
		}

		// [u32 count][count x ([u32 morphId][f32 value][u32 0][u8 0][u8 strLen][strLen bytes])]. count == 0 is the
		// "single record" form - the same slot, empty.
		private static int? EntryTableEnd(byte[] post, int at)
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

		// The player's own tail: two words, two PLAIN-counted pair lists, then the ListFrame-counted third.
		private static bool TailEndsAtStreamEnd(byte[] post, int o)
		{
			var i = o + 8;
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
