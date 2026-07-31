using System;
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
	// The whole-corpus gate for the FULL snapshot split (prefix + root + tails) on PROGRESSED journals - Gitea #384.
	//
	// Every other snapshot golden in this suite is pinned on the two level-1 Strie beach assets, and the beach
	// journal is the degenerate case: exactly ONE held quest. With one quest, "the objective region follows the
	// record" and "the objective region follows the list" are the same offset, so a reader that treats the @609
	// objective region as a separate TOP-LEVEL region parses the beach fine and desyncs on the second record of
	// any real journal - which is what happened ("Quest_c record: marker 0x2 != 0x412" on every level-80 capture).
	//
	// This reads the tshark corpus directly (as ProtocolCitationTests/QuestTests do) so it covers the L23/L24/L80
	// journals without committing a 600 KB binary asset. Same lesson as [[list_frame_0x3f1_count_word]]: a grammar
	// pinned only by the single-element case is not pinned.
	public class ProgressedJournalSnapshotTests
	{
		private readonly ITestOutputHelper _output;

		public ProgressedJournalSnapshotTests(ITestOutputHelper output)
		{
			_output = output;
		}

		// The TAILS must survive the typed model byte-for-byte on every captured snapshot - the #384 regression
		// surface. (The protobuf ROOT does not yet re-encode byte-exactly on characters that carry character_tag:
		// protobuf-net writes that repeated field UNPACKED while retail packs it. Separate defect, tracked on its
		// own issue; it is why this asserts on SubClass rather than the whole body.)
		[Fact]
		public void EveryCapturedSnapshot_TailsRoundTrip_ByteExact()
		{
			var checkedBodies = 0;
			var progressed = 0;

			foreach (var (source, body) in SnapshotBodies())
			{
				var parts = FullPlayerCharacterCodec.Parse(body);

				// The tails as they arrived: everything after [subop][typeTag][instance][prefix][root]. Scoped to the
				// SUB-CLASS deliberately: the whole-RECORD round-trip fails on 7 of 151 captures because their
				// protobuf ROOT re-encodes exactly one byte shorter (#538) - a separate, real gap that a refactor
				// wave must not widen this gate into. Widen to FullPlayerCharacterCodec.Build(parts) when #538 lands.
				Assert.True(SnapshotPackets.SubClassBytes(body).SequenceEqual(FullCharSnapshotSubClassCodec.Build(parts.SubClass)),
					$"{source}: tails must survive the typed model byte-identically");

				checkedBodies++;
				if (parts.SubClass.Quests.Records.Count > 1)
					progressed++;
			}

			_output.WriteLine($"{checkedBodies} snapshot bodies round-tripped ({progressed} multi-quest journals)");
			Assert.True(checkedBodies > 0, "no snapshot bodies found in the capture corpus");
			// Guard the actual regression: without multi-quest journals in the set this test proves nothing beyond
			// what the beach goldens already cover.
			Assert.True(progressed > 0, "corpus has no multi-quest journal - this gate would be vacuous");
		}

		// Each journal record owns its own @609 objective region, rather than one region trailing the whole list.
		[Fact]
		public void MultiQuestJournal_InterleavesAnObjectiveRegionPerRecord()
		{
			var multi = SnapshotBodies()
				.Select(x =>
				{
					var stream = new FaolanStream(x.Body);
					stream.ReadUInt32();
					stream.ReadUInt32();
					stream.ReadUInt32();
					return (x.Source, Parts: SnapshotPackets.Parse(stream));
				})
				.FirstOrDefault(x => x.Parts.SubClass.Quests.Records.Count > 1);

			Assert.NotNull(multi.Source);
			var records = multi.Parts.SubClass.Quests.Records;
			_output.WriteLine($"{multi.Source}: {records.Count} quest records");

			Assert.All(records, r => Assert.NotNull(r.Objectives));
			// The record's quest id must match the goals its objective region carries - i.e. the regions really are
			// per-quest and not one region sliced at arbitrary boundaries.
			foreach (var record in records.Where(r => r.Objectives!.Goals.Count > 0))
				Assert.All(record.Objectives!.Goals, g => Assert.Equal(record.QuestId, g.QuestId));
		}

		// Yields the `[subop][typeTag][instance][prefix][root][tails]` body of every 0x737A6DF9 snapshot in the
		// tshark corpus.
		private static IEnumerable<(string Source, byte[] Body)> SnapshotBodies()
		{
			// Reads the committed CORPUS EXTRACT rather than the capture repo, so the suite runs from a plain clone.
			// Bodies are byte-identical to the corpus' BodyHex and keep their "<file>#<index>" provenance.
			foreach (var (source, body) in CorpusExtract.OfType((uint)GameServer0X2000Opcodes.FullPlayerCharacterIIR))
			{
				// BodyHex is the body WITH its leading u32 length; callers want everything after it.
				var declared = body.ReadU32Be(0);
				if (declared + 4 > body.Length)
					continue; // truncated capture - the TCP-reassembly gap on compressed downloads

				yield return (source, body[4..]);
			}
		}
	}
}
