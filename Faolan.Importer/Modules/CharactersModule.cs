using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;

namespace Faolan.Importer.Modules
{
	// Captured characters (incl. other players) -> Characters + child tables. Depends on items (worn
	// equipment references item rows). Wraps CaptureCharacterImporter, which is insert-only today (existing
	// names are skipped, never overwritten) - hence Insert is the natural mode.
	// TODO(importer step 2a): Upsert support; Overwrite only behind --force (cascades to 12 child tables).
	public sealed class CharactersModule : IImportModule
	{
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => [nameof(ItemsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert
		};

		public string Description => "Captured characters (incl. other players) and their bodies/gear.";
		public string Name => "characters";

		// Fresh/tutorial captures (level <= 5) are skipped: they carry no trained feats and only a base ability
		// scaffold, so they add nothing to the reference set. The kept chars are the leveled retail captures.
		private const uint MinLevel = 5;

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (seen, added, skipped) = await CaptureCharacterImporter.ImportAsync(ctx.Db, ctx.Captures.Select(c => c.Packets), MinLevel);
			return new ImportResult($"{seen} captured, {added} added, {skipped} already in DB or below level {MinLevel}.");
		}
	}
}
