using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Protobuf;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Captures
{
	// Imports the crafting recipe-book catalog from a captured TradeskillReplyIIR_c (0x9062D559) - the list the
	// server sends when the client opens the Crafting -> Recipes window. Decoded + validated byte-exact in
	// TradeskillRecipeCodec (see docs/subsystems/tradeskill-recipes.md). Each record becomes a fully-columnized Recipe
	// row (header/mid/tail columns + ingredient/result RecipeItem child rows); the wire record rebuilds from those
	// columns, so no verbatim blob is stored.
	public static class CaptureRecipeImporter
	{
		private static readonly byte[] ReplySubOpBe = [0x90, 0x62, 0xD5, 0x59]; // TradeskillReplyIIR_c

		public static List<RecipeRecord> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var records = new List<RecipeRecord>();
			foreach (var p in packets)
			{
				if (!p.IsDownload || p.SubOpcode != TradeskillReplyCodec.Opcode)
					continue;

				// Locate the sub-opcode inside the raw packet; the record runs from there to the packet end, which is
				// what the codec's strict Parse asserts (#530 wave 7 - the reader used to be told where the BODY
				// started and stopped after `count` records, so trailing bytes went unnoticed).
				var sub = IndexOf(p.Bytes, ReplySubOpBe);
				if (sub < 0)
					continue;

				records.AddRange(TradeskillReplyCodec.Parse(p.Bytes.AsSpan(sub)).Recipes);
			}

			// De-dupe by recipe id (keep first) in case a capture carries the list more than once.
			return records.GroupBy(r => r.Id).Select(g => g.First()).ToList();
		}

		public static async Task<(int Seen, int Added, int Updated, int HarvestedItems)> ImportAsync(IDatabaseContext db, IReadOnlyCollection<RecipeRecord> records)
		{
			// Seed a bare Items TEMPLATE for every item a recipe references so the RecipeItem.ItemId -> Items FK
			// (Restrict) resolves. Must run BEFORE the recipe rows are saved (same ordering as the shop/AH harvesters).
			var harvestedItems = await HarvestItems(db, records);

			var added = 0;
			var updated = 0;
			foreach (var r in records)
			{
				var row = BuildRow(r);
				var existing = await db.Recipes.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == r.Id);
				if (existing == null)
				{
					db.Recipes.Add(row);
					added++;
				}
				else
				{
					existing.Header1 = row.Header1;
					existing.RdbType = row.RdbType;
					existing.MidFlag = row.MidFlag;
					existing.Tail = row.Tail;
					existing.TrailingFlag = row.TrailingFlag;
					existing.RefId0 = row.RefId0;
					existing.RefId1 = row.RefId1;
					existing.Items.Clear(); // cascade-delete the old rows, then re-add the freshly parsed set
					foreach (var item in row.Items)
						existing.Items.Add(item);
					updated++;
				}
			}

			await db.SaveChangesAsync();
			return (records.Count, added, updated, harvestedItems);
		}

		private static void AddItems(Recipe recipe, IReadOnlyList<RecipeElement> elements, RecipeItemRole role)
		{
			for (var i = 0; i < elements.Count; i++)
			{
				var e = elements[i];
				recipe.Items.Add(new RecipeItem
				{
					Role = role,
					Position = i,
					ItemId = e.ItemId,
					Flag = e.Flag,
					Trailing = e.Trailing
				});
			}
		}

		// Map a decoded wire record to its fully-columnized Recipe row: the per-recipe header/mid/tail fields, the
		// opaque ref-id list (1-2 entries), and the ingredient/result elements as RecipeItem child rows (item id +
		// flag + trailing; the item's TEA key + hash live once on the referenced Items row).
		private static Recipe BuildRow(RecipeRecord r)
		{
			if (r.RefIds.Count is < 1 or > 2)
				throw new InvalidOperationException($"Recipe {r.Id:x}: ref-id list has {r.RefIds.Count} entries (expected 1 or 2)");

			var recipe = new Recipe
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

			AddItems(recipe, r.Ingredients, RecipeItemRole.Ingredient);
			AddItems(recipe, r.Results, RecipeItemRole.Result);
			return recipe;
		}

		// Ensures each item a recipe references (ingredient or result) exists as an Items TEMPLATE so the
		// RecipeItem.ItemId -> Items FK resolves. The recipe element carries the full identity (id + 128-bit TEA key
		// quad + hash) - the same source the wire record rebuilds from - so we read it straight off the elements
		// (RecipeItem stores neither key nor hash; both live once in Items). Name="" keeps these bare templates out of
		// random loot (GetRandomBuildableItems filters Name != ""). A missing item is seeded; an item we already have
		// keeps its (richer) row but has its Hash BACKFILLED from the recipe when it was 0 - the recipe carries the
		// real per-template hash that AH-listing-harvested items lack (Hash=0). Verified: no recipe/item hash ever
		// DIFFERS non-zero, so the backfill only ever fills a 0, never overwrites a real hash.
		private static async Task<int> HarvestItems(IDatabaseContext db, IEnumerable<RecipeRecord> records)
		{
			var harvested = 0;
			var seen = new HashSet<uint>();
			foreach (var r in records)
			foreach (var e in r.Ingredients.Concat(r.Results))
			{
				if (e.ItemId == 0 || !seen.Add(e.ItemId))
					continue;

				var existing = await db.Items.FirstOrDefaultAsync(i => i.Id == e.ItemId);
				if (existing != null)
				{
					if (existing.Hash == 0 && e.Hash != 0)
						existing.Hash = e.Hash;
					continue;
				}

				db.Items.Add(new Item
				{
					Id = e.ItemId,
					Name = "",
					Type = ItemType.Normal,
					Hash = e.Hash,
					Key = new ProtoDecryptionKey
					{
						Key1 = e.Key1,
						Key2 = e.Key2,
						Key3 = e.Key3,
						Key4 = e.Key4
					}
				});
				harvested++;
			}

			await db.SaveChangesAsync();
			return harvested;
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
