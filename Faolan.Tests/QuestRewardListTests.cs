using System.Collections.Generic;
using System.Linq;
using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The four reward lists inside every captured `Quest_c` journal record - the ones the codec carried as
	// "16 raw u32s, unvalidated" until the whole corpus was swept.
	//
	// Why the corpus and not a golden: the beach starter carries none of these lists, and the beach template is the
	// only journal record a golden covers. That is exactly why they went unread - the one record anybody looked at
	// was the one record with nothing in them. The gate is therefore breadth by construction.
	//
	// The decryption proof - that each element's key opens the template it names - lives in
	// `--report quest-rewards`, not here: it needs the client's RDB, which the suite must run without. What is
	// asserted here is the grammar the decode rests on, which is a property of the wire and of our reader.
	public class QuestRewardListTests
	{
		// The client's item-template catalog, rdbh variant. Every captured element names it on both halves.
		private const uint ItemTemplateCatalogType = 1000620;

		[Fact]
		public void EveryRewardElement_MatchesTheDecodedGrammar()
		{
			var elements = 0;
			var records = 0;
			var templates = new HashSet<uint>();

			foreach (var (source, record) in JournalRecords())
			{
				records++;
				foreach (var e in new[] { record.Obj1, record.Obj2, record.Obj3, record.Obj4 }.SelectMany(l => l))
				{
					elements++;
					templates.Add(e.TemplateId);

					// The questId echo. It is what ties an element to the record that carries it, and it is the
					// cheapest thing that would break first if the 16-word carve were off by a word.
					Assert.Equal(record.QuestId, e.QuestId);

					// Both halves name the item-template catalog. A type word that drifted would mean the pair is
					// not (type, id) at all.
					Assert.Equal(ItemTemplateCatalogType, e.TemplateType);
					Assert.Equal(ItemTemplateCatalogType, e.TemplateType2);

					// The ACG template1/template2 mirror - equal on every captured element, which is the same
					// "equal in the common case" the snapshot's inventory entries show.
					Assert.Equal(e.TemplateId, e.TemplateId2);
					Assert.Equal(e.Key, e.Key2);

					// A template's key is what admits it to the catalog, so an all-zero one would be a silent hole.
					Assert.False(e.Key is { Key1: 0, Key2: 0, Key3: 0, Key4: 0 }, $"{source}: template {e.TemplateId} carries no key");
				}
			}

			// The gate would go vacuous if the corpus slice ever lost these records - the failure mode a
			// derived-extract regeneration has (CorpusExtract.ExpectedCounts is the ratchet for the other half).
			// The floors are the slice's numbers, not the dumps'. `--report quest-rewards` over the real corpus
			// sees 7,347 elements / 3,741 records / 101 templates; the committed extract holds 4,989 / 2,626 / 100.
			// Writing the dumps' figure into a test is the standing trap here - it fails on a clean clone.
			Assert.True(elements > 4500, $"only {elements} reward elements over {records} journal records - the corpus slice has narrowed");
			Assert.True(templates.Count >= 100, $"only {templates.Count} distinct reward templates");
		}

		// Round-trip: a record carrying reward lists must re-emit byte-for-byte. The four lists sit in the middle of
		// the record, so a mis-sized element would desync everything after it - which is precisely what made the
		// interleaved objective region so hard to find.
		[Fact]
		public void ARecordWithRewards_ReEmitsByteExact()
		{
			var checkedRecords = 0;
			foreach (var (source, record) in JournalRecords())
			{
				if (record.Obj1.Count + record.Obj2.Count + record.Obj3.Count + record.Obj4.Count == 0)
					continue;

				var built = QuestcRecordCodec.Build(record);
				Assert.Equal(built, QuestcRecordCodec.Build(QuestcRecordCodec.Parse(built)));
				checkedRecords++;
			}

			Assert.True(checkedRecords > 700, $"only {checkedRecords} records carried a reward list");
		}

		private static IEnumerable<(string Source, QuestcRecord Record)> JournalRecords()
		{
			foreach (var (source, body) in CorpusExtract.OfType((uint)IirTypes.FullPlayerCharacterIIR))
			{
				var declared = body.ReadU32Be(0);
				if (declared + 4 > body.Length)
					continue; // truncated capture - the TCP-reassembly gap on compressed downloads

				FullPlayerCharacterIIR parts;
				try
				{
					var stream = new FaolanStream(body[4..]);
					stream.ReadUInt32();
					stream.ReadUInt32();
					stream.ReadUInt32();
					parts = SnapshotPackets.Parse(stream);
				}
				catch
				{
					continue; // a snapshot shape this codec cannot split - coverage is reported elsewhere
				}

				foreach (var record in parts.SubClass.Quests?.Records ?? [])
					yield return (source, record);
			}
		}
	}
}
