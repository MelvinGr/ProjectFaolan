using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Faolan.Core.Network.Codecs;

namespace Faolan.Importer.Captures
{
	// Imports NPC vendor shops from captured DispatchShopData (0x2033) packets into Shop/ShopItem rows
	// (grammar: ShopContentCodec / Captures/scripts/decode_shopdata.py). Shops are keyed by the vendor NPC NAME -
	// the shop id and NPC handle are session-volatile, the name is stable (resolved from the same capture's
	// FA015807 spawns, exactly like conversations). The wares' item identities land in the Items table via
	// ItemHashHarvester's shop source (CaptureItemImporter runs over the same packets first), so the
	// templates referenced here resolve. Re-importing a vendor replaces its stock wholesale.
	public static class CaptureShopImporter
	{
		public static List<ShopEntry> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var names = ConversationExtractor.BuildSpawnNameMap(packets);
			var shops = new List<ShopEntry>();
			foreach (var p in packets)
			{
				if (!p.IsDownload || p.Opcode != 0x2033 || p.Bytes.Length < 44)
					continue;

				var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(p.Bytes.AsSpan(8, 4));
				var bodyStart = 12 + headerLen;
				if (bodyStart >= p.Bytes.Length)
					continue;

				var body = p.Bytes[bodyStart..];
				if (!ShopContentCodec.LooksLikeShopData(body))
					continue;

				ShopContentCodec.ShopContent content;
				try
				{
					content = ShopContentCodec.Parse(body);
				}
				catch
				{
					continue;
				}

				var vendor = names.GetValueOrDefault(content.NpcHandle);
				if (string.IsNullOrEmpty(vendor))
					continue; // no stable key without the vendor spawn in the same capture

				shops.Add(new ShopEntry(vendor, content.MaxSize, content.Entries));
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
				var shop = await db.Shops.Include(s => s.Items).FirstOrDefaultAsync(s => s.VendorName == e.VendorName);
				if (shop == null)
				{
					shop = new Shop { VendorName = e.VendorName };
					db.Shops.Add(shop);
					added++;
				}
				else
				{
					if (shop.Items != null)
						db.ShopItems.RemoveRange(shop.Items);
					updated++;
				}

				shop.MaxSize = e.MaxSize;
				shop.Items = e
					.Items
					.Select(i => new ShopItem
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
			return entries.GroupBy(e => e.VendorName, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToList();
		}

		public sealed record ShopEntry(string VendorName, uint MaxSize, IReadOnlyList<ShopContentCodec.ShopEntry> Items);
	}
}
