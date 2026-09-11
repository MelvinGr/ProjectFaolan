using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
// The codec's per-ware record and this file's per-shop one are both called ShopEntry; alias the wire one.
using WireShopEntry = Faolan.Core.Network.Codecs.Economy.ShopEntry;

namespace Faolan.Importer.Captures
{
	/// <summary>
	///     Imports NPC vendor shops from captured DispatchShopData packets into Shop/ShopItem rows (grammar:
	///     ShopContentCodec / tools/capture/decode_shopdata.py).
	/// </summary>
	/// <remarks>
	///     Shops are keyed by the vendor's NPC template id - the shop id and NPC handle are session-volatile, the
	///     template is stable (resolved from the same capture's FA015807 spawns). The wares' item identities land in the
	///     Items table via ItemHashHarvester's shop source (CaptureItemImporter runs over the same packets first), so
	///     the templates referenced here resolve. Re-importing a vendor replaces its stock wholesale.
	/// </remarks>
	public static class CaptureShopImporter
	{
		public static List<ShopEntry> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var names = ConversationExtractor.BuildSpawnNameMap(packets);
			// handle -> vendor template id: the shop is keyed by the vendor's NPC type, not its (non-unique) internal
			// name, so distinct same-named vendors do not merge and a shop never leaks onto a same-named non-vendor.
			var templates = NpcSpawnExtractor.Extract(packets).Where(e => e.Handle != 0 && e.TemplateId != 0).GroupBy(e => e.Handle).ToDictionary(g => g.Key, g => g.First().TemplateId);
			var shops = new List<ShopEntry>();
			foreach (var p in packets)
			{
				if (!p.IsDownload || p.WireOpcode != CapturedPacket.Wire(GameCharInterfaceMethods.DispatchShopData) || p.Bytes.Length < 44)
					continue;

				var headerLen = (int)p.Bytes.ReadU32Be(8);
				var bodyStart = 12 + headerLen;
				if (bodyStart >= p.Bytes.Length)
					continue;

				var body = p.Bytes[bodyStart..];
				if (!ShopDataScan.LooksLikeShopData(body))
					continue;

				ShopContent content;
				try
				{
					content = ShopContentCodec.Parse(body);
				}
				catch
				{
					continue;
				}

				var vendor = names.GetValueOrDefault(content.NpcHandle);
				var template = templates.GetValueOrDefault(content.NpcHandle);
				if (template == 0)
					continue; // no stable key without the vendor spawn (its template) in the same capture

				shops.Add(new ShopEntry(template, vendor ?? "", content.MaxSize, content.Entries));
			}

			return shops;
		}

		public static async Task<(int Seen, int Added, int Updated)> ImportAsync(IDatabaseContext db, IEnumerable<ShopEntry> shops, ILogger? logger = null)
		{
			var added = 0;
			var updated = 0;
			var merged = Merge(shops);
			foreach (var e in merged)
			{
				var shop = await db.Shops.Include(s => s.Items).FirstOrDefaultAsync(s => s.NpcTemplateId == e.TemplateId);
				if (shop == null)
				{
					shop = new Shop
					{
						NpcTemplateId = e.TemplateId,
						VendorName = e.VendorName
					};
					db.Shops.Add(shop);
					added++;
				}
				else
				{
					shop.VendorName = e.VendorName; // refresh the display name
					if (shop.Items != null)
						db.ShopItems.RemoveRange(shop.Items);
					updated++;
				}

				shop.MaxSize = e.MaxSize;
				shop.Items = e
					.Items.Select(i => new ShopItem
					{
						Pos = i.Pos,
						ItemTemplateId = i.Template,
						Count = i.Count,
						Stat356 = i.Stat356,
						TailLevel = i.TailLevel
					})
					.ToList();

				logger?.LogInformation("Shop \"{Vendor}\": {Count} wares (maxSize {Max}).", e.VendorName, e.Items.Count, e.MaxSize);
			}

			await db.SaveChangesAsync();
			return (merged.Count, added, updated);
		}

		// Last capture wins for a vendor seen multiple times (stock is the same in practice; sessions only
		// shift the volatile ids).
		public static List<ShopEntry> Merge(IEnumerable<ShopEntry> entries)
		{
			return entries.GroupBy(e => e.TemplateId).Select(g => g.Last()).ToList();
		}

		public sealed record ShopEntry(uint TemplateId, string VendorName, uint MaxSize, IReadOnlyList<WireShopEntry> Items);
	}
}
