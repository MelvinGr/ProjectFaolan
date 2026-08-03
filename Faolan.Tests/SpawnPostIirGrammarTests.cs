using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The NPC post-IIR stream and the PLAYER post-IIR stream are the same grammar (Gitea #475).
	//
	// They were modelled as two unrelated things: `PlayerSpawnScaffoldCodec` wrote a `[u32 4][u32 2]` pair as bare
	// constants, and `NpcSpawnPostIir` read the same two words as `[firstRecordLen][recordCount]` and kept
	// everything after them as an opaque `byte[] Records`. Both are the same header, and what follows it is the
	// same stream apart from one slot:
	//
	//   [u32 headCount][headCount x u32] [u32 firstRecordLen] [u32 word2]
	//   [firstRecordLen bytes - the first record, ENDING in the buff list's framing word]
	//   [buffs] [the entry slot] [pair list][pair list][ListFrame-counted pair list]
	//
	// The player's "4" is its first record's length and its "2" is word2; an NPC's are 5 and 1/2/3. So neither is a
	// protocol constant - they are only constant for the player, which is exactly how they came to be literals.
	public class SpawnPostIirGrammarTests
	{
		// The MINIMAL forms, whose entry slot and pair lists are all empty: what is being pinned here is the header
		// the two codecs used to disagree about, and the fact that one reader now covers both families.
		[Fact]
		public void TheNpcAndPlayerPostIirStreams_AreTheSameGrammar()
		{
			var shapes = new Dictionary<string, int>();

			foreach (var (body, post) in Streams())
			{
				if (!SpawnBodyReader.TryParse(body, out var spawn) || MorphsOf(spawn).Count > 0)
					continue;
				if (spawn.Post.BuffRecords.Count > 0 || spawn.Post.EndPairs.Count > 0 || spawn.Post.EndPairs2.Count > 0 || spawn.Post.EndPairs3.Count > 0)
					continue;

				var shape = $"len={post.Length} firstRecordLen={spawn.Post.FirstRecordLen} word2={spawn.Post.MovementMode}";
				shapes[shape] = shapes.GetValueOrDefault(shape) + 1;
			}

			// Pinned to the FULL corpus. The 40-byte form is the create-editor preview PLAYER scaffold, fully empty;
			// the 41-byte one is the NPC minimal form, which differs by exactly one leading byte in its first record.
			// That one byte, and nothing else, is what made them look like different formats.
			Assert.Equal(21, shapes["len=40 firstRecordLen=4 word2=2"]);
			Assert.Equal(40, 12 + 4 + 24); // the empty-tail identity, spelled out for the two minimal forms
			Assert.Equal(41, 12 + 5 + 24);
			Assert.Equal(1609 + 3513 + 30, shapes.Where(s => s.Key.StartsWith("len=41 ")).Sum(s => s.Value));
		}

		// word2 is NOT a record count, which is what it was called until this landed: it takes three different values
		// across streams of IDENTICAL length, so it cannot be counting anything that occupies bytes. This is the gate
		// behind the SpawnPostIir.MovementMode rename.
		[Fact]
		public void Word2_DoesNotAffectTheStreamsLength_SoItIsNotARecordCount()
		{
			var byLength = new Dictionary<int, HashSet<uint>>();

			foreach (var (body, post) in Streams())
			{
				if (!SpawnBodyReader.TryParse(body, out var spawn) || MorphsOf(spawn).Count > 0)
					continue;

				byLength.TryAdd(post.Length, []);
				byLength[post.Length].Add(spawn.Post.MovementMode);
			}

			Assert.Equal([1u, 2u, 3u], byLength[41].Order().ToArray());
			Assert.Equal([2u], byLength[40].ToArray());
		}

		private static List<NpcAppearanceMorph> MorphsOf(SimpleCharFullUpdateIIR spawn)
		{
			return spawn.Post.Slot is NpcEntrySlot npc
				? npc.Morphs
				: [];
		}

		// Every captured FA015807's post-IIR stream, sliced off behind the embedded CharacterIIR, paired with the
		// body it came from (the codec reads the whole record, so the family is decided there rather than here).
		private static IEnumerable<(byte[] Body, byte[] Post)> Streams()
		{
			foreach (var (_, body) in CorpusExtract.OfType((uint)IirTypes.SimpleCharFullUpdateIIR))
				if (SpawnBodyReader.TryReadCharacterMessage(body, out var read))
					yield return (body, body[(read.Offset + read.Length)..]);
		}
	}
}
