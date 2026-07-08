using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Protobuf;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Faolan.Core.Network.Codecs;

namespace Faolan.Importer.Captures
{
	// Imports auction-house (Tradepost) listings from captured DispatchTradepostSearchResults (S->C 0x2005) packets
	// into TradepostListing rows, so AH search returns real data instead of the single-row replay fallback. The
	// rows are parsed by TradepostListingCodec (the same codec that builds them byte-exact for the wire); each
	// listing carries a concrete item INSTANCE (its own Data1..4 quad), so the identity lives on the row, not via
	// the Items template table. Mirrors CaptureShopImporter. Keyed by SaleId (the unique listing id BuyItem targets).
	public static class CaptureListingImporter
	{
		// TradepostInterface class id (the search-results sender) in little-endian wire order - the discriminator
		// that tells a Tradepost 0x2005 apart from the colliding GameCharInterface DispatchUpdateCash (also 0x2005).
		private static readonly byte[] TradepostInterfaceLe = [0xB5, 0xD5, 0xC2, 0xF2]; // 0xF2C2D5B5

		public static List<TradepostListing> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var listings = new List<TradepostListing>();
			foreach (var p in packets)
			{
				if (!p.IsDownload || p.Opcode != 0x2005 || p.Bytes.Length < 16)
					continue;

				var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(p.Bytes.AsSpan(8, 4));
				var bodyStart = 12 + headerLen;
				if (headerLen <= 0 || bodyStart >= p.Bytes.Length)
					continue;

				// Disambiguate from DispatchUpdateCash: the search-results endpoints (header region only) name the
				// TradepostInterface class. A wallet update never references it.
				if (!ContainsSequence(p.Bytes.AsSpan(12, headerLen), TradepostInterfaceLe))
					continue;

				var body = p.Bytes[bodyStart..];

				TradepostListingCodec.ParsedResults results;
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

		// Ensures each listed item exists as an Items TEMPLATE so a bought item can persist (CharacterInventory.ItemId
		// FK) and be re-rendered on relog. The AH listing carries the per-template identity quad + level (verified
		// identical across a template's listings) but NO hash (the search-results ProtoAcgItem.Hash is 0), so
		// these are by-template / Hash-0 items: they render via the live add-item path (DispatchAddItem), not the
		// hash-gated world-entry snapshot. Skips templates we already have (real harvested items win). Returns
		// the number of new Items rows seeded.
		public static async Task<int> HarvestItems(IDatabaseContext db, IEnumerable<TradepostListing> listings)
		{
			var merged = Merge(listings);
			var harvested = 0;

			// Seed a row for every id the listings reference: the instance Template (the buy/render identity) AND the
			// search-key ItemId (the FK target, #257). They're usually equal but can differ; both must exist as Items
			// rows. For each id, pick a representative listing - one whose Template matches if any (carries the real
			// instance identity), else one whose ItemId matches (a catalog placeholder).
			var neededIds = merged.Select(l => l.Template).Where(t => t != 0).Concat(merged.Select(l => l.ItemId).Where(i => i != 0)).Distinct();
			foreach (var tmpl in neededIds)
			{
				if (await db.Items.AnyAsync(i => i.Id == tmpl))
					continue;

				var src = merged.FirstOrDefault(l => l.Template == tmpl) ?? merged.First(l => l.ItemId == tmpl);
				db.Items.Add(new Item
				{
					Id = tmpl,
					Name = "",
					Level = (byte)Math.Min(src.ItemLevel, byte.MaxValue),
					Type = ItemType.Normal,
					Hash = src.ItemHash, // 0 for AH listings - rendered by-template via the live add, not the snapshot
					Data0 = 0,
					Data1 = src.Data1,
					Data2 = src.Data2,
					Data3 = src.Data3,
					Data4 = src.Data4
				});
				harvested++;
			}

			if (harvested > 0)
				await db.SaveChangesAsync();
			return harvested;
		}

		public static async Task<(int Seen, int Added, int Updated)> ImportAsync(IDatabaseContext db, IEnumerable<TradepostListing> listings, ILogger? logger = null)
		{
			var added = 0;
			var updated = 0;
			var merged = Merge(listings);

			// Seed the item TEMPLATES first (#257): TradepostListing.ItemId is an FK into Items (OnDelete Restrict), so
			// the listing save below throws "FOREIGN KEY constraint failed" if the filed catalog item has no Items row
			// yet. HarvestItems used to run AFTER the save (too late) and only seeded by Template - a different field
			// from the FK target ItemId - so a listing whose ItemId lacked a row crashed the whole importer. Now it runs
			// here, before the save, and seeds rows for BOTH the search-key ItemId and the instance Template.
			var harvested = await HarvestItems(db, merged);

			foreach (var e in merged)
			{
				var existing = await db.TradepostListings.FirstOrDefaultAsync(l => l.SaleId == e.SaleId);
				if (existing == null)
				{
					db.TradepostListings.Add(e);
					added++;
				}
				else
				{
					existing.Handle = e.Handle;
					existing.SellerName = e.SellerName;
					existing.Price = e.Price;
					existing.Quantity = e.Quantity;
					existing.ItemId = e.ItemId;
					existing.InstanceField = e.InstanceField;
					existing.ItemHandle = e.ItemHandle;
					existing.EntryFlags = e.EntryFlags;
					existing.Timestamp = e.Timestamp;
					existing.Data1 = e.Data1;
					existing.Data2 = e.Data2;
					existing.Data3 = e.Data3;
					existing.Data4 = e.Data4;
					existing.Template = e.Template;
					existing.ItemLevel = e.ItemLevel;
					existing.ItemHash = e.ItemHash;
					updated++;
				}
			}

			await db.SaveChangesAsync();

			logger?.LogInformation("Tradepost listings: {Seen} seen, {Added} added, {Updated} updated; {Harvested} item template(s) harvested.", merged.Count, added, updated, harvested);

			return (merged.Count, added, updated);
		}

		// Last capture wins for a saleId seen multiple times (re-walks of the same search just re-emit the same
		// listing with session-shifted volatile ids).
		public static List<TradepostListing> Merge(IEnumerable<TradepostListing> listings)
		{
			return listings.GroupBy(l => l.SaleId).Select(g => g.Last()).ToList();
		}

		// Maps one parsed search-results row to a TradepostListing. Inverse of TradepostListingCodec.BuildEntry, so a
		// row re-imported and rebuilt reproduces the captured bytes (proven by CaptureListingImporterTests).
		public static TradepostListing ToListing(TradepostListingCodec.ParsedRow row)
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
				Data1 = key.Key1,
				Data2 = key.Key2,
				Data3 = key.Key3,
				Data4 = key.Key4,
				Template = item.Template1,
				ItemLevel = item.Level,
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
