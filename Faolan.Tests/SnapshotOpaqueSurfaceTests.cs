using System.Collections.Generic;
using System.Linq;
using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The opaque-surface ratchet for the 0x737A6DF9 snapshot tails. Every unmodelled byte is a byte a
	// character in an uncaptured state could carry in a shape that now throws at parse (the silent fallbacks were
	// removed). This gate pins the opaque surface at its current floor so a future change cannot quietly widen it -
	// the failure mode that let the quest index sit unrecognised in 81 of 141 snapshots (the "no quest index" finding)
	// while every round-trip stayed green.
	//
	// The quest index was the largest known-unknown: it opens at its id-list marker (count+1)*0x3F1, not a leading
	// 0x3F1 "IndexTag" (that word is the empty completed-journal frame), and its per-quest blocks carry the area's
	// catalog with real (non-monotonic) sequences and per-quest trailer words. Modelling it dropped
	// UnknownBeforeAccessWindowTable from 105,744 B to 0 across the corpus, and the field is gone outright (the whole
	// subclass reads sequentially), so the pre-access half of this ratchet is structural: any unrecognised byte there
	// throws at parse instead of falling into a blob.
	public class SnapshotOpaqueSurfaceTests
	{
		private readonly ITestOutputHelper _o;

		public SnapshotOpaqueSurfaceTests(ITestOutputHelper o)
		{
			_o = o;
		}

		// The quest index must be identified (not left opaque) in every snapshot that holds quests. This is the direct
		// regression guard for the index decode: it fails the moment a journal shape stops being recognised.
		[Fact]
		public void EverySnapshotWithQuests_HasAnIdentifiedQuestIndex()
		{
			int total = 0, withQuests = 0, indexed = 0;
			var missing = new List<string>();

			foreach (var (source, sub) in SubClasses())
			{
				total++;
				if (sub.Quests.Records.Count == 0)
					continue;

				withQuests++;
				if (sub.QuestIndex is { QuestIds.Count: > 0 })
					indexed++;
				else
					missing.Add(source);
			}

			_o.WriteLine($"snapshots={total} withActiveQuests={withQuests} indexed={indexed}");
			Assert.True(withQuests > 0, "corpus carried no snapshot with active quests");
			Assert.True(missing.Count == 0, $"{missing.Count} snapshot(s) with active quests have NO identified quest index (opaque regression): " + string.Join(", ", missing.Distinct().Take(8)));
		}

		private static IEnumerable<(string Source, FullCharSnapshotSubClass SubClass)> SubClasses()
		{
			foreach (var (source, body) in CorpusExtract.OfType((uint)IirTypes.FullPlayerCharacterIIR))
			{
				var declared = body.ReadU32Be(0);
				if (declared + 4 > body.Length)
					continue; // truncated capture - the TCP-reassembly gap on compressed downloads

				var stream = new FaolanStream(body[4..]);
				stream.ReadUInt32();
				stream.ReadUInt32();
				stream.ReadUInt32();
				yield return (source, SnapshotPackets.Parse(stream).SubClass);
			}
		}
	}
}
