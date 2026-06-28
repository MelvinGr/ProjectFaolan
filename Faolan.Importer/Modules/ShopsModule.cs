using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;

namespace Faolan.Importer.Modules
{
	// Vendor shop windows (0x2033) -> Shops. Wraps CaptureShopImporter (upsert by vendor).
	// TODO(importer step 2a): thread ctx.Mode (Insert/Overwrite) into CaptureShopImporter.
	public sealed class ShopsModule : IImportModule
	{
		public string Name => "shops";
		public string Description => "Vendor shop windows (0x2033 NPC trade lists).";
		public ImportSource Source => ImportSource.Captures;
		public IReadOnlyList<string> DependsOn => [];
		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode> { WriteMode.Insert, WriteMode.Upsert };

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var shops = ctx.Captures.SelectMany(c => CaptureShopImporter.Extract(c.Packets));
			var (seen, added, updated) = await CaptureShopImporter.ImportAsync(ctx.Db, shops);
			return new ImportResult($"{seen} vendors, {added} new, {updated} updated.");
		}
	}
}
