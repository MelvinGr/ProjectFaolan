using System;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Protobuf;
using Xunit;

namespace Faolan.Tests
{
	// Proves the decoded DispatchShopData grammar round-trips byte-exact: parse the captured trader blob
	// (the .shop PoC replay bytes) into rows, rebuild with ShopContentCodec.Build from those rows, and the
	// output must equal the capture bit-for-bit. This is what makes the DB-backed vendor construction safe.
	public class ShopContentCodecTests
	{
		[Fact]
		public void CapturedTraderBlob_RoundTrips_ByteExact()
		{
			var captured = ShopReplayData.DispatchShopDataBody;
			var parsed = ShopContentCodec.Parse(captured);

			Assert.Equal(0x1E7Eu, parsed.ShopInstance);
			Assert.Equal(80u, parsed.MaxSize);
			Assert.Equal(58, parsed.Entries.Count);

			// Rebuild from the parsed rows, resolving item identity from the entries themselves (the importer
			// stores the same quads/archetypes in Items).
			var items = parsed.Entries.Select(e => new ShopItem
			{
				Pos = e.Pos,
				ItemTemplateId = e.Template,
				Count = e.Count,
				Stat356 = e.Stat356,
				TailLevel = e.TailLevel
			});
			var byTemplate = parsed.Entries.ToDictionary(e => e.Template, e => new Item
			{
				Id = e.Template,
				Hash = e.Hash,
				Key = new ProtoDecryptionKey { Key1 = e.Data1, Key2 = e.Data2, Key3 = e.Data3, Key4 = e.Data4 }
			});

			var rebuilt = ShopContentCodec.Build(parsed.ShopInstance, parsed.NpcHandle, parsed.MaxSize, items, t => byTemplate[t]);

			Assert.Equal(Convert.ToHexStringLower(captured), Convert.ToHexStringLower(rebuilt));
		}
	}
}
