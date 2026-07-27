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
		[Fact]
		public void EveryCapturedSpawn_ExtractsItsFeatList_OrNoneAtAll()
		{
			var withFeats = 0;
			var empty = 0;
			var notPlayerScaffold = 0;
			var badIds = new List<string>();
			var badLength = new List<string>();
			var distinct = new HashSet<uint>();

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
				if (scaffold.PreFrame.Length != CreateEditorFrameLength && scaffold.PreFrame.Length != EmptyFrameLength + (4 * feats.Count))
					badLength.Add($"{source}: {feats.Count} feats but a {scaffold.PreFrame.Length}-byte frame");

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
			Assert.Equal(11632, withFeats + empty + notPlayerScaffold);
			Assert.Equal(91, withFeats);
			Assert.Equal(135, empty);

			// 141 distinct ids over the 226 spawns TryParse accepts - up from 86 over 203 when the ID-list terminator's
			// rank byte stopped being asserted to zero. Still NOT the whole corpus: ~27 captured player spawns remain
			// unparsed, 17 of them because the ID list's "0x3F1 terminator" is really the count word of a FOLLOWING list
			// (ListFrame.ForCount(2) is observed), so the walk consumes into it. That is a decode task, not an assertion
			// to relax - see the Gitea issue. Pinned exactly so the next fix shows up here as a number going UP.
			Assert.Equal(141, distinct.Count);
		}

		// The create-editor pre-IIR variant: a different shape with no feat region.
		private const int CreateEditorFrameLength = 228;

		// A standard pre-IIR frame carrying an EMPTY feat list; each feat adds 4 bytes.
		private const int EmptyFrameLength = 239;
	}
}
