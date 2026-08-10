using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Captures
{
	// Imports item identities harvested from captured inventory snapshots (0x737A6DF9) straight into Items:
	// sets the Data1..4 quad + Hash on existing rows (e.g. p_items templates) and INSERTS capture-only items
	// (real quad + hash, no name/level). Takes raw on-wire packet bytes so it has no dependency on the pcap
	// reader - the Faolan.Importer tool feeds it AnalyzedPacket.Bytes; tests feed it capture .bin bytes.
	public static class CaptureItemImporter
	{
		public static Task<(int Total, int Inserted)> ImportAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, ILogger? logger = null)
		{
			var harvested = new Dictionary<uint, ItemHashHarvester.CapturedItem>();
			var types = new Dictionary<uint, ItemType>();
			foreach (var packet in rawPackets)
				ItemHashHarvester.Harvest(packet, harvested, types: types);

			return UpsertAsync(db, harvested, types, null, logger);
		}

		// The upsert itself, over an ALREADY harvested set. Split out for the sources that do not arrive as raw Conan
		// packets and so cannot go through `ItemHashHarvester`: the `itemref://` chat links (#751) are plain text on
		// the CHAT stream, and they are also the only source that carries a NAME, which is why `names` exists at all.
		// Every never-downgrade rule below is shared rather than reimplemented per source.
		public static async Task<(int Total, int Inserted)> UpsertAsync(IDatabaseContext db, IReadOnlyDictionary<uint, ItemHashHarvester.CapturedItem> harvested,
			IReadOnlyDictionary<uint, ItemType>? types = null, IReadOnlyDictionary<uint, string>? names = null, ILogger? logger = null)
		{
			types ??= new Dictionary<uint, ItemType>();
			if (harvested.Count == 0)
				return (0, 0);

			var ids = harvested.Keys.ToList();
			var existing = await db.Items.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id);

			var inserted = 0;
			foreach (var (id, ci) in harvested)
			{
				if (!existing.TryGetValue(id, out var item))
				{
					item = new Item
					{
						Id = id,
						Name = string.Empty,
						Type = ItemType.Normal // upgraded below if a quest/resource container classified it
					};
					db.Items.Add(item);
					inserted++;
				}

				item.Key.Key1 = ci.Data1;
				item.Key.Key2 = ci.Data2;
				item.Key.Key3 = ci.Data3;
				item.Key.Key4 = ci.Data4;
				// Never downgrade a real hash to 0: some sources (loot bags, claim lists) carry the identity quad but no
				// hash, and must not clobber a hash another source already set (e.g. the same item seen worn/in a bag).
				if (ci.Hash != 0)
					item.Hash = ci.Hash;

				// The wire LEVEL, on the same never-downgrade rule as the hash: a source that carries no level must not
				// clobber one another source supplied. This is also the only thing that CORRECTS a level - the legacy
				// values in the shipped DB predate the visible history and 12 measurable templates disagreed with
				// retail's wire, because the only previous writer (CaptureListingImporter.HarvestItems) sets Level on
				// INSERT and skips any template already in Items, so a wrong one was never revisited (#725).
				if (ci.Level != 0)
					item.Level = (byte)Math.Min(ci.Level, byte.MaxValue);

				// The SECOND template and its own key (#752) - the high endpoint of a level-scaled item. On the same
				// never-downgrade rule: only 3 of the 344 captured pairs carry one, and a source that saw the item
				// without a pair must not erase one that did.
				if (ci.Pair is { } pair)
				{
					item.TemplateId2 = pair.Template2;
					item.PairKey.Key1 = pair.Key1;
					item.PairKey.Key2 = pair.Key2;
					item.PairKey.Key3 = pair.Key3;
					item.PairKey.Key4 = pair.Key4;
				}

				// Classify Quest/Resource items by the container they were captured in; only ever UPGRADE from the
				// default Normal so a classification survives a later capture that holds the item in a bag.
				if (types.TryGetValue(id, out var type) && type != ItemType.Normal)
					item.Type = type;

				// The display NAME, and ONLY into an empty one. A chat link is player-typed text that happens to be
				// composed by the client, so it fills a gap but must never outrank a row a snapshot or the RDB named
				// (#751). Every one of the 13 corpus links whose item we already hold agrees with the stored name
				// anyway - that agreement is the oracle for the source, not a licence to overwrite.
				if (names != null && string.IsNullOrEmpty(item.Name) && names.TryGetValue(id, out var name) && !string.IsNullOrEmpty(name))
					item.Name = name;
			}

			await db.SaveChangesAsync();
			logger?.LogInformation("CaptureItemImporter: upserted {Count} items from captures ({Inserted} new, hash set on all).", harvested.Count, inserted);
			return (harvested.Count, inserted);
		}
	}
}
