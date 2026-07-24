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
		// Depends on `items`: every ware's ProtoAcgItem identity (quad + template + hash) is harvested into Items by
		// the items module (ItemHashHarvester's shop source reads the same 0x2033 packets). Shops must run AFTER that
		// so the ShopItem -> Items FK resolves; otherwise a `--only shops` run leaves the wares orphaned and the shop
		// window renders empty (the builder skips slots whose template has no Items row).
		// NpcsModule too (#244): the shop FK-keys on NpcTemplateId, so the vendor's NPC TYPE must be imported first.
		public IReadOnlyList<string> DependsOn => [nameof(ItemsModule), nameof(NpcsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Vendor shop windows (0x2033 NPC trade lists).";
		public string Name => "shops";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var shops = ctx.Captures.SelectMany(c => CaptureShopImporter.Extract(c.Packets));
			var (seen, added, updated) = await CaptureShopImporter.ImportAsync(ctx.Db, shops);
			return new ImportResult($"{seen} vendors, {added} new, {updated} updated.");
		}
	}
}
