using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Captures
{
	/// <summary>
	///     Imports auction-house (Tradepost) listings from captured DispatchTradepostSearchResults (S->C 0x2005)
	///     packets into TradepostListing rows.
	/// </summary>
	/// <remarks>
	///     Rows are parsed by TradepostListingCodec, the same codec that builds them byte-exact for the wire. Each
	///     listing carries a concrete item instance (its own Data1..4 quad), so the identity lives on the row, not on
	///     the Items template table. Mirrors CaptureShopImporter. Keyed by SaleId, the unique listing id BuyItem targets.
	/// </remarks>
	public static class CaptureListingImporter
	{
		// TradepostInterface class id (the search-results sender) in little-endian wire order - the discriminator
		// that tells DispatchTradepostSearchResults apart from GameCharInterface.DispatchUpdateCash: a method is a
		// per-interface index and both are index 0x05, so they are the same two bytes on the wire.
		private static readonly byte[] TradepostInterfaceLe = [0xB5, 0xD5, 0xC2, 0xF2]; // 0xF2C2D5B5

		public static List<TradepostListing> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var listings = new List<TradepostListing>();
			foreach (var p in packets)
			{
				if (!p.IsDownload || p.WireOpcode != CapturedPacket.Wire(TradepostInterfaceMethods.DispatchTradepostSearchResults) || p.Bytes.Length < 16)
					continue;

				var headerLen = (int)p.Bytes.ReadU32Be(8);
				var bodyStart = 12 + headerLen;
				if (headerLen <= 0 || bodyStart >= p.Bytes.Length)
					continue;

				// Disambiguate from DispatchUpdateCash: the search-results endpoints (header region only) name the
				// TradepostInterface class. A wallet update never references it.
				if (!ContainsSequence(p.Bytes.AsSpan(12, headerLen), TradepostInterfaceLe))
					continue;

				var body = p.Bytes[bodyStart..];

				ParsedResults results;
				try
				{
					results = TradepostListingCodec.Parse(body);
				}
				catch
				{
					continue; // a truncated/partial download - skip rather than corrupt the import
				}

				foreach (var row in results.Rows)
					listings.Add(ToListing(row));
			}

			return listings;
		}

		/// <summary>
		///     Ensures each listed item exists as an Items template so a bought item can persist (CharacterInventory.ItemId
		///     FK) and be re-rendered on relog.
		/// </summary>
		/// <remarks>
		///     The AH listing carries the per-template identity quad and level but no hash (the search-results
		///     ProtoAcgItem.Hash is 0), so these are by-template / Hash-0 items: they render via the live add-item path
		///     (DispatchAddItem), not the hash-gated world-entry snapshot. Templates that already exist are not replaced
		///     (real harvested items win), but their level is refreshed.
		/// </remarks>
		/// <returns>The number of new Items rows seeded.</returns>
		public static async Task<int> HarvestItems(IDatabaseContext db, IEnumerable<TradepostListing> listings)
		{
			var merged = Merge(listings);
			var harvested = 0;

			// Seed a row for every id the listings reference: the instance Template (the buy/render identity) and the
			// search-key ItemId (the FK target). They are usually equal but can differ; both must exist as Items rows.
			// For each id, pick a representative listing - one whose Template matches if any (carries the real
			// instance identity), else one whose ItemId matches (a catalog placeholder).
			var neededIds = merged.Select(l => l.Template).Where(t => t != 0).Concat(merged.Select(l => l.ItemId).Where(i => i != 0)).Distinct();
			foreach (var tmpl in neededIds)
			{
				var src = merged.FirstOrDefault(l => l.Template == tmpl) ?? merged.First(l => l.ItemId == tmpl);

				// An existing row still gets its level refreshed: a template that only ever appears in a listing is not
				// reached by the inventory-entry level harvest, and an insert-only shape would leave a wrong level in
				// place through every re-import. An AH listing reports the same level as the inventory entry for every
				// template seen in both, so there is no source conflict to arbitrate. Never downgrade to 0, same rule as
				// the hash.
				if (await db.Items.FirstOrDefaultAsync(i => i.Id == tmpl) is { } existing)
				{
					if (src.TemplateItem.Level != 0)
						existing.Level = src.TemplateItem.Level;

					continue;
				}

				db.Items.Add(new Item
				{
					Id = tmpl,
					Name = "",
					Level = src.TemplateItem.Level,
					Type = ItemType.Normal,
					Hash = src.ItemHash, // 0 for AH listings - rendered by-template via the live add, not the snapshot
					Key = (ProtoDecryptionKey)src.TemplateItem.Key.Clone() // carried on the transient nav from ToListing
				});
				harvested++;
			}

			if (harvested > 0)
				await db.SaveChangesAsync();
			return harvested;
		}

		/// <param name="realmId">
		///     Scopes the imported listings to a realm: captures do not carry local realm ids, so the AH listings join the
		///     deployment's realm (the same one imported characters join). TradepostListing.RealmId FKs Realms and
		///     Postgres enforces it, so a RealmId of 0 is not acceptable.
		/// </param>
		public static async Task<(int Seen, int Added, int Updated)> ImportAsync(IDatabaseContext db, IEnumerable<TradepostListing> listings, uint realmId, ILogger? logger = null)
		{
			var added = 0;
			var updated = 0;
			var merged = Merge(listings);

			// Seed the item templates before the listing save: TradepostListing.ItemId is an FK into Items (OnDelete
			// Restrict), so the save throws "FOREIGN key constraint failed" if the filed catalog item has no Items row
			// yet. HarvestItems seeds rows for both the search-key ItemId and the instance Template.
			var harvested = await HarvestItems(db, merged);

			foreach (var e in merged)
			{
				e.TemplateItem = null!; // key already harvested into Items above; persist only the Template FK column
				e.RealmId = realmId;
				var existing = await db.TradepostListings.FirstOrDefaultAsync(l => l.RealmId == realmId && l.SaleId == e.SaleId);
				if (existing == null)
				{
					db.TradepostListings.Add(e);
					added++;
				}
				else
				{
					existing.RealmId = realmId;
					existing.Handle = e.Handle;
					existing.SellerName = e.SellerName;
					existing.Price = e.Price;
					existing.Quantity = e.Quantity;
					existing.ItemId = e.ItemId;
					existing.InstanceField = e.InstanceField;
					existing.ItemHandle = e.ItemHandle;
					existing.EntryFlags = e.EntryFlags;
					existing.Timestamp = e.Timestamp;
					existing.Template = e.Template;
					existing.ItemHash = e.ItemHash;
					updated++;
				}
			}

			await db.SaveChangesAsync();

			logger?.LogInformation("Tradepost listings: {Seen} seen, {Added} added, {Updated} updated; {Harvested} item template(s) harvested.", merged.Count, added, updated, harvested);

			return (merged.Count, added, updated);
		}

		/// <summary>
		///     Last capture wins for a saleId seen multiple times (re-walks of the same search re-emit the same listing
		///     with session-shifted volatile ids).
		/// </summary>
		public static List<TradepostListing> Merge(IEnumerable<TradepostListing> listings)
		{
			return listings.GroupBy(l => l.SaleId).Select(g => g.Last()).ToList();
		}

		/// <summary>
		///     Maps one parsed search-results row to a TradepostListing. Inverse of TradepostListings.Entry, so a row
		///     re-imported and rebuilt reproduces the captured bytes (CaptureListingImporterTests).
		/// </summary>
		public static TradepostListing ToListing(ParsedRow row)
		{
			var item = row.Entry.ItemData ?? new ProtoAcgItem();
			var key = item.Key1 ?? new ProtoDecryptionKey();

			uint flags = 0, timestamp = 0;
			foreach (var stat in row.Entry.StatLists)
			{
				if (stat.Stat == TradepostListingCodec.StatFlags)
					flags = stat.Value;
				else if (stat.Stat == TradepostListingCodec.StatTimestamp)
					timestamp = stat.Value;
			}

			return new TradepostListing
			{
				SaleId = row.SaleId,
				Handle = row.Handle,
				SellerName = row.Seller,
				Price = row.Price,
				Quantity = row.Quantity,
				ItemId = row.ItemId,
				InstanceField = row.InstanceField,
				ItemHandle = row.ItemHandle,
				EntryFlags = flags,
				Timestamp = timestamp,
				// The listed instance's TEA key lives once in Items (harvested by template). Carry it on a transient
				// TemplateItem so HarvestItems can seed Items from it; ImportAsync nulls this nav before saving the
				// listing (only the Template FK column persists - the row resolves its real Items row on reload).
				TemplateItem = new Item
				{
					Key = key,
					// The wire's `Level` rides the transient nav rather than a column on the listing: it is a
					// per-template fact, and HarvestItems (seeding/correcting `Items.Level`) is its only reader.
					Level = (byte)Math.Min(item.Level, byte.MaxValue)
				},
				Template = item.Template1,
				ItemHash = item.Hash
			};
		}

		private static bool ContainsSequence(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
		{
			if (needle.Length == 0 || haystack.Length < needle.Length)
				return false;

			for (var i = 0; i <= haystack.Length - needle.Length; i++)
			{
				if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
					return true;
			}

			return false;
		}
	}
}
