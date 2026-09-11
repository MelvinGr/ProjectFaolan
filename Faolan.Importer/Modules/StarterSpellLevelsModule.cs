using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;

namespace Faolan.Importer.Modules
{
	/// <summary>
	///     Corrects starter-spell unlock levels from the create grants (the level-1 truth), overriding the ellatha/wiki
	///     level scrape so a level-1 create yields exactly that class's f239 grant.
	/// </summary>
	/// <remarks>
	///     Runs after class-spells (which harvests the per-class grants); registration order places it after the
	///     ellatha/wiki levels.
	/// </remarks>
	public sealed class StarterSpellLevelsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(StartingSpellsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Mark create-granted spells as each captured class's starter kit + the levels later grants land at (ClassSpell).";
		public string Name => "starter-spells";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (starters, cleared) = await CaptureSpellImporter.MarkStarterSpellsAsync(ctx.Db, ctx.CreateCaptures);

			// The same table's other half: what a ding actually grants, from every capture (not just creates).
			var (access, levels) = await CaptureSpellImporter.MarkLevelGrantsAsync(ctx.Db, ctx.Captures);
			return new ImportResult($"{starters} create-granted starters marked, {cleared} stale flags cleared; {levels} capture-proven grant levels ({access} new access rows).");
		}
	}
}
