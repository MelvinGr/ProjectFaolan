using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Faolan.Importer.V2.Modules
{
	// Captured characters (incl. other players) -> Characters + child tables. Depends on items (worn
	// equipment references item rows). Wraps CaptureCharacterImporter, which is insert-only today (existing
	// names are skipped, never overwritten) - hence Insert is the natural mode.
	// TODO(importer-v2 step 2a): Upsert support; Overwrite only behind --force (cascades to 12 child tables).
	public sealed class CharactersModule : IImportModule
	{
		public string Name => "characters";
		public string Description => "Captured characters (incl. other players) and their bodies/gear.";
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => ["items"];
		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode> { WriteMode.Insert };

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (seen, added, skipped) = await CaptureCharacterImporter.ImportAsync(ctx.Db, ctx.Captures.Select(c => c.Packets));
			return new ImportResult($"{seen} captured, {added} added, {skipped} already in DB.");
		}
	}
}
