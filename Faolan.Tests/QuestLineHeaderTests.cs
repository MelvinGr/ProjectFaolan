using System.Collections.Generic;
using Faolan.Core.Database.Tables;
using Faolan.Core.Protobuf;
using Faolan.Importer.Extractors;
using Xunit;

namespace Faolan.Tests
{
	// A quest line named only by an index block (a 4CC, no header) takes its three header records from any quest on
	// it - the quest record carries the same 50300 / 52000 / 52000 trio the completed journal's area group does.
	// Reported in-client 2026-09-03: an imported level-15 character's completed tab drew "The Ancient Gateway" (15051, line 2237) under a
	// nameless "(1)" group because 2237 had a 4CC and no title.
	public class QuestLineHeaderTests
	{
		private static QuestDefinition Quest(uint id, uint line, bool withHeader = true)
		{
			var q = new QuestDefinition { QuestId = id, QuestLineId = line };
			q.TextRefs.Add(new QuestTextRef { QuestId = id, Slot = QuestTextSlot.Title, Category = 50301, RefId = id });
			if (!withHeader)
				return q;

			q.TextRefs.Add(new QuestTextRef { QuestId = id, Slot = QuestTextSlot.QuestLineTitle, Category = 50300, RefId = line, KeyText = new ProtoDecryptionKey { Key1 = 1 } });
			q.TextRefs.Add(new QuestTextRef { QuestId = id, Slot = QuestTextSlot.RegionName, Category = 52000, RefId = 2, KeyText = new ProtoDecryptionKey { Key1 = 2 } });
			q.TextRefs.Add(new QuestTextRef { QuestId = id, Slot = QuestTextSlot.MapName, Category = 52000, RefId = 2090, KeyText = new ProtoDecryptionKey { Key1 = 3 } });
			return q;
		}

		[Fact]
		public void AHeaderlessLine_TakesTheHeaderOffItsQuest_AndAHeadedOneKeepsItsOwn()
		{
			var headerless = new QuestLine { QuestLineId = 2237, Magic = 843862608 };
			var headed = new QuestLine { QuestLineId = 241, Magic = 1, TitleRefId = 241, TitleCategory = 50300, MapRefId = 4010, RegionRefId = 4 };

			var lines = QuestLineExtractor.WithHeadersFromQuests([headerless, headed], [Quest(15051, 2237), Quest(10238, 241), Quest(99, 5000)]);

			var filled = Assert.Single(lines, l => l.QuestLineId == 2237);
			Assert.Equal(843862608u, filled.Magic); // the 4CC survives
			Assert.Equal((50300u, 2237u, 1u), (filled.TitleCategory, filled.TitleRefId, filled.TitleKey.Key1));
			Assert.Equal((52000u, 2u, 2u), (filled.RegionCategory, filled.RegionRefId, filled.RegionKey.Key1));
			Assert.Equal((52000u, 2090u, 3u), (filled.MapCategory, filled.MapRefId, filled.MapKey.Key1));

			var kept = Assert.Single(lines, l => l.QuestLineId == 241);
			Assert.Equal(4010u, kept.MapRefId); // the journal's own header is not replaced

			var added = Assert.Single(lines, l => l.QuestLineId == 5000); // a line no pass produced: header-only
			Assert.Equal(0u, added.Magic);
			Assert.Equal(5000u, added.TitleRefId);
		}

		[Fact]
		public void AQuestWithoutTheTrio_FillsNothing()
		{
			var lines = QuestLineExtractor.WithHeadersFromQuests([new QuestLine { QuestLineId = 7, Magic = 5 }], [Quest(1, 7, withHeader: false)]);
			Assert.Equal(0u, Assert.Single(lines).TitleRefId);
		}
	}
}
