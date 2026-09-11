using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Importer.Cli;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Modules
{
	// Quest wiring read from the goals' own DECRYPTED objective text. RDB-backed, so it is opt-in and
	// separate from the capture-driven `quest-wiring`.
	public sealed class RdbQuestTextModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(QuestsModule), nameof(NpcsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;
		public bool InDefaultSet => false;

		public string Description => "Quest wiring from the goals' decrypted objective text + typed target reference (#520/#521, opt-in).";
		public string Name => "rdb-quest-text";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbQuestTextImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			// The target fill runs second on purpose. It claims every remaining bare Kill goal whose trailing reference
			// is an npc_template, which makes the goal "wired" - so running it first would hide a talk/deliver shape
			// from the objective-text pass above, which is the only thing that can retype one.
			var t = await RdbQuestGoalTargetImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);

			// Every goal it did not wire is accounted for by reason - a silent skip here is indistinguishable from a
			// decode regression.
			return new ImportResult(
				$"{r.Wired} goal(s) typed from their objective text ({r.WiredWithoutNpc} of them naming an NPC we do not hold yet, " +
				$"so inert until its map is imported, {r.WiredWithoutDialogue} held but with no dialogue captured so the talk " +
				$"cannot fire yet); {r.AlreadyWired} already wired, {r.PreconditionGated} carrying a recorded rank/level precondition, " +
				$"{r.NotOurShape} not a deliver/talk shape, {r.NoNameInText} no name in the text, {r.Unreadable} unreadable, of {r.Goals} goals; " +
				$"{r.ReconciledToTheCatalogName} short name(s) reconciled to the NpcTemplate's full name (a match key must EQUAL the live name). " +
				$"Targets: {t.FromCatalog} named from the NpcTemplate the reference points at ({t.PlaceholderKept} of those a shared " +
				$"placeholder name the LDB disagrees with), {t.FromLdb} from LDB[51000] for a template we do not hold, " +
				$"{t.AlreadyWired} already wired, {t.Unresolved} with no English LDB entry, of {t.Goals} npc-referencing goals.");
		}
	}
}
