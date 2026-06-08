using System;
using System.Collections.Generic;
using Faolan.Core.Data;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;

namespace Faolan.GameServer.Packets._0x737A6DF9_Test
{
	// Harvests the full identity of every item seen in captured 0x737A6DF9 snapshots: the template id, the
	// Data1..4 identity quad (tail field-1 payload), and the archetype (tail.f8). The quad + archetype are exactly
	// what `SnapshotItemBuilder` needs to build an item, so harvesting them makes any item a player has actually
	// held buildable - even ones absent from `p_items`. The merged result is embedded as
	// Data/archive/captured-items.tsv and imported into the `Items` table. See docs/spawn-dissection-plan.md Phase 5.
	public static class ArchetypeHarvester
	{
		private const uint InventorySubOpcode = 0x737A6DF9;

		// Parses one on-wire packet; if it is an inventory snapshot, merges every (templateId -> CapturedItem) found
		// in its containers into `into`. Conflicting identities for the same template are reported (should not happen).
		public static void Harvest(byte[] packetBytes, IDictionary<uint, CapturedItem> into, IList<string> conflicts = null)
		{
			Dictionary<uint, CapturedItem> found;
			try
			{
				if (!TryReadInventoryRoot(packetBytes, out var root))
					return;

				found = ExtractFromRoot(root);
			}
			catch
			{
				return; // not a parseable inventory packet
			}

			foreach (var (template, item) in found)
			{
				if (into.TryGetValue(template, out var existing))
				{
					if (!existing.Equals(item))
						conflicts?.Add($"template {template}: {existing} vs {item}");
					continue;
				}

				into[template] = item;
			}
		}

		private static Dictionary<uint, CapturedItem> ExtractFromRoot(ProtoMessage root)
		{
			var map = new Dictionary<uint, CapturedItem>();

			foreach (InventorySnapshotInspector.ContainerKind kind in Enum.GetValues(typeof(InventorySnapshotInspector.ContainerKind)))
			{
				var container = InventorySnapshotInspector.GetContainer(root, kind);

				foreach (var entry in InventorySnapshotInspector.GetContainerEntries(container))
				{
					var template = InventorySnapshotInspector.GetEntryTemplateId(entry);
					var archetype = InventorySnapshotInspector.GetEntryArchetype(entry);
					var quad = InventorySnapshotInspector.GetEntryIdentityQuad(entry);

					if (template.HasValue && archetype.HasValue && quad.HasValue)
						map[template.Value] = new CapturedItem(quad.Value.Data1, quad.Value.Data2, quad.Value.Data3, quad.Value.Data4, archetype.Value);
				}
			}

			return map;
		}

		private static bool TryReadInventoryRoot(byte[] packetBytes, out ProtoMessage root)
		{
			root = null;

			var packet = new ConanPacket(packetBytes);
			if (packet.Opcode != 0x2000)
				return false;

			var body = new FaolanStream(packet.Body.ReadArrayUInt32Length());
			var subOpcode = body.ReadUInt32();
			if (subOpcode != InventorySubOpcode)
				return false;

			body.ReadUInt32(); // c350
			body.ReadUInt32(); // instance

			root = InventorySnapshotInspector.ParseInventoryPacketBody(body).Root;
			return true;
		}

		public readonly record struct CapturedItem(uint Data1, uint Data2, uint Data3, uint Data4, uint Archetype);
	}
}
