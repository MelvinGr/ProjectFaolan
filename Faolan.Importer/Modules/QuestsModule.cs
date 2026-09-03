using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;
using Faolan.Core.Rdb;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Modules
{
	// Quest journal-content definitions + the hand-decoded snapshot/chain seeds (beach 10238, Casilda #261/#255).
	public sealed class QuestsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(MapsModule)];

		// The capture refresh CLOBBERS the hand-authored trigger fields quest-wiring owns, so stopping here leaves the
		// quest data wrong (see IImportModule.RequiredAfter - this is the hazard that motivated it).
		public IReadOnlyList<string> RequiredAfter => [nameof(QuestWiringModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Quest definitions + areas + @609 objective goals (harvest; wiring is the quest-wiring module).";
		public string Name => "quests";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var packets = ctx.Captures.SelectMany(c => c.Packets).ToList();
			var defs = QuestDefinitionExtractor.Extract(packets);

			// Areas FIRST: QuestDefinition.QuestLineId is an FK, so every area a definition points at must already have a
			// row. The 4CCs come from the completed-quest journal (#390); the rest are placeholders.
			// ...and a line an index block named without a header takes it from its own quests' header records (the
			// three the completed journal would have carried for it) - the fix for the nameless completed-tab group.
			var areas = QuestLineExtractor.WithHeadersFromQuests(QuestLineExtractor.Extract(packets), defs);
			var areaCount = await QuestImportOps.ImportQuestLines(ctx.Db, areas, defs.Select(d => d.QuestLineId).Where(id => id != 0).Distinct().ToList());

			await QuestImportOps.ImportQuestDefinitions(ctx.Db, defs, ctx.Force);

			// The completed journal carries a title + sub-line for every quest the character ever finished - far more
			// quests than any capture session touches. Additive: never disturbs the richer full-update definitions.
			var harvest = CompletedQuestExtractor.Extract(packets);
			var (addedCompleted, filledCompleted) = await QuestImportOps.ImportCompletedQuestDefinitions(ctx.Db, harvest);

			// The index blocks and the mid-session MainQuestUpdate lists state each held line's WHOLE order, finished
			// steps or not - the completed journal only the finished ones. Fills the sequence the journal left at 0;
			// the index block of a held quest is built from it, so a multi-step line without one keeps its quests out
			// of the login snapshot (#926).
			var lineOrder = LineOrderExtractor.Extract(packets);
			foreach (var d in lineOrder.Disagreements)
				Console.WriteLine($"  ⚠️ line order: {d}");
			var (orderedSteps, orderDisagree, orderNoRow) = await QuestImportOps.ImportLineOrder(ctx.Db, lineOrder.ByQuest);
			Console.WriteLine($"Line order from the quest index blocks + MainQuestUpdate lists: {lineOrder.ByQuest.Count} steps captured ({lineOrder.OnIndexBlocks} on an index block, {lineOrder.OnlyOnUpdateLists} only on a list), {orderedSteps} filled a 0 JournalSequence, {orderDisagree} disagree with the completed journal, {orderNoRow} name a quest with no row (#926).");

			// The @609 objective goals - required before a quest can ride the login snapshot (#389).
			var goals = ObjectiveGoalExtractor.Extract(packets);
			var addedGoals = await QuestImportOps.ImportObjectiveGoals(ctx.Db, goals);

			// NB the objective-region verification does NOT run here - it runs at the end of quest-wiring, because the
			// rows it judges are not final until that overlay has re-applied the hand-authored fields this harvest
			// clobbers (goal 1448's TrailingLocalized; see IImportModule.RequiredAfter). Verifying here flagged the
			// quest against an intermediate state, which is how the beach starter stayed unverified while the SHIPPED
			// rows rebuild byte-exact (#389).

			return new ImportResult($"{defs.Count} quest definitions, {addedGoals} objective goals, {areaCount} quest areas " +
			                        $"({areas.Count} with wire data), {addedCompleted} completed-journal quests added ({filledCompleted} already known).");
		}
	}
}
