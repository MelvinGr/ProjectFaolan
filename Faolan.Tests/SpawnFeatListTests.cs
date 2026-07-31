using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Corpus-BREADTH gate for the unlocked feat list in the player spawn's pre-IIR frame (Gitea #251).
	//
	// PlayerSpawnScaffoldRowsTests pins the ids + train order for three NAMED goldens. This asks the other question -
	// does the extractor hold across EVERY captured spawn - which is why it reads the corpus extract and would go
	// vacuous on a fixture. It is what retires the hand-run Python sweep: the same 17,717 spawns, re-checked on every
	// test run instead of when someone remembers to point a script at the private corpus.
	//
	// The byte-exact REGENERATION half of this file moved to SimpleCharFullUpdateCorpusTests when the player and NPC
	// codecs merged (#530 wave 5b): that gate covers all 17,717 captured spawns rather than the 317 player ones, so
	// keeping a narrower copy here would only be a second chance to disagree.
	public class SpawnFeatListTests
	{
		[Fact]
		public void EveryCapturedSpawn_ExtractsItsFeatList_OrNoneAtAll()
		{
			var withFeats = 0;
			var empty = 0;
			var notPlayerScaffold = 0;
			var badIds = new List<string>();
			var badLength = new List<string>();
			var distinct = new HashSet<uint>();
			var bases = new Dictionary<int, int>();

			foreach (var (source, body) in CorpusExtract.OfType((uint)IirTypes.SimpleCharFullUpdateIIR))
			{
				if (!SpawnBodyReader.TryParse(body, out var scaffold) || !SpawnBodyReader.IsPlayer(scaffold))
				{
					notPlayerScaffold++; // an NPC/entity spawn - not the player scaffold shape, so it carries no feat frame
					continue;
				}

				var feats = PlayerSpawnScaffoldRows.ExtractPreFrameFeats(scaffold.PreIir);
				if (feats.Count == 0)
					empty++;
				else
					withFeats++;

				// INDEPENDENT corroboration, and the reason this gate is not self-referential: the frame's LENGTH is not
				// something ExtractPreFrameFeats looks at, yet it agrees with the count the extractor read, every time.
				// The feat list is the frame's only variable-width part, so a standard frame is 239 bytes + 4 per feat.
				// (228 is the create-editor variant, which has no feat region at all - excluded, not silently passed.)
				var basis = scaffold.PreIir.ByteCount - 4 * feats.Count;
				bases[basis] = bases.GetValueOrDefault(basis) + 1;

				foreach (var id in feats)
				{
					distinct.Add(id);

					// featId = featBase * 100 + rank, so a real id is 4+ digits and its low two are a plausible rank. An
					// extractor reading the wrong offset shows up here as wild values rather than as a clean id list.
					if (id is < 1000 or > 9_999_999 || id % 100 > 20)
						badIds.Add($"{source}: {id}");
				}
			}

			Assert.True(badIds.Count == 0, $"{badIds.Count} extracted feat ids are outside the base*100+rank id space:\n  " + string.Join("\n  ", badIds.Take(10)));
			Assert.True(badLength.Count == 0, $"{badLength.Count} spawns whose frame length disagrees with the feat count read out of it:\n  " + string.Join("\n  ", badLength.Take(10)));

			// Pinned to the FULL corpus: a regeneration against a partial one, or an extractor that quietly stops finding
			// the list, lands here rather than passing with fewer spawns.
			// INDEPENDENT corroboration, and why this gate is not self-referential: the pre-IIR frame's LENGTH is not
			// something ExtractPreFrameFeats looks at, yet frame length minus 4 per feat collapses onto a handful of
			// fixed bases - 239 for the standard frame (all 286 of them), 228 for the create-editor variant, and three
			// larger bases for the six MULTI-SPEC frames, whose extra width is their specs' NAMES (see
			// PlayerSpawnPreFrameTests): 250 = 228 + 2 unnamed specs, 264 = 250 + len("PvE - Lance") + len("PvP"),
			// 336 = 228 + 4 named specs. A feat count read one too high or low would break this arithmetic immediately.
			// (The -5 on each is the embedded CoreCharacter's version byte + IirHeader: the old player-only model cut
			// the frame at the IIR's length prefix and so counted 5 of the MESSAGE's bytes into the scaffold.)
			// (These bases were 350/370/464/772 while the extractor read a fixed offset: on a multi-spec frame that
			// offset lands inside a spec NAME, so it found no valid count word and reported ZERO feats - base ==
			// length. The frames were pinned, but pinned to a mis-read.)
			Assert.Equal(new Dictionary<int, int> { [228 - 5] = 21, [239 - 5] = 286, [250 - 5] = 7, [264 - 5] = 2, [336 - 5] = 1 }, bases);

			Assert.Equal(17717, withFeats + empty + notPlayerScaffold);
			// 168 / 149, from 112 / 147 on the narrower corpus: the multi-spec frames whose fixed-offset read landed inside a spec name
			// reported an EMPTY feat list and were counted as unfeated characters. They are level-80 characters.
			Assert.Equal(168, withFeats);
			Assert.Equal(149, empty);

			// 329 distinct feat ids. The count is a history of what the grammar could SEE: 86 while six fields of it
			// were read as constants, 204 once the spawn parsed, 296 once the pre-IIR frame's SUBLIST was read as a
			// list of named specs instead of a single feat list at a fixed offset (the multi-spec frames alone carried
			// 92 ids nothing had ever read), and 329 on the wider 2026-07-31 corpus.
			Assert.Equal(329, distinct.Count);
		}

	}
}
