using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Constants;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// What the NPC post-IIR's record BODY actually contains (Gitea #475).
	//
	// It was carried as an opaque `byte[] Records` described as "session runtime state the simulation re-derives".
	// Walked over the whole corpus, it is almost entirely made of two things this codebase ALREADY decodes and had
	// simply never connected to this stream:
	//
	//   [shared header] [first record] [k x 60-byte ScaffoldBuff] [morph table] [20-byte terminator]
	//
	// The buff records are the same 60-byte shape the PLAYER scaffold carries (`ScaffoldBuff`), and the morph table
	// is the same one `NpcSpawnAppearanceCodec` parses. So "runtime state" was right about the buffs and wrong about
	// the rest.
	public class NpcPostIirBodyTests
	{
		// The 20-byte closing terminator: the empty entry table's marker plus the three empty pair lists.
		private const int TerminatorLength = 20;

		[Fact]
		public void EveryNpcPostIirBody_IsBuffRecordsThenAMorphTable()
		{
			var census = new Dictionary<string, int>();

			foreach (var (_, body) in CorpusExtract.OfType((uint)GameServer0X2000Opcodes.SimpleCharFullUpdateIIR))
			{
				if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
					continue;

				var post = body[(read.Offset + read.Length)..];
				census[Classify(post)] = census.GetValueOrDefault(Classify(post)) + 1;
			}

			// Pinned to the FULL corpus. 11,032 of 11,632 streams - 95% - are accounted for by grammar that already
			// existed; the two buckets at the bottom are what is genuinely still opaque, and naming their size is the
			// point of this gate. A change that "simplifies" the body back into a blob moves these numbers.
			Assert.Equal(new Dictionary<string, int>
			{
				["single record"] = 3875,
				["morph table"] = 6889,
				["buffs + morph table"] = 268,
				["unresolved body"] = 321,
				["non-standard terminator"] = 279
			}, census);
		}

		// The 60-byte records the NPC stream carries ahead of its morph table are ScaffoldBuffs, not anonymous
		// runtime bytes: every one is the player scaffold's shape, and its spell id is a row in our own catalog.
		[Fact]
		public void TheNpcsSixtyByteRecords_AreTheSameBuffRecordsThePlayerCarries()
		{
			var spellIds = new HashSet<uint>();
			var records = 0;

			foreach (var (_, body) in CorpusExtract.OfType((uint)GameServer0X2000Opcodes.SimpleCharFullUpdateIIR))
			{
				if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
					continue;

				var post = body[(read.Offset + read.Length)..];
				foreach (var buff in BuffsOf(post))
				{
					records++;
					spellIds.Add(buff.SpellId);
					Assert.Equal(ScaffoldBuff.DefaultKind, buff.Kind);
					Assert.Equal(EntityClass.Character, buff.TargetClass);
					Assert.InRange(buff.CasterLevel, 0u, 80u);
					Assert.True(buff.TimerRemaining <= buff.TimerTotal, "the remaining timer must not exceed the total");
				}
			}

			Assert.Equal(268, records);

			// One spell across all of them, and it is a real combat debuff rather than a number that merely parses.
			Assert.Single(spellIds);
			Assert.Equal(3911037u, spellIds.Single()); // Spells row 3911037 = "Stagger"
		}

		private static List<ScaffoldBuff> BuffsOf(byte[] post)
		{
			var found = new List<ScaffoldBuff>();
			if (BodyStart(post) is not { } start)
				return found;

			var end = post.Length - TerminatorLength;
			for (var k = 0; k < 12; k++)
			{
				var at = start + ScaffoldBuff.ByteCount * k;
				if (at + 4 > end)
					break;

				if (!IsMorphTable(post, at, end))
					continue;

				for (var j = 0; j < k; j++)
					found.Add(ReadBuff(post, start + ScaffoldBuff.ByteCount * j));

				break;
			}

			return found;
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

		private static string Classify(byte[] post)
		{
			if (BodyStart(post) is not { } start)
				return "non-standard terminator";
			if (start == post.Length - 24)
				return "single record";

			var end = post.Length - TerminatorLength;
			for (var k = 0; k < 12; k++)
			{
				var at = start + ScaffoldBuff.ByteCount * k;
				if (at + 4 > end)
					break;

				if (IsMorphTable(post, at, end))
				{
					return k == 0
						? "morph table"
						: "buffs + morph table";
				}
			}

			return "unresolved body";
		}

		// The body starts after the shared header and the first record. Returns null when the stream does not end in
		// the standard empty terminator, which is its own bucket rather than a silent skip.
		private static int? BodyStart(byte[] post)
		{
			if (SpawnPostIirHeader.Read(post) is not { } header || post.Length < 24)
				return null;

			var standard = new byte[] { 0, 0, 0, 0, 0, 0, 3, 0xF1, 0, 0, 3, 0xF1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3, 0xF1 };
			if (!post[^24..].SequenceEqual(standard))
				return null;

			var start = header.ByteCount + (int)header.FirstRecordLen;
			return start > post.Length - TerminatorLength
				? null
				: start;
		}

		// [u32 count] then count x ([u32 morphId][f32 value][u32 0][u8 0][u8 strLen][strLen bytes]), landing EXACTLY
		// on the terminator - the grammar NpcSpawnAppearanceCodec already parses.
		private static bool IsMorphTable(byte[] post, int at, int end)
		{
			if (at + 4 > end)
				return false;

			var count = post.ReadU32Be(at);
			if (count is 0 or > 0x100)
				return false;

			var i = at + 4;
			for (var n = 0; n < count; n++)
			{
				if (i + 14 > end || i + 14 + post[i + 13] > end)
					return false;

				i += 14 + post[i + 13];
			}

			return i == end;
		}
	}
}
