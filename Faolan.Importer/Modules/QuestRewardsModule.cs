using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;

namespace Faolan.Importer.Modules
{
	// Per-quest pick-one-of-N reward offers, harvested from the QuestRewardNotifyIIR the server sends when a
	// quest completes. See QuestRewardOptionExtractor for why this cannot come from the client's quest catalog.
	// Depends on `quests` (the QuestDefinition to hang off) and `items` (the FK, and the template the client renders).
	public sealed class QuestRewardsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(QuestsModule), nameof(ItemsModule), nameof(QuestRewardItemModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Per-quest OWN reward sets (granted + choose-one) from journal records and offers, plus line-reward quantities (#815/#893).";
		public string Name => "quest-rewards";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			// Two sources: the journal records carry a quest's whole own set (granted + choose-one), the OFFER only
			// its choose-one third. A quest seen in a record is taken from the record; an offer fills in only a quest no
			// record ever carried.
			var fromRecords = ctx.Captures.Select(c => QuestRewardOptionExtractor.ExtractFromRecords(c.Packets)).ToList();
			var own = QuestRewardOptionExtractor.Merge(fromRecords.SelectMany(r => r.Own));
			var recordQuests = own.Select(o => o.QuestId).ToHashSet();
			var offers = QuestRewardOptionExtractor.Merge(ctx.Captures.SelectMany(c => QuestRewardOptionExtractor.Extract(c.Packets)))
				.Where(o => !recordQuests.Contains(o.QuestId))
				.ToList();
			var (written, skippedQuest, skippedItem) = await QuestImportOps.ImportQuestRewardOptions(ctx.Db, [.. own, .. offers]);
			var (quantities, wireOnly, unmatched) = await QuestImportOps.ImportQuestLineRewardQuantities(ctx.Db, fromRecords.SelectMany(r => r.Line).ToList());

			var skipped = skippedQuest + skippedItem == 0
				? ""
				: $"; {skippedQuest} for a quest and {skippedItem} for an item we do not hold (dropped rather than FK-failed)";
			return new ImportResult($"{written} own-reward row(s) across {recordQuests.Count} quest(s) from records + {offers.Select(o => o.QuestId).Distinct().Count()} from offers only{skipped}; "
			                        + $"{quantities} line-reward quantity/order words filled, {wireOnly} line-reward row(s) inserted for lines the catalog lacks"
			                        + (unmatched > 0 ? $", {unmatched} wire element(s) named a template the catalog does not put on that line" : "") + ".");
		}
	}
}
