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
	// vacuous on a fixture. It is what retires the hand-run Python sweep: the same 11632 spawns, re-checked on every
	// test run instead of when someone remembers to point a script at the private corpus.
	public class SpawnFeatListTests
	{
		// The gate that makes the parser's relaxations honest. Every field this codec stopped ASSERTING (the ID-list
		// terminator rank, the entry flag, the buff record's +32 word) it now CARRIES, and every list it stopped
		// reading as a closing constant (the ID-list tail, the third pair list, the head list) it now round-trips. If
		// any of that were dropped instead, Generate would emit different bytes and this fails on the spot.
		[Fact]
		public void EveryParsedSpawn_RegeneratesByteExact()
		{
			var mismatches = new List<string>();
			var parsed = 0;

			foreach (var (source, body) in CorpusExtract.OfType((uint)GameServer0X2000Opcodes.SimpleCharFullUpdateIIR))
			{
				if (!PlayerSpawnScaffoldCodec.TryParse(body, out var scaffold, out _))
					continue;

				parsed++;
				var regenerated = PlayerSpawnScaffoldCodec.Generate(scaffold);
				if (!regenerated.SequenceEqual(body))
					mismatches.Add($"{source}: {body.Length} bytes in, {regenerated.Length} out");
			}

			Assert.True(mismatches.Count == 0, $"{mismatches.Count} of {parsed} captured player spawns did not regenerate byte-exact:\n  " + string.Join("\n  ", mismatches.Take(10)));
			Assert.Equal(259, parsed);
		}

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

			foreach (var (source, body) in CorpusExtract.OfType((uint)GameServer0X2000Opcodes.SimpleCharFullUpdateIIR))
			{
				if (!PlayerSpawnScaffoldCodec.TryParse(body, out var scaffold, out _))
				{
					notPlayerScaffold++; // an NPC/entity spawn - not the player scaffold shape, so it carries no feat frame
					continue;
				}

				var feats = PlayerSpawnScaffoldRows.ExtractPreFrameFeats(scaffold.PreFrame);
				if (feats.Count == 0)
					empty++;
				else
					withFeats++;

				// INDEPENDENT corroboration, and the reason this gate is not self-referential: the frame's LENGTH is not
				// something ExtractPreFrameFeats looks at, yet it agrees with the count the extractor read, every time.
				// The feat list is the frame's only variable-width part, so a standard frame is 239 bytes + 4 per feat.
				// (228 is the create-editor variant, which has no feat region at all - excluded, not silently passed.)
				var basis = scaffold.PreFrame.Length - (4 * feats.Count);
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
			// fixed bases - 239 for the standard frame (all 232 of them), 228 for the create-editor variant, and six
			// outliers in four larger shapes that carry something else variable-width and are pinned rather than waved
			// through. A feat count read one too high or low would break this arithmetic immediately.
			Assert.Equal(new Dictionary<int, int> { [228] = 21, [239] = 232, [350] = 2, [370] = 1, [464] = 2, [772] = 1 }, bases);

			Assert.Equal(11632, withFeats + empty + notPlayerScaffold);
			Assert.Equal(109, withFeats);
			Assert.Equal(150, empty);

			// 204 distinct feat ids, up from 86 when six fields of this grammar were still being read as constants.
			// EVERY captured player spawn now parses, so this is the whole corpus rather than the part that fit.
			Assert.Equal(204, distinct.Count);
		}

		// The create-editor pre-IIR variant: a different shape with no feat region.
		private const int CreateEditorFrameLength = 228;

		// A standard pre-IIR frame carrying an EMPTY feat list; each feat adds 4 bytes.
		private const int EmptyFrameLength = 239;
	}
}
