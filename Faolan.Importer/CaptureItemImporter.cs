using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer
{
	// Imports item identities harvested from captured inventory snapshots (0x737A6DF9) straight into Items:
	// sets the Data1..4 quad + Archetype on existing rows (e.g. p_items templates) and INSERTS capture-only items
	// (real quad + archetype, no name/level). Takes raw on-wire packet bytes so it has no dependency on the pcap
	// reader - the Faolan.Importer tool feeds it AnalyzedPacket.Bytes; tests feed it capture .bin bytes.
	public static class CaptureItemImporter
	{
		public static async Task<(int Total, int Inserted)> ImportAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, ILogger logger = null)
		{
			var harvested = new Dictionary<uint, ArchetypeHarvester.CapturedItem>();
			foreach (var packet in rawPackets)
				ArchetypeHarvester.Harvest(packet, harvested);

			if (harvested.Count == 0)
				return (0, 0);

			var ids = harvested.Keys.ToList();
			var existing = await db
				.Items
				.Where(i => ids.Contains(i.Id))
				.ToDictionaryAsync(i => i.Id);

			var inserted = 0;
			foreach (var (id, ci) in harvested)
			{
				if (!existing.TryGetValue(id, out var item))
				{
					item = new Item
					{
						Id = id,
						Name = string.Empty
					};
					db.Items.Add(item);
					inserted++;
				}

				item.Data1 = ci.Data1;
				item.Data2 = ci.Data2;
				item.Data3 = ci.Data3;
				item.Data4 = ci.Data4;
				item.Archetype = ci.Archetype;
			}

			await db.SaveChangesAsync();
			logger?.LogInformation("CaptureItemImporter: upserted {Count} items from captures ({Inserted} new, archetype set on all).", harvested.Count, inserted);
			return (harvested.Count, inserted);
		}
	}
}
