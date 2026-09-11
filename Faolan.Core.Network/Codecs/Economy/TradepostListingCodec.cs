using System;
using System.Collections.Generic;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Economy
{
	/// <summary>
	///     The <c>DispatchTradepostSearchResults</c> body - the auction-house listing grid.
	/// </summary>
	/// <remarks>
	///     Decoded from the NecroL80 auctionhouse capture (tools/capture/decode_tradepost.py), validated byte-exact on
	///     pkt#1028; docs/subsystems/tradepost-protocol.md. The body is <c>[head:u32][ row ]*</c>, and per row, all
	///     integers big-endian:
	///     <code>
	///     [saleId:u64][handle:u32][nameLen:u16][seller ascii][price:u64][qty:u32][itemId:u32]
	///     [instanceField:u64][itemHandle:u32][entryLen:u32][ProtoInventoryGameEntry]
	///     </code>
	///     The trailing entry is the same <c>ProtoInventoryGameEntry</c> the FullChar snapshot, shop and add-item use:
	///     <c>ItemId</c> is the saleId, the stats are {0: flags, 26: qty, 258: price, 265: timestamp}, and
	///     <c>item_data</c> is the listed instance's <c>ProtoAcgItem</c> (its own Data1..4 quad plus template). One entry
	///     codec therefore serves the snapshot, the shop window, the live add, the AH grid and the mail attachment.
	/// </remarks>
	public static class TradepostListingCodec
	{
		public const uint Method = (uint)TradepostInterfaceMethods.DispatchTradepostSearchResults;

		// The flag word (stat 0) every captured listing carries; the default for a freshly created listing.
		public const uint DefaultEntryFlags = 0x421;

		// Entry stat ids carried per listing (decoded from the capture; the 0 and 265 semantics are still loose and are
		// preserved). These same stat ids drive the bank "listed" overlay.
		public const uint StatFlags = 0;
		public const uint StatPrice = 258;
		public const uint StatQuantity = 26;
		public const uint StatTimestamp = 265;

		// Builds the full body from DB listing rows. `head` is the shared list count - the same framing GuildSearchResult,
		// TradeskillRecipeCodec and ResurrectionSpotsBody use - rather than an echo of the search query's category. The
		// captures settle it: an empty result is a 4-byte body carrying the empty frame and nothing else, and head tracks the
		// row count all the way up (a 78-row grid frames as 79).
		//
		// Listings are emitted in the order given - the client renders them as-is, so sort by price ascending at the call
		// site to match retail.
		//
		// `mintHandles` optionally overrides the per-row display handles (Handle and ItemHandle) by row index. The client
		// keys the search grid by the per-row dynel handle, so rows that share one handle collapse to a single visible row,
		// and stored listings all carry the same seeded handle (or 0 for freshly sold ones). Live search supplies a minter
		// that gives each row a unique handle; the buy path keys on SaleId rather than the handle, so these are pure
		// session-volatile display ids. Left null for the byte-exact golden replay tests.
		public static byte[] Build(uint head, IEnumerable<TradepostListing> listings, Func<int, (uint Handle, uint ItemHandle)>? mintHandles = null)
		{
			return Build(TradepostListings.FromRows(head, listings, mintHandles));
		}

		// Canonical: the only method here that writes bytes.
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

	/// <summary>One row of the AH grid.</summary>
	public sealed record ParsedRow(ulong SaleId, uint Handle, string Seller, ulong Price, uint Quantity, uint ItemId, ulong InstanceField, uint ItemHandle, ProtoInventoryGameEntry Entry);

	/// <summary>The decoded AH search grid: the list <c>Head</c> plus its rows.</summary>
	public sealed record ParsedResults(uint Head, IReadOnlyList<ParsedRow> Rows);

	/// <summary>The grid forms, as value factories: a row-to-value mapping.</summary>
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

		// The listed item as the wire ProtoInventoryGameEntry (the same shape SnapshotItems emits, with the AH-specific stat
		// set). The listed instance's TEA key is per-template and lives in Items, reached through the TemplateItem nav (FK on
		// Template), so Include it before building.
		//
		// `Level` comes off that same joined template rather than from a per-listing copy: the level is strictly
		// per-template, since across 8,644 captured inventory sightings not one template carries two different levels.
		// `ItemHash` stays on the row and is not `Items.Hash` - it is the captured wire value, and an AH-delivered item sends
		// 0.
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
					Level = listing.TemplateItem.Level,
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
