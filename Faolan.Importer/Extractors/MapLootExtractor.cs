using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;

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
		private const uint LootBagSubOpcode = (uint)GameServer0X2000Opcodes.LootBagFullUpdateIIR;
		private const uint MapChangeSubOpcode = (uint)GameServer0X2000Opcodes.PlayfieldFIIR;

		public static List<MapLootPoolEntry> Extract(IReadOnlyList<CapturedPacket> packets)
		{
			var entries = new List<MapLootPoolEntry>();
			uint currentMap = 0;

			foreach (var p in packets)
			{
				if (!p.IsIirCarrier)
					continue;

				if (p.SubOpcode == MapChangeSubOpcode)
				{
					var body = p.Body;
					if (body.Length >= 38)
						currentMap = body.ReadU32Be(34);
					continue;
				}

				if (p.SubOpcode != LootBagSubOpcode || currentMap == 0)
					continue;

				foreach (var template in ReadLootTemplates(p.Body))
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
		// only the template ids - the identity quad/hash is harvested separately by the items module). `body` is the
		// packet body after the framing header: [u32 payloadLen][u32 subOpcode][record]; the ProtoLootBagIIR record the
		// codec parses starts at offset 8 (after the length + sub-opcode words).
		private static IEnumerable<uint> ReadLootTemplates(byte[] body)
		{
			if (body.Length < 8 || body.ReadU32Be(4) != LootBagSubOpcode)
				yield break;

			if (!LootBagMessageCodec.TryParse(body.AsSpan(8), out var contents, out _) || contents?.Inventory?.Items == null)
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
