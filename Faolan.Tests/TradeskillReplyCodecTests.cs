using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Protobuf;
using Faolan.Importer.Extractors;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The crafting recipe-book list (TradeskillReplyIIR_c 0x9062D559) the server sends when the client opens the
	// Recipes window. Grammar decoded from AgeOfConan.exe (docs/subsystems/tradeskill-recipes.md) and gated byte-exact
	// against the one captured reply (603 recipes, Assets/recipe-book-reply.bin): parsing it into Recipe records and
	// rebuilding it via the codec must reproduce the wire body exactly.
	public class TradeskillReplyCodecTests
	{
		private static readonly byte[] ReplySubOpBe = [0x90, 0x62, 0xD5, 0x59];

		[Fact]
		public void Build_EmptyCatalog_IsAValidEmptyList()
		{
			// The correct reply for a character with no learned recipes: env=0, version=1, type=3, count=0x3F1 - after
			// the [subop][0xC350][handle] carrier prefix the codec now owns (#530 wave 7).
			var body = TradeskillReplyCodec.Build(0x0802E5D4, []);
			Assert.Equal("9062d5590000c3500802e5d4" + "00" + "00000001" + "03" + "000003f1", Convert.ToHexStringLower(body));
		}

		[Fact]
		public void DbProjection_ThroughColumnsAndBack_ReproducesTheBook_ByteExact()
		{
			// The end-to-end DB-path guard: project each decoded record into its Recipe row + RecipeItem children (as
			// the importer does) with each element's key+hash on the referenced Items row, then ToRecord it back (as
			// the live server does) and rebuild. Proves the column schema loses nothing through the DB representation.
			var reply = Reply();
			var sub = IndexOf(reply.Bytes, ReplySubOpBe);
			var capturedRecord = reply.Bytes.AsSpan(sub).ToArray();
			var parsed = TradeskillReplyCodec.Parse(capturedRecord);

			var rows = parsed.Recipes.Select(ToRowLikeImporter).ToList();
			var roundTripped = rows.Select(RecipeRecords.FromRow).ToList();
			var rebuilt = TradeskillReplyCodec.Build(parsed.Handle, roundTripped);

			Assert.Equal(Convert.ToHexStringLower(capturedRecord), Convert.ToHexStringLower(rebuilt));
		}

		[Fact]
		public void Parse_DecodesRecipeFieldsAndElements()
		{
			var reply = Reply();
			var sub = IndexOf(reply.Bytes, ReplySubOpBe);
			var records = TradeskillReplyCodec.Parse(reply.Bytes.AsSpan(sub)).Recipes.ToList();

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
			var capturedRecord = reply.Bytes.AsSpan(sub).ToArray(); // the WHOLE record, carrier prefix included

			var parsed = TradeskillReplyCodec.Parse(capturedRecord);
			Assert.Equal(603, parsed.Recipes.Count);
			Assert.Equal(9u, parsed.Recipes.First().Id); // first recipe id in the capture

			Assert.Equal(Convert.ToHexStringLower(capturedRecord), Convert.ToHexStringLower(TradeskillReplyCodec.Build(parsed)));
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

		// The one captured recipe catalog, committed as its own fixture. There is exactly ONE TradeskillReplyIIR in
		// the whole corpus, so this never wanted the corpus - it wanted that packet.
		private static CapturedPacket Reply()
		{
			var packet = CapturedFixture.Packets("recipe-book-reply.bin").Single();
			Assert.Equal((uint)GameServer0X2000Opcodes.TradeskillReplyIIR, packet.Iir);
			return new CapturedPacket(0, true, (uint)GameInterfaceOpcodes.DispatchPassBlob, packet.Iir, packet.Full);
		}

		// Mirrors CaptureRecipeImporter.BuildRow + item harvest, in-memory: a Recipe row whose RecipeItems each carry
		// the referenced Item template (with the element's TEA key + hash), so ToRecord can read them back.
		private static Recipe ToRowLikeImporter(RecipeRecord r)
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

			static void Add(Recipe row, IReadOnlyList<RecipeElement> els, RecipeItemRole role)
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
