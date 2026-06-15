using System;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Packets;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact construct + round-trip for the DispatchTradepostSearchResults grid against the necrol80
	// auctionhouse capture (pkt#1028: seller "Jocko", item 0x755D, price 1,000,000). Proves a DB-backed listing
	// row rebuilds the captured bytes (construct-don't-replay) and that Parse is its faithful inverse.
	public class TradepostListingCodecTests
	{
		// The captured single-row body (bytes after the 0x2005 opcode) - same blob the PoC replay used.
		private const string CapturedBody =
			"000007e220d82b60902c95450c0457f300054a6f636b6f00000000000f4240000000010000755d000000000000000015" +
			"da16700000008008c5aab28189ec8aec201205080010a1081204081a1001120708820210c0843d12090889021085aa8c" +
			"d1061a530a140de5971bd61556c078261d9280fde2255a5c7bf112140de5971bd61556c078261d9280fde2255a5c7bf1" +
			"1a140de5971bd61556c078261d9280fde2255a5c7bf125970a3e002d970a3e0038014500000000";

		private static TradepostListing JockoListing() => new()
		{
			SaleId = 0x20d82b60902c9545,
			Handle = 0x0c0457f3,
			SellerName = "Jocko",
			Price = 1_000_000,
			Quantity = 1,
			ItemId = 0x755d,
			InstanceField = 0,
			ItemHandle = 0x15da1670,
			EntryFlags = 0x421,
			Timestamp = 0x6a231505,
			Data1 = 0xd61b97e5,
			Data2 = 0x2678c056,
			Data3 = 0xe2fd8092,
			Data4 = 0xf17b5c5a,
			Template = 0x3e0a97,
			ItemLevel = 1,
			ItemHash = 0
		};

		[Fact]
		public void Build_MatchesCapturedRow()
		{
			var body = TradepostListingCodec.Build(0x7e2, [JockoListing()]);

			Assert.Equal(CapturedBody, Convert.ToHexStringLower(body));
		}

		[Fact]
		public void Parse_DecodesTheCapturedRow()
		{
			var results = TradepostListingCodec.Parse(Convert.FromHexString(CapturedBody));

			Assert.Equal(0x7e2u, results.Head);
			var row = Assert.Single(results.Rows);
			Assert.Equal(0x20d82b60902c9545u, row.SaleId);
			Assert.Equal("Jocko", row.Seller);
			Assert.Equal(1_000_000u, row.Price);
			Assert.Equal(1u, row.Quantity);
			Assert.Equal(0x755du, row.ItemId);
			Assert.Equal(0x15da1670u, row.ItemHandle);
			// The entry's identity = the listed instance (saleId as ItemId, the same quad in all three keys).
			Assert.Equal(0x20d82b60902c9545u, row.Entry.ItemId);
			Assert.Equal(0xd61b97e5u, row.Entry.ItemData.Key1.Key1);
			Assert.Equal(0x3e0a97u, row.Entry.ItemData.Template1);
		}

		[Fact]
		public void Build_WithMintHandles_GivesEachRowAUniqueHandle()
		{
			// #212: multiple listings of one item all carry the same stored handle (or 0 for fresh sells); the
			// client collapses rows sharing a handle, so the live search path mints a unique handle per row.
			var listings = Enumerable.Range(0, 5)
				.Select(_ => JockoListing()) // all identical => identical stored Handle/ItemHandle
				.ToList();

			var body = TradepostListingCodec.Build(0x7e2, listings, i => (0x0c000000u + (uint)i, 0x15000000u + (uint)i));
			var rows = TradepostListingCodec.Parse(body).Rows;

			Assert.Equal(5, rows.Count);
			Assert.Equal(5, rows.Select(r => r.Handle).Distinct().Count());
			Assert.Equal(5, rows.Select(r => r.ItemHandle).Distinct().Count());
		}

		[Fact]
		public void BuildThenParse_RoundTrips()
		{
			var listing = JockoListing();
			var body = TradepostListingCodec.Build(0x7e2, [listing]);

			var row = TradepostListingCodec.Parse(body).Rows.Single();

			Assert.Equal(listing.SaleId, row.SaleId);
			Assert.Equal(listing.Price, row.Price);
			Assert.Equal(listing.SellerName, row.Seller);
		}
	}
}
