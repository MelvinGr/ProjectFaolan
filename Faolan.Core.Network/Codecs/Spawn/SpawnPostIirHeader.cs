using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// The header every FA015807 post-IIR stream opens with - PLAYER and NPC alike (Gitea #475):
	//
	//   [u32 headWordCount][headWordCount x u32] [u32 firstRecordLen] [u32 word2]
	//
	// It was modelled twice, differently, and both models made a claim that is not true:
	//   - `PlayerSpawnScaffoldCodec` wrote the last two words as the bare literals `4` and `2`, which reads as "these
	//     are protocol constants". They are only PLAYER constants: an NPC carries 5 and 1/2/3.
	//   - `NpcSpawnPostIir` called the first word `Word0` and documented it as "constant 0 across the corpus (the
	//     player spawn has 2 in this slot)". It is neither constant nor 2 on a player: it is the head-word list's
	//     COUNT, and it is 0 on 11,627 captured streams and 1 on the 5 that carry a head word.
	//
	// One reader now covers both, gated over the corpus by SpawnPostIirGrammarTests.
	public sealed class SpawnPostIirHeader
	{
		// The stream's own leading plain-counted u32 list. Empty on every NPC and on all but a handful of players
		// (those carry a single word, 60), which is exactly why it read as a constant 0 for so long.
		public List<uint> HeadWords { get; } = [];

		// The length in bytes of the FIRST record that follows. 4 on a player - the buff list's count word alone,
		// which stays 4 whether the character has 0 or 3 buffs because the buff records are that list's ELEMENTS -
		// and 5 on an NPC's minimal form, whose first record carries one extra leading byte.
		public uint FirstRecordLen { get; set; }

		// The third word. NOT a count of anything: streams of identical length carry 1, 2 and 3 in it. Type-stable
		// per NPC and always 2 on a player; meaning unknown, so it is named for its position.
		public uint Word2 { get; set; }

		// The header's own byte length.
		public int ByteCount => 4 + 4 * HeadWords.Count + 8;

		public byte[] ToArray()
		{
			var stream = new FaolanStream().WriteUInt32((uint)HeadWords.Count);
			foreach (var word in HeadWords)
				stream.WriteUInt32(word);

			return stream.WriteUInt32(FirstRecordLen).WriteUInt32(Word2).ToArray();
		}

		// Reads the header at `offset`. Returns null when the stream is too short to hold one - the caller decides
		// whether that is fatal, since this is also used to probe a body that may not be a spawn at all.
		public static SpawnPostIirHeader? Read(byte[] post, int offset = 0)
		{
			if (post == null || offset + 4 > post.Length)
				return null;

			var count = post.ReadU32Be(offset);
			if (count > 0x1000 || offset + 4 + 4 * (long)count + 8 > post.Length)
				return null;

			var header = new SpawnPostIirHeader();
			for (var i = 0; i < count; i++)
				header.HeadWords.Add(post.ReadU32Be(offset + 4 + 4 * i));

			var at = offset + 4 + 4 * (int)count;
			header.FirstRecordLen = post.ReadU32Be(at);
			header.Word2 = post.ReadU32Be(at + 4);
			return header;
		}
	}
}
