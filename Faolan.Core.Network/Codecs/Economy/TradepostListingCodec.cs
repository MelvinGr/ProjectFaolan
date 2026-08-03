using System;
using System.Collections.Generic;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Economy
{
	// Parses/builds the DispatchTradepostSearchResults (S→C 0x2005) body - the auction-house listing grid.
	// Wire grammar (decoded 2026-06-13 from the NecroL80 auctionhouse capture, tools/capture/decode_tradepost.py,
	// validated byte-exact on pkt#1028; docs/subsystems/tradepost-protocol.md):
	//   [head:u32][ row ]*
	// Per row (all integers big-endian):
	//   [saleId:u64][handle:u32][nameLen:u16][seller ascii][price:u64][qty:u32][itemId:u32]
	//   [instanceField:u64][itemHandle:u32][entryLen:u32][ProtoInventoryGameEntry]
	// The trailing entry is the SAME ProtoInventoryGameEntry the FullChar snapshot / shop / add-item use: ItemId =
	// the saleId, stats {0:flags, 26:qty, 258:price, 265:timestamp}, item_data = the listed instance's ProtoAcgItem
	// (its own Data1..4 quad + template). So one entry codec serves the snapshot, the shop window, the live add,
	// and the AH grid (and, later, the mail attachment).
	// The value used to be a self-serializing `ICodec<ParsedResults>` (#530 wave 7 retired the interface here).
	public static class TradepostListingCodec
	{
		public const uint Method = (uint)TradepostInterfaceMethods.DispatchTradepostSearchResults;

		// The flag word (stat 0) every captured listing carries; the default for a freshly-created listing.
		public const uint DefaultEntryFlags = 0x421;

		// Entry stat ids carried per listing (decoded from the capture; 0/265 semantics still loose - preserved).
		// These same stat ids drive the bank "listed" overlay (TradepostMailHandler reuses them).
		public const uint StatFlags = 0;
		public const uint StatPrice = 258;
		public const uint StatQuantity = 26;
		public const uint StatTimestamp = 265;

		// Builds the full body from DB listing rows. `head` is the shared 0x3F1 LIST COUNT - (rowCount+1) * 0x3F1, the
		// same framing GuildSearchResult/TradeskillRecipeCodec/ResurrectionSpotsBody use - NOT, as this once claimed,
		// an echo of the search query's category. It only looks like an echo because the query happens to carry 0x7E2
		// too, and because a 1-row result is 2 * 0x3F1 = 0x7E2 exactly. The captures settle it: an EMPTY result is a
		// 4-byte body carrying head 0x3F1 (= 0 rows) and nothing else, and head tracks the row count all the way up
		// (0x1375F = 79 * 0x3F1 for a 78-row grid). The call site already computes it correctly.
		//
		// Listings are emitted in the order given (the client renders them as-is - sort by price ascending at the
		// call site to match retail).
		//
		// `mintHandles` optionally overrides the per-row display handles (Handle + ItemHandle) by row index. The
		// client keys the search grid by the per-row dynel handle, so rows that share one handle COLLAPSE to a single
		// visible row (#212) - and stored listings all carry the same seeded handle (or 0 for freshly-sold ones).
		// Live search supplies a minter that gives each row a UNIQUE handle; the buy path keys on SaleId, not the
		// handle, so these are pure session-volatile display ids. Left null for the byte-exact golden replay tests.
		public static byte[] Build(uint head, IEnumerable<TradepostListing> listings, Func<int, (uint Handle, uint ItemHandle)>? mintHandles = null)
		{
			return Build(TradepostListings.FromRows(head, listings, mintHandles));
		}

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(ParsedResults value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static ParsedResults Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(TradepostListingCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static ParsedResults Read(ref SpanReader r)
		{
			var head = r.ReadU32Be();

			var rows = new List<ParsedRow>();
			while (r.Remaining >= sizeof(uint))
			{
				var saleId = r.ReadU64Be();
				var handle = r.ReadU32Be();
				var seller = r.ReadStringU16Length();
				var price = r.ReadU64Be();
				var qty = r.ReadU32Be();
				var itemId = r.ReadU32Be();
				var instanceField = r.ReadU64Be();
				var itemHandle = r.ReadU32Be();
				rows.Add(new ParsedRow(saleId, handle, seller, price, qty, itemId, instanceField, itemHandle, r.ReadProtobufU32Length<ProtoInventoryGameEntry>()));
			}

			return new ParsedResults(head, rows);
		}

		internal static void Write(FaolanStream s, ParsedResults v)
		{
			s.WriteUInt32(v.Head);
			foreach (var row in v.Rows)
			{
				s
					.WriteUInt64(row.SaleId)
					.WriteUInt32(row.Handle)
					.WriteString(row.Seller ?? "")
					.WriteUInt64(row.Price)
					.WriteUInt32(row.Quantity)
					.WriteUInt32(row.ItemId)
					.WriteUInt64(row.InstanceField)
					.WriteUInt32(row.ItemHandle)
					.WriteArrayUInt32Length(row.Entry.ToProtobuf());
			}
		}
	}

	// One row of the AH grid.
	public sealed record ParsedRow(ulong SaleId, uint Handle, string Seller, ulong Price, uint Quantity, uint ItemId, ulong InstanceField, uint ItemHandle, ProtoInventoryGameEntry Entry);

	// The decoded AH search grid: the 0x3F1 list `Head` plus its rows.
	public sealed record ParsedResults(uint Head, IReadOnlyList<ParsedRow> Rows);

	// The grid forms, as VALUE factories - row -> value mapping (#533).
	public static class TradepostListings
	{
		public static ParsedResults FromRows(uint head, IEnumerable<TradepostListing> listings, Func<int, (uint Handle, uint ItemHandle)>? mintHandles = null)
		{
			ArgumentNullException.ThrowIfNull(listings);

			var rows = new List<ParsedRow>();
			var index = 0;
			foreach (var listing in listings)
			{
				var (handle, itemHandle) = mintHandles?.Invoke(index) ?? (listing.Handle, listing.ItemHandle);
				rows.Add(new ParsedRow(listing.SaleId, handle, listing.SellerName ?? "", listing.Price, listing.Quantity, listing.ItemId, listing.InstanceField, itemHandle, Entry(listing)));
				index++;
			}

			return new ParsedResults(head, rows);
		}

		// The listed item as the wire ProtoInventoryGameEntry (the same shape SnapshotItems emits, with the
		// AH-specific stat set). The listed instance's TEA key is per-TEMPLATE and lives in Items, reached through the
		// TemplateItem nav (FK on Template) - Include it before building. The Template/Level/ItemHash tail still comes
		// off the listing row (ItemHash is the captured wire value, 0 for AH, not Items.Hash).
		public static ProtoInventoryGameEntry Entry(TradepostListing listing)
		{
			var key = (listing.TemplateItem ?? throw new InvalidOperationException(
				$"TradepostListing {listing.SaleId} (template {listing.Template}): the TemplateItem navigation is not loaded - Include it before building the row.")).Key;

			var entry = new ProtoInventoryGameEntry
			{
				ItemId = listing.SaleId,
				ItemData = new ProtoAcgItem
				{
					Key1 = key,
					Key2 = key,
					Key3 = key,
					Template1 = listing.Template,
					Template2 = listing.Template,
					Level = listing.ItemLevel,
					Hash = listing.ItemHash
				}
			};
			entry.StatLists.Add(new ProtoInventoryGameEntry.StatValue
			{
				Stat = TradepostListingCodec.StatFlags,
				Value = listing.EntryFlags
			});
			entry.StatLists.Add(new ProtoInventoryGameEntry.StatValue
			{
				Stat = TradepostListingCodec.StatQuantity,
				Value = listing.Quantity
			});
			entry.StatLists.Add(new ProtoInventoryGameEntry.StatValue
			{
				Stat = TradepostListingCodec.StatPrice,
				Value = (uint)listing.Price
			});
			entry.StatLists.Add(new ProtoInventoryGameEntry.StatValue
			{
				Stat = TradepostListingCodec.StatTimestamp,
				Value = listing.Timestamp
			});
			return entry;
		}

	}
}
