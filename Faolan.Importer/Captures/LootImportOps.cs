using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Captures
{
	/// <summary>
	///     Capture-import orchestration for loot, driven by the modules (Faolan.Importer.Modules): the map loot pools
	///     and the random-loot exclusions.
	/// </summary>
	public static class LootImportOps
	{
		// Curated unique/no-random-drop item templates. Unique items are typed Normal and carry no wire-derivable
		// "unique" flag (the RDB item-template property table has no decoded unique field, and SearchCategory mixes them
		// with ordinary sellables), so they cannot be auto-detected - this is the hand-maintained denylist. Add a
		// template id here (or set it live with `.noloot <id>`) when an item that should not randomly drop is spotted
		// in-client.
		private static readonly uint[] UniqueLootDenylist =
		[
			354477 // Ape King's Treasure
		];

		/// <summary>
		///     Seeds the random-loot exclusion flag: excludes every Quest/Resource item (those typed from a non-Normal
		///     container) plus the curated unique denylist.
		/// </summary>
		/// <remarks>
		///     Add-only - it never clears the flag, so an item flagged live via.noloot (or by hand) survives a
		///     re-import.
		/// </remarks>
		/// <returns>The number of rows newly excluded.</returns>
		public static async Task<int> ApplyRandomLootExclusions(DatabaseContext db)
		{
			var denylist = UniqueLootDenylist.ToHashSet();
			var newlyExcluded = 0;
			foreach (var item in await db.Items.ToListAsync())
			{
				if (item.ExcludeFromRandomLoot)
					continue; // already excluded (a previous run, or a live .noloot) - never clear it

				if (item.Type != ItemType.Normal || denylist.Contains(item.Id))
				{
					item.ExcludeFromRandomLoot = true;
					newlyExcluded++;
				}
			}

			if (newlyExcluded > 0)
				await db.SaveChangesAsync();
			return newlyExcluded;
		}

		/// <summary>
		///     Rebuilds the per-map loot pool from the captured bag contents.
		/// </summary>
		/// <remarks>
		///     A clean rebuild (the pool is fully derived from the captures, so a re-run must not accumulate stale rows),
		///     keyed for dedup by (MapId, ItemTemplateId). Only items that exist in the Items table are kept - an unknown
		///     template the server cannot build is useless as a drop (the items module seeds those identities, so it runs
		///     first in the default order).
		/// </remarks>
		public static async Task<int> ImportMapLootPool(DatabaseContext db, List<MapLootPoolEntry> entries)
		{
			db.MapLootEntries.RemoveRange(await db.MapLootEntries.ToListAsync());

			var knownItems = (await db.Items.Select(i => i.Id).ToListAsync()).ToHashSet();
			var knownMaps = (await db.Maps.Select(m => m.Id).ToListAsync()).ToHashSet();
			var kept = entries
				.Where(e => knownMaps.Contains(e.MapId) && knownItems.Contains(e.ItemTemplateId)) // FK targets must exist
				.GroupBy(e => (e.MapId, e.ItemTemplateId))
				.Select(g => g.Key)
				.ToList();

			foreach (var (mapId, templateId) in kept)
			{
				db.MapLootEntries.Add(new MapLootEntry
				{
					MapId = mapId,
					ItemTemplateId = templateId
				});
			}

			await db.SaveChangesAsync();
			return kept.Count;
		}
	}
}
