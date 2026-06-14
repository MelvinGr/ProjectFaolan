using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Packets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer
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

		// Maps one parsed search-results row to a TradepostListing. Inverse of TradepostListingCodec.BuildEntry, so a
		// row re-imported and rebuilt reproduces the captured bytes (proven by CaptureListingImporterTests).
		public static TradepostListing ToListing(TradepostListingCodec.ParsedRow row)
		{
			var item = row.Entry.ItemData ?? new Core.Network.Protobuf.ProtoAcgItem();
			var key = item.Key1 ?? new Core.Network.Protobuf.ProtoDecryptionKey();

			uint flags = 0, timestamp = 0;
			foreach (var stat in row.Entry.StatLists)
			{
				if (stat.Stat == TradepostListingCodec.StatFlags) flags = stat.Value;
				else if (stat.Stat == TradepostListingCodec.StatTimestamp) timestamp = stat.Value;
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

		// Last capture wins for a saleId seen multiple times (re-walks of the same search just re-emit the same
		// listing with session-shifted volatile ids).
		public static List<TradepostListing> Merge(IEnumerable<TradepostListing> listings)
		{
			return listings
				.GroupBy(l => l.SaleId)
				.Select(g => g.Last())
				.ToList();
		}

		public static async Task<(int Seen, int Added, int Updated)> ImportAsync(IDatabaseContext db, IEnumerable<TradepostListing> listings, ILogger logger = null)
		{
			var added = 0;
			var updated = 0;
			var merged = Merge(listings);
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
			return (merged.Count, added, updated);
		}

		private static bool ContainsSequence(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
		{
			if (needle.Length == 0 || haystack.Length < needle.Length)
				return false;
			for (var i = 0; i <= haystack.Length - needle.Length; i++)
				if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
					return true;
			return false;
		}
	}
}
