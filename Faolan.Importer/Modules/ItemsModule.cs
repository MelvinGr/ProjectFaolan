using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;

namespace Faolan.Importer.Modules
{
	// Item identities from inventory/snapshot packets. Wraps CaptureItemImporter (upsert by item key).
	// TODO(importer step 2a): thread ctx.Mode into CaptureItemImporter so Insert skips existing keys.
	public sealed class ItemsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Item identities from inventory/ApplySpell packets.";
		public string Name => "items";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (total, inserted) = await CaptureItemImporter.ImportAsync(ctx.Db, ctx.RawPackets);
			var excluded = await LootImportOps.ApplyRandomLootExclusions(ctx.Db); // #273: quest/resource + curated uniques
			return new ImportResult($"{total} item-candidate packets, {inserted} new, {excluded} newly excluded from random loot.");
		}
	}
}
