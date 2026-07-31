using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Network.Codecs.World;

namespace Faolan.Importer.Extractors
{
	// One item template observed in a captured loot bag on a given map (#273).
	public sealed record MapLootPoolEntry(uint MapId, uint ItemTemplateId);

	// Builds the per-map loot pool (#273): tracks the current map (MapChange 5D85BFC7, same as WorldObjectExtractor),
	// then for every loot bag (LootBagFullUpdateIIR 0xF096251E) parses its ProtoLootBagIIR inventory and emits each
	// item template against the current map. The captured bags read as STATIC map bags (world-entry burst objects),
	// so this is a MAP pool, not a per-NPC table - good enough for "NPCs on map X drop items map X's real bags held".
	// Sibling of WorldObjectExtractor (which keeps the bag as a world object); this one lifts only the contents.
	public static class MapLootExtractor
	{
		public static List<MapLootPoolEntry> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var entries = new List<MapLootPoolEntry>();
			uint currentMap = 0;

			foreach (var p in packets)
			{
				if (!p.IsIirCarrier)
					continue;

				if (p.SubOpcode == PlayfieldFCodec.Opcode)
				{
					if (p.TryParse(PlayfieldFCodec.Parse, out var change))
						currentMap = change.MapId;
					continue;
				}

				if (p.SubOpcode != LootContainerTemplate.ContainerClass || currentMap == 0)
					continue;

				foreach (var template in ReadLootTemplates(p))
					entries.Add(new MapLootPoolEntry(currentMap, template));
			}

			return entries;
		}

		// Dedup to one row per (map, item); a bag's items repeat across captures and several bags carry the same item.
		public static List<MapLootPoolEntry> Merge(IEnumerable<MapLootPoolEntry> all)
		{
			return all.GroupBy(e => (e.MapId, e.ItemTemplateId)).Select(g => g.First()).ToList();
		}


		// The item template ids inside a loot-bag IIR record (mirrors ItemHashHarvester.ExtractFromLootBag, but yields
		// only the template ids - the identity quad/hash is harvested separately by the items module).
		private static IEnumerable<uint> ReadLootTemplates(CapturedPacket packet)
		{
			if (!WorldObjectColumns.TryLocateContents(packet.Record, out var contents, out _) || contents.Inventory?.Items == null)
				yield break;

			foreach (var listEntry in contents.Inventory.Items)
			{
				var acg = listEntry.Item?.ItemData;
				if (acg != null && acg.Template1 != 0)
					yield return acg.Template1;
			}
		}
	}
}
