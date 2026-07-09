using System;
using System.IO;
using System.Linq;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Constants;
using Faolan.Importer.Captures;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The crafting recipe-book list (TradeskillReplyIIR_c 0x9062D559) the server sends when the client opens the
	// Recipes window. Grammar decoded from AgeOfConan.exe (docs/tradeskill-recipes.md) and gated byte-exact against
	// NecroL80_..._open-crrafting-receipies frame 2926 (603 recipes): parsing the captured reply into Recipe records
	// and rebuilding it via the codec must reproduce the wire body exactly.
	public class TradeskillRecipeCodecTests
	{
		private static readonly byte[] ReplySubOpBe = [0x90, 0x62, 0xD5, 0x59];
		private static readonly string Capture = TestPaths.Capture("NecroL80_mage-necromancer_shops_open-crrafting-receipies.tshark.json");

		[Fact]
		public void BuildReplyBody_EmptyCatalog_IsAValidEmptyList()
		{
			// The correct reply for a character with no learned recipes: env=0, version=1, type=3, count=0x3F1.
			var body = TradeskillRecipeCodec.BuildReplyBody([]);
			Assert.Equal("00" + "00000001" + "03" + "000003f1", Convert.ToHexStringLower(body));
		}

		[Fact]
		public void ParseRecordItems_DecodesTheIngredientsAndResults()
		{
			if (!File.Exists(Capture))
				return;

			var packets = TsharkCaptureReader.Read(Capture);
			var reply = packets.First(p => p.IsDownload && p.SubOpcode == (uint)GameServer0X2000Opcodes.TradeskillReplyIIR);
			var sub = IndexOf(reply.Bytes, ReplySubOpBe);
			var records = TradeskillRecipeCodec.ParseReplyBody(reply.Bytes.AsSpan(sub + 12).ToArray());

			// Recipe 9 (the first record) = 3 ingredients + 1 result (validated against the capture bytes).
			var recipe9 = records.First(r => r.Id == 9);
			var items = TradeskillRecipeCodec.ParseRecordItems(recipe9.Data);
			var ingredients = items.Where(i => i.Role == TradeskillRecipeCodec.RoleIngredient).ToList();
			var results = items.Where(i => i.Role == TradeskillRecipeCodec.RoleResult).ToList();

			Assert.Equal(3, ingredients.Count);
			Assert.Single(results);
			Assert.Equal(0x3cd8d7u, ingredients[0].ItemId); // first ingredient item id
			Assert.Equal(0xa8d92184u, ingredients[0].Data1); // its DecryptionKey word 1
			Assert.Equal(0x265c8u, results[0].ItemId); // the produced item id

			// Every recipe's items parse cleanly (no over/under-run of the record body).
			foreach (var rec in records)
				Assert.NotEmpty(TradeskillRecipeCodec.ParseRecordItems(rec.Data));
		}

		[Fact]
		public void ParseThenBuild_ReproducesTheCapturedRecipeBook_ByteExact()
		{
			if (!File.Exists(Capture))
				return; // capture not present in this checkout - nothing to assert

			var packets = TsharkCaptureReader.Read(Capture);
			var reply = packets.FirstOrDefault(p => p.IsDownload && p.SubOpcode == (uint)GameServer0X2000Opcodes.TradeskillReplyIIR);
			Assert.NotEqual(default, reply);

			var sub = IndexOf(reply.Bytes, ReplySubOpBe);
			Assert.True(sub >= 0);
			var capturedBody = reply.Bytes.AsSpan(sub + 12).ToArray(); // past [subop][0xC350][handle]

			var records = TradeskillRecipeCodec.ParseReplyBody(capturedBody);
			Assert.Equal(603, records.Count);
			Assert.Equal(9u, records[0].Id); // first recipe id in the capture

			var rebuilt = TradeskillRecipeCodec.BuildReplyBody(records);
			Assert.Equal(Convert.ToHexStringLower(capturedBody), Convert.ToHexStringLower(rebuilt));
		}

		private static int IndexOf(byte[] haystack, byte[] needle)
		{
			for (var i = 0; i + needle.Length <= haystack.Length; i++)
			{
				var ok = true;
				for (var j = 0; j < needle.Length; j++)
				{
					if (haystack[i + j] != needle[j])
					{
						ok = false;
						break;
					}
				}

				if (ok)
					return i;
			}

			return -1;
		}
	}
}
