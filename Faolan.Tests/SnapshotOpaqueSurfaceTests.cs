using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The opaque-surface RATCHET for the 0x737A6DF9 snapshot tails (Gitea #391). Every unmodelled byte is a byte a
	// character in an uncaptured state could carry in a shape that now THROWS at parse (the silent fallbacks were
	// removed). This gate pins the opaque surface at its current floor so a future change cannot quietly widen it -
	// the failure mode that let the quest index sit unrecognised in 81 of 141 snapshots (the "no quest index" finding)
	// while every round-trip stayed green.
	//
	// The quest INDEX was the largest known-unknown: it opens at its id-list marker (count+1)*0x3F1, NOT a leading
	// 0x3F1 "IndexTag" (that word is the empty completed-journal frame), and its per-quest blocks carry the area's
	// catalog with real (non-monotonic) sequences and per-quest trailer words. Modelling it dropped
	// UnknownBeforeAccessWindowTable from 105,744 B to 0 across the corpus.
	public class SnapshotOpaqueSurfaceTests
	{
		// The corpus floor: after modelling the quest index (#391) NO snapshot carries opaque bytes before the
		// access-window table. Ratchet - only ever lower this, never raise it.
		private const long UnknownBeforeCeiling = 0;

		private readonly ITestOutputHelper _o;

		public SnapshotOpaqueSurfaceTests(ITestOutputHelper o)
		{
			_o = o;
		}

		// The quest index must be IDENTIFIED (not left opaque) in every snapshot that holds quests. This is the direct
		// regression guard for the #391 decode: it fails the moment a journal shape stops being recognised.
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

		// The opaque byte count before the access-window table must not GROW. It is 0 today (the quest index is fully
		// modelled); any increase means a shape stopped being recognised and its bytes fell into the opaque blob.
		[Fact]
		public void OpaqueSurface_BeforeAccessWindowTable_DoesNotGrow()
		{
			long unknownBefore = 0, totalTails = 0;
			var worst = ("", 0);

			foreach (var (source, sub) in SubClasses())
			{
				var n = sub.UnknownBeforeAccessWindowTable.Length;
				unknownBefore += n;
				totalTails += sub.OriginalData.Length;
				if (n > worst.Item2)
					worst = (source, n);
			}

			_o.WriteLine($"UnknownBeforeAccessWindowTable total={unknownBefore} B of {totalTails} B tails " +
			             $"({(totalTails == 0 ? 0 : 100.0 * unknownBefore / totalTails):F3}%); worst={worst.Item2} B ({worst.Item1})");
			Assert.True(unknownBefore <= UnknownBeforeCeiling,
				$"opaque pre-access-window bytes GREW to {unknownBefore} (ceiling {UnknownBeforeCeiling}); " +
				$"a snapshot shape stopped being modelled - worst offender {worst.Item1} with {worst.Item2} B");
		}

		private static IEnumerable<(string Source, FullCharSnapshotSubClass SubClass)> SubClasses()
		{
			foreach (var (source, body) in CorpusExtract.OfType((uint)GameServer0X2000Opcodes.FullPlayerCharacterIIR))
			{
				var declared = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0, 4));
				if (declared + 4 > body.Length)
					continue; // truncated capture - the TCP-reassembly gap on compressed downloads

				var stream = new FaolanStream(body[4..]);
				stream.ReadUInt32();
				stream.ReadUInt32();
				stream.ReadUInt32();
				yield return (source, FullCharSnapshotCodec.ParseInventoryPacketBody(stream).SubClass);
			}
		}
	}
}
