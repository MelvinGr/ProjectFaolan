using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The NPC post-IIR stream and the PLAYER post-IIR stream are the same grammar (Gitea #475).
	//
	// They were modelled as two unrelated things: `PlayerSpawnScaffoldCodec` writes a `[u32 4][u32 2]` pair as bare
	// constants, and `NpcSpawnPostIir` reads the same two words as `[firstRecordLen][recordCount]` and keeps
	// everything after them as an opaque `byte[] Records`. Both are the same header:
	//
	//   [u32 headCount][headCount x u32] [u32 firstRecordLen] [u32 word2]
	//   [firstRecordLen bytes - the first record]
	//   [u32 entryCount][u32 entryTableX] ... [pair list][pair list][ListFrame-counted pair list]
	//
	// The player's "4" is its first record's length (the buff list's count word) and its "2" is word2; an NPC's are
	// 5 and 1/2/3. So neither is a protocol constant - they are only constant for the player, which is exactly how
	// they came to be written as literals.
	public class SpawnPostIirGrammarTests
	{
		// Walks the shared grammar over the empty-entry-table forms and requires it to land on the stream's LAST
		// byte. Restricted to entryCount == 0 on purpose: the entry table and ID list are already gated byte-exact by
		// PlayerSpawnScaffoldCodec, and what is being tested here is the HEADER those two codecs disagree about.
		[Fact]
		public void TheNpcAndPlayerPostIirStreams_AreTheSameGrammar()
		{
			var shapes = new Dictionary<string, int>();
			var exact = 0;
			var withEntries = 0;

			foreach (var (_, body) in CorpusExtract.OfType((uint)GameServer0X2000Opcodes.SimpleCharFullUpdateIIR))
			{
				if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
					continue;

				var post = body[(read.Offset + read.Length)..];
				var walked = Walk(post, out var firstRecordLen, out var word2, out var hasEntries);
				if (hasEntries)
				{
					withEntries++;
					continue;
				}

				// The whole assertion: the walk lands on the LAST byte. Nothing is left over and nothing is guessed.
				Assert.True(walked == post.Length, $"the post-IIR walk ended at {walked} of {post.Length} bytes");
				exact++;

				// `12 + firstRecordLen + 24` is the length only when all three pair lists are EMPTY, which is the
				// minimal forms - the shapes pinned below. It is NOT a general identity: streams with populated pair
				// lists walk to their exact end and are longer, which is what caught this claim when it was written
				// as a blanket assert.
				var shape = $"len={post.Length} firstRecordLen={firstRecordLen} word2={word2}";
				shapes[shape] = shapes.GetValueOrDefault(shape) + 1;
			}

			// Pinned to the FULL corpus. The 40-byte form is the create-editor preview PLAYER scaffold, fully empty;
			// the 41-byte one is the NPC minimal form, which differs by exactly one leading byte in its first record.
			// That one byte, and nothing else, is what made them look like different formats.
			Assert.Equal(21, shapes["len=40 firstRecordLen=4 word2=2"]);
			Assert.Equal(40, 12 + 4 + 24); // the empty-tail identity, spelled out for the two minimal forms
			Assert.Equal(41, 12 + 5 + 24);
			Assert.Equal(757 + 2492 + 23, shapes.Where(s => s.Key.StartsWith("len=41 ")).Sum(s => s.Value));
			Assert.True(exact >= 3800, $"only {exact} streams walked to their exact end");
			Assert.True(withEntries > 0, "no stream carried an entry table - the corpus slice is wrong");
		}

		// word2 is NOT a record count, which is what `NpcSpawnPostIir.RecordCount` calls it: it takes three different
		// values across streams of IDENTICAL length, so it cannot be counting anything that occupies bytes.
		[Fact]
		public void Word2_DoesNotAffectTheStreamsLength_SoItIsNotARecordCount()
		{
			var byLength = new Dictionary<int, HashSet<uint>>();

			foreach (var (_, body) in CorpusExtract.OfType((uint)GameServer0X2000Opcodes.SimpleCharFullUpdateIIR))
			{
				if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
					continue;

				var post = body[(read.Offset + read.Length)..];
				if (Walk(post, out _, out var word2, out var hasEntries) < 0 || hasEntries)
					continue;

				byLength.TryAdd(post.Length, []);
				byLength[post.Length].Add(word2);
			}

			Assert.Equal([1u, 2u, 3u], byLength[41].Order().ToArray());
			Assert.Equal([2u], byLength[40].ToArray());
		}

		// The shared walk. Returns the offset it consumed to, or -1 if the stream is not this shape.
		private static int Walk(byte[] post, out uint firstRecordLen, out uint word2, out bool hasEntries)
		{
			firstRecordLen = 0;
			word2 = 0;
			hasEntries = false;
			var i = 0;
			if (post.Length < 12)
				return -1;

			i += 4 + 4 * (int)post.ReadU32Be(i); // the plain-counted head word list
			if (i + 8 > post.Length)
				return -1;

			firstRecordLen = post.ReadU32Be(i);
			word2 = post.ReadU32Be(i + 4);
			i += 8 + (int)firstRecordLen;
			if (i + 8 > post.Length)
				return -1;

			var entryCount = post.ReadU32Be(i);
			i += 8; // entryCount + entryTableX
			if (entryCount != 0)
			{
				hasEntries = true;
				return -1;
			}

			i += 4; // the entryCount == 0 marker
			for (var list = 0; list < 2; list++)
			{
				if (i + 4 > post.Length)
					return -1;

				i += 4 + 8 * (int)post.ReadU32Be(i); // a PLAIN-counted pair list
			}

			if (i + 4 > post.Length || !ListFrame.TryCountOf(post.ReadU32Be(i), out var third))
				return -1;

			return i + 4 + 8 * third;
		}
	}
}
