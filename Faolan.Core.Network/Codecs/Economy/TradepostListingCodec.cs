using System;
using System.Collections.Generic;
using System.IO;
using Faolan.Core.Database.Tables;
using Faolan.Core.Protobuf;
using ProtoBuf;

namespace Faolan.Core.Network.Codecs.Economy
{
	// Parses/builds the DispatchTradepostSearchResults (S→C 0x2005) body - the auction-house listing grid.
	// Wire grammar (decoded 2026-06-13 from the necrol80 auctionhouse capture, tools/capture/decode_tradepost.py,
	// validated byte-exact on pkt#1028; docs/subsystems/tradepost-protocol.md):
	//   [head:u32][ row ]*
	// Per row (all integers big-endian):
	//   [saleId:u64][handle:u32][nameLen:u16][seller ascii][price:u64][qty:u32][itemId:u32]
	//   [instanceField:u64][itemHandle:u32][entryLen:u32][ProtoInventoryGameEntry]
	// The trailing entry is the SAME ProtoInventoryGameEntry the FullChar snapshot / shop / add-item use: ItemId =
	// the saleId, stats {0:flags, 26:qty, 258:price, 265:timestamp}, item_data = the listed instance's ProtoAcgItem
	// (its own Data1..4 quad + template). So one entry codec serves the snapshot, the shop window, the live add,
	// and the AH grid (and, later, the mail attachment).
	public static class TradepostListingCodec
	{
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
			var rows = new List<ParsedRow>();
			var index = 0;
			foreach (var listing in listings)
			{
				var (handle, itemHandle) = mintHandles?.Invoke(index) ?? (listing.Handle, listing.ItemHandle);
				rows.Add(new ParsedRow(listing.SaleId, handle, listing.SellerName ?? "", listing.Price, listing.Quantity, listing.ItemId, listing.InstanceField, itemHandle, BuildEntry(listing)));
				index++;
			}

			return new ParsedResults(head, rows).ToArray();
		}

		// The listed item as the wire ProtoInventoryGameEntry (the same shape SnapshotItemBuilder emits, with the
		// AH-specific stat set). The listed instance's TEA key is per-TEMPLATE and lives in Items, reached through the
		// TemplateItem nav (FK on Template) - Include it before building. The Template/Level/ItemHash tail still comes
		// off the listing row (ItemHash is the captured wire value, 0 for AH, not Items.Hash).
		public static ProtoInventoryGameEntry BuildEntry(TradepostListing listing)
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
				Stat = StatFlags,
				Value = listing.EntryFlags
			});
			entry.StatLists.Add(new ProtoInventoryGameEntry.StatValue
			{
				Stat = StatQuantity,
				Value = listing.Quantity
			});
			entry.StatLists.Add(new ProtoInventoryGameEntry.StatValue
			{
				Stat = StatPrice,
				Value = (uint)listing.Price
			});
			entry.StatLists.Add(new ProtoInventoryGameEntry.StatValue
			{
				Stat = StatTimestamp,
				Value = listing.Timestamp
			});
			return entry;
		}

		// Decodes a captured/served body back into structured rows (the entry boundary falls out of the u32 length
		// prefix, so no prefix-sniffing is needed - unlike the Python dissector).
		public static ParsedResults Parse(byte[] body)
		{
			return ParsedResults.Read(new FaolanStream(body));
		}

		public sealed record ParsedRow(ulong SaleId, uint Handle, string Seller, ulong Price, uint Quantity, uint ItemId, ulong InstanceField, uint ItemHandle, ProtoInventoryGameEntry Entry);

		// The decoded AH search grid as a self-serializing codec (ICodec): Write frames [head][row*] (each row's
		// trailing ProtoInventoryGameEntry serialized length-prefixed); Read recovers the rows.
		public sealed record ParsedResults(uint Head, IReadOnlyList<ParsedRow> Rows) : ICodec<ParsedResults>
		{
			public static ParsedResults Read(FaolanStream s)
			{
				var head = s.ReadUInt32();

				var rows = new List<ParsedRow>();
				while (s.Position + 4 <= s.Length)
				{
					var saleId = s.ReadUInt64();
					var handle = s.ReadUInt32();
					var seller = s.ReadString();
					var price = s.ReadUInt64();
					var qty = s.ReadUInt32();
					var itemId = s.ReadUInt32();
					var instanceField = s.ReadUInt64();
					var itemHandle = s.ReadUInt32();
					var entryBytes = s.ReadArrayUInt32Length();

					using var ms = new MemoryStream(entryBytes);
					var entry = Serializer.Deserialize<ProtoInventoryGameEntry>(ms);
					rows.Add(new ParsedRow(saleId, handle, seller, price, qty, itemId, instanceField, itemHandle, entry));
				}

				return new ParsedResults(head, rows);
			}

			public void Write(FaolanStream s)
			{
				s.WriteUInt32(Head);
				foreach (var row in Rows)
				{
					byte[] entryBytes;
					using (var ms = new MemoryStream())
					{
						Serializer.Serialize(ms, row.Entry);
						entryBytes = ms.ToArray();
					}

					s
						.WriteUInt64(row.SaleId)
						.WriteUInt32(row.Handle)
						.WriteString(row.Seller ?? "")
						.WriteUInt64(row.Price)
						.WriteUInt32(row.Quantity)
						.WriteUInt32(row.ItemId)
						.WriteUInt64(row.InstanceField)
						.WriteUInt32(row.ItemHandle)
						.WriteArrayUInt32Length(entryBytes);
				}
			}
		}
	}
}
