using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Captures
{
	// Imports the crafting recipe-book catalog from a captured TradeskillReplyIIR_c (0x9062D559) - the list the
	// server sends when the client opens the Crafting -> Recipes window. Decoded + validated byte-exact in
	// TradeskillRecipeCodec (see docs/subsystems/tradeskill-recipes.md). Each record becomes a Recipe row keyed by its recipe
	// id, carrying the verbatim record body so the book reproduces byte-for-byte on the wire.
	public static class CaptureRecipeImporter
	{
		private static readonly byte[] ReplySubOpBe = [0x90, 0x62, 0xD5, 0x59]; // TradeskillReplyIIR_c

		public static List<Recipe> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var recipes = new List<Recipe>();
			foreach (var p in packets)
			{
				if (!p.IsDownload || p.SubOpcode != (uint)GameServer0X2000Opcodes.TradeskillReplyIIR)
					continue;

				// Locate the sub-opcode inside the raw packet, then step past [subop][0xC350][handle] to the reply body.
				var sub = IndexOf(p.Bytes, ReplySubOpBe);
				if (sub < 0)
					continue;

				var bodyStart = sub + 12;
				if (bodyStart >= p.Bytes.Length)
					continue;

				foreach (var r in TradeskillRecipeCodec.ParseReplyBody(p.Bytes.AsSpan(bodyStart)))
				{
					var recipe = new Recipe
					{
						Id = r.Id,
						Data = r.Data
					};
					foreach (var e in TradeskillRecipeCodec.ParseRecordItems(r.Data))
					{
						recipe.Items.Add(new RecipeItem
						{
							Role = (RecipeItemRole)e.Role,
							Position = e.Position,
							ItemId = e.ItemId,
							Flag = e.Flag,
							Trailing = e.Trailing
						});
					}

					recipes.Add(recipe);
				}
			}

			// De-dupe by recipe id (keep first) in case a capture carries the list more than once.
			return recipes.GroupBy(r => r.Id).Select(g => g.First()).ToList();
		}

		public static async Task<(int Seen, int Added, int Updated, int HarvestedItems)> ImportAsync(IDatabaseContext db, IReadOnlyCollection<Recipe> recipes)
		{
			// Seed a bare Items TEMPLATE for every item a recipe references so the RecipeItem.ItemId -> Items FK
			// (Restrict) resolves. Must run BEFORE the recipe rows are saved (same ordering as the shop/AH harvesters).
			var harvestedItems = await HarvestItems(db, recipes);

			var added = 0;
			var updated = 0;
			foreach (var r in recipes)
			{
				var existing = await db.Recipes.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == r.Id);
				if (existing == null)
				{
					db.Recipes.Add(r);
					added++;
				}
				else
				{
					existing.Data = r.Data;
					existing.Items.Clear(); // cascade-delete the old rows, then re-add the freshly parsed set
					foreach (var item in r.Items)
						existing.Items.Add(item);
					updated++;
				}
			}

			await db.SaveChangesAsync();
			return (recipes.Count, added, updated, harvestedItems);
		}

		// Ensures each item a recipe references (ingredient or result) exists as an Items TEMPLATE so the
		// RecipeItem.ItemId -> Items FK resolves. The recipe element carries the full identity (id + 128-bit TEA key
		// quad + hash) - the same source Recipe.Data ships from - so we reparse Recipe.Data to recover it (RecipeItem
		// stores neither key nor hash now; both live once in Items). Name="" keeps these bare templates out of random
		// loot (GetRandomBuildableItems filters Name != ""). A missing item is seeded; an item we already have keeps
		// its (richer) row but has its Hash BACKFILLED from the recipe when it was 0 - the recipe carries the real
		// per-template hash that AH-listing-harvested items lack (Hash=0). Verified: no recipe/item hash ever DIFFERS
		// non-zero, so the backfill only ever fills a 0, never overwrites a real hash.
		private static async Task<int> HarvestItems(IDatabaseContext db, IEnumerable<Recipe> recipes)
		{
			var harvested = 0;
			var seen = new HashSet<uint>();
			foreach (var r in recipes)
			foreach (var e in TradeskillRecipeCodec.ParseRecordItems(r.Data))
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
					Key = new ProtoDecryptionKey { Key1 = e.Data1, Key2 = e.Data2, Key3 = e.Data3, Key4 = e.Data4 }
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
