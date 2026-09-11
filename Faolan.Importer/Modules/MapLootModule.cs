using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;

namespace Faolan.Importer.Modules
{
	/// <summary>
	///     Per-map loot pool: the items a map's captured loot bags carried, which NPC-kill drops on that map roll from
	///     instead of the all-items random sample. Needs the items first so the pool can be filtered to buildable rows.
	/// </summary>
	public sealed class MapLootModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(ItemsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Per-map loot pool from captured bag contents (#273).";
		public string Name => "map-loot";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var entries = MapLootExtractor.Merge(ctx.Captures.SelectMany(c => MapLootExtractor.Extract(c.Packets)));
			var kept = await LootImportOps.ImportMapLootPool(ctx.Db, entries);
			return new ImportResult($"{kept} map-loot entries ({entries.Count} seen).");
		}
	}
}
