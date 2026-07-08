using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Constants;

namespace Faolan.Importer.Captures
{
	// Imports the crafting recipe-book catalog from a captured TradeskillReplyIIR_c (0x9062D559) - the list the
	// server sends when the client opens the Crafting -> Recipes window. Decoded + validated byte-exact in
	// TradeskillRecipeCodec (see docs/tradeskill-recipes.md). Each record becomes a Recipe row keyed by its recipe
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
					var recipe = new Recipe { Id = r.Id, Data = r.Data };
					foreach (var e in TradeskillRecipeCodec.ParseRecordItems(r.Data))
						recipe.Items.Add(new RecipeItem
						{
							Role = (RecipeItemRole)e.Role,
							Position = e.Position,
							ItemId = e.ItemId,
							Flag = e.Flag,
							Hash = e.Hash,
							Data1 = e.Data1,
							Data2 = e.Data2,
							Data3 = e.Data3,
							Data4 = e.Data4,
							Trailing = e.Trailing
						});
					recipes.Add(recipe);
				}
			}

			// De-dupe by recipe id (keep first) in case a capture carries the list more than once.
			return recipes
				.GroupBy(r => r.Id)
				.Select(g => g.First())
				.ToList();
		}

		public static async Task<(int Seen, int Added, int Updated)> ImportAsync(IDatabaseContext db, IReadOnlyCollection<Recipe> recipes)
		{
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
			return (recipes.Count, added, updated);
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
