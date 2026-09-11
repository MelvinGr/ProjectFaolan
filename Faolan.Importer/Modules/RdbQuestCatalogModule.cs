using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Importer.Cli;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Modules
{
	public sealed class RdbQuestCatalogModule : IImportModule
	{
		/// <remarks>
		///     No dependency: the catalog is self-contained client data. Lines must exist before the quests that
		///     reference them and quests before their goals, and the importer orders those three passes itself.
		/// </remarks>
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Quest lines/quests/goals + their names from the client's quest catalog (RDB 1000104).";
		public string Name => "rdb-quest-catalog";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbQuestCatalogImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			return new ImportResult(
				$"lines +{r.LinesAdded} ({r.LinesNamed} named, {r.PlaceholderLines} placeholder); quests +{r.QuestsAdded} ({r.QuestsNamed} named); " +
				$"goals +{r.GoalsAdded} ({r.GoalsNamed} named); {r.Rewards} item rewards; {r.GoalsOrphaned} goals with no quest.");
		}
	}
}
