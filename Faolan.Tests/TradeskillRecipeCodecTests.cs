using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;
using Faolan.Importer.Extractors;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The crafting recipe-book list (TradeskillReplyIIR_c 0x9062D559) the server sends when the client opens the
	// Recipes window. Grammar decoded from AgeOfConan.exe (docs/subsystems/tradeskill-recipes.md) and gated byte-exact against
	// NecroL80_..._open-crrafting-receipies frame 2926 (603 recipes): parsing the captured reply into Recipe records
	// and rebuilding it via the codec must reproduce the wire body exactly.
	public class TradeskillRecipeCodecTests
	{
		private static readonly byte[] ReplySubOpBe = [0x90, 0x62, 0xD5, 0x59];

		[Fact]
		public void BuildReplyBody_EmptyCatalog_IsAValidEmptyList()
		{
			// The correct reply for a character with no learned recipes: env=0, version=1, type=3, count=0x3F1.
			var body = TradeskillRecipeCodec.BuildReplyBody([]);
			Assert.Equal("00" + "00000001" + "03" + "000003f1", Convert.ToHexStringLower(body));
		}

		[Fact]
		public void DbProjection_ThroughColumnsAndBack_ReproducesTheBook_ByteExact()
		{
			// The end-to-end DB-path guard: project each decoded record into its Recipe row + RecipeItem children (as
			// the importer does) with each element's key+hash on the referenced Items row, then ToRecord it back (as
			// the live server does) and rebuild. Proves the column schema loses nothing through the DB representation.
			var reply = Reply();
			var sub = IndexOf(reply.Bytes, ReplySubOpBe);
			var capturedBody = reply.Bytes.AsSpan(sub + 12).ToArray();
			var records = TradeskillRecipeCodec.ParseReplyBody(capturedBody);

			var rows = records.Select(ToRowLikeImporter).ToList();
			var roundTripped = rows.Select(TradeskillRecipeCodec.ToRecord).ToList();
			var rebuilt = TradeskillRecipeCodec.BuildReplyBody(roundTripped);

			Assert.Equal(Convert.ToHexStringLower(capturedBody), Convert.ToHexStringLower(rebuilt));
		}

		[Fact]
		public void ParseReplyBody_DecodesRecipeFieldsAndElements()
		{
			var reply = Reply();
			var sub = IndexOf(reply.Bytes, ReplySubOpBe);
			var records = TradeskillRecipeCodec.ParseReplyBody(reply.Bytes.AsSpan(sub + 12).ToArray());

			// Recipe 9 (the first record) = 3 ingredients + 1 result (validated against the capture bytes).
			var recipe9 = records.First(r => r.Id == 9);
			Assert.Equal(3, recipe9.Ingredients.Count);
			Assert.Single(recipe9.Results);
			Assert.Equal(0x3cd8d7u, recipe9.Ingredients[0].ItemId); // first ingredient item id
			Assert.Equal(0xa8d92184u, recipe9.Ingredients[0].Key1); // its DecryptionKey word 1
			Assert.Equal(0x265c8u, recipe9.Results[0].ItemId); // the produced item id

			// Decoded per-recipe columns for recipe 9 (from the capture).
			Assert.Equal(0xf4241u, recipe9.RdbType);
			Assert.Equal([0x4e5e8u], recipe9.RefIds);

			// Every recipe decodes into a non-empty element set (no over/under-run of the record body).
			foreach (var rec in records)
				Assert.True(rec.Ingredients.Count + rec.Results.Count > 0);
		}

		[Fact]
		public void ParseThenBuild_ReproducesTheCapturedRecipeBook_ByteExact()
		{
			var reply = Reply();

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

		// The one captured recipe catalog, from the committed corpus extract. Both tests below used to `return`
		// silently when the capture was absent, so they asserted NOTHING on a checkout without the capture repo.
		private static CapturedPacket Reply()
		{
			var entry = CorpusExtract.PacketsOfType((uint)GameServer0X2000Opcodes.TradeskillReplyIIR).Single();
			return new CapturedPacket(0, true, entry.Opcode, entry.Iir, entry.Full);
		}

		// Mirrors CaptureRecipeImporter.BuildRow + item harvest, in-memory: a Recipe row whose RecipeItems each carry
		// the referenced Item template (with the element's TEA key + hash), so ToRecord can read them back.
		private static Recipe ToRowLikeImporter(TradeskillRecipeCodec.RecipeRecord r)
		{
			var row = new Recipe
			{
				Id = r.Id,
				Header1 = r.Header1,
				RdbType = r.RdbType,
				MidFlag = r.MidFlag,
				Tail = r.Tail,
				TrailingFlag = r.TrailingFlag,
				RefId0 = r.RefIds[0],
				RefId1 = r.RefIds.Count > 1
					? r.RefIds[1]
					: null
			};
			Add(row, r.Ingredients, RecipeItemRole.Ingredient);
			Add(row, r.Results, RecipeItemRole.Result);
			return row;

			static void Add(Recipe row, IReadOnlyList<TradeskillRecipeCodec.RecipeElement> els, RecipeItemRole role)
			{
				for (var i = 0; i < els.Count; i++)
				{
					var e = els[i];
					row.Items.Add(new RecipeItem
					{
						Role = role,
						Position = i,
						ItemId = e.ItemId,
						Flag = e.Flag,
						Trailing = e.Trailing,
						Item = new Item
						{
							Id = e.ItemId,
							Hash = e.Hash,
							Key = new ProtoDecryptionKey
							{
								Key1 = e.Key1,
								Key2 = e.Key2,
								Key3 = e.Key3,
								Key4 = e.Key4
							}
						}
					});
				}
			}
		}
	}
}
