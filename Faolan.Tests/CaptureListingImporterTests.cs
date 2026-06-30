using System;
using System.Linq;
using Faolan.Core.Network.Packets;
using Faolan.Importer.Captures;
using Xunit;

namespace Faolan.Tests
{
	// The AH listings importer maps a captured DispatchTradepostSearchResults row to a TradepostListing whose
	// fields rebuild the captured bytes byte-exact (construct-don't-replay). Validates CaptureListingImporter.ToListing
	// is a faithful inverse of TradepostListingCodec.BuildEntry against the necrol80 auctionhouse capture (#1029:
	// seller "Jocko", item 0x755D, price 1,000,000).
	public class CaptureListingImporterTests
	{
		// The captured single-row search-results body (bytes after the 0x2005 opcode).
		private const string CapturedBody =
			"000007e220d82b60902c95450c0457f300054a6f636b6f00000000000f4240000000010000755d000000000000000015" +
			"da16700000008008c5aab28189ec8aec201205080010a1081204081a1001120708820210c0843d12090889021085aa8c" +
			"d1061a530a140de5971bd61556c078261d9280fde2255a5c7bf112140de5971bd61556c078261d9280fde2255a5c7bf1" +
			"1a140de5971bd61556c078261d9280fde2255a5c7bf125970a3e002d970a3e0038014500000000";

		[Fact]
		public void ToListing_RebuildsTheCapturedRowByteExact()
		{
			var parsed = TradepostListingCodec.Parse(Convert.FromHexString(CapturedBody));

			var listings = parsed.Rows.Select(CaptureListingImporter.ToListing).ToList();
			var rebuilt = TradepostListingCodec.Build(parsed.Head, listings);

			Assert.Equal(CapturedBody, Convert.ToHexStringLower(rebuilt));
		}

		[Fact]
		public void ToListing_CapturesTheListingIdentity()
		{
			var row = TradepostListingCodec.Parse(Convert.FromHexString(CapturedBody)).Rows.Single();

			var listing = CaptureListingImporter.ToListing(row);

			Assert.Equal(0x20d82b60902c9545ul, listing.SaleId);
			Assert.Equal("Jocko", listing.SellerName);
			Assert.Equal(1_000_000ul, listing.Price);
			Assert.Equal(1u, listing.Quantity);
			Assert.Equal(0x755du, listing.ItemId);
			Assert.Equal(0x421u, listing.EntryFlags);  // entry stat 0
			Assert.Equal(0x3e0a97u, listing.Template);
		}

		[Fact]
		public void Merge_DedupesBySaleId_LastWins()
		{
			var a = CaptureListingImporter.ToListing(TradepostListingCodec.Parse(Convert.FromHexString(CapturedBody)).Rows.Single());
			var b = CaptureListingImporter.ToListing(TradepostListingCodec.Parse(Convert.FromHexString(CapturedBody)).Rows.Single());
			b.Price = 42; // same saleId, later capture wins

			var merged = CaptureListingImporter.Merge([a, b]);

			var single = Assert.Single(merged);
			Assert.Equal(42ul, single.Price);
		}
	}
}
