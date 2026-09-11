using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Modules
{
	/// <summary>
	///     AH listings (TradepostListing) from the captured search results (0x2005).
	/// </summary>
	public sealed class ListingsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Auction-house listings (0x2005 search results).";
		public string Name => "listings";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var listings = ctx.Captures.SelectMany(c => CaptureListingImporter.Extract(c.Packets));
			// AH listings are realm-scoped; join the deployment's realm (ensured before any module runs).
			var realmId = await ctx.Db.Realms.OrderBy(r => r.Id).Select(r => r.Id).FirstAsync();
			var (seen, added, updated) = await CaptureListingImporter.ImportAsync(ctx.Db, listings, realmId);
			return new ImportResult($"{seen} listings, {added} new, {updated} updated.");
		}
	}
}
