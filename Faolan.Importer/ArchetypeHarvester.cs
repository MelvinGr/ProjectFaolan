using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Faolan.Core.Data;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.GameServer.Packets.FullCharSnapshot;
using Faolan.GameServer.Spawn;

namespace Faolan.Importer
{
	// Harvests the full identity of every item seen in captures: the template id, the Data1..4 identity quad, and
	// the archetype. Two sources, both yielding (template -> CapturedItem): the 0x737A6DF9 inventory snapshot
	// (bag/equipped container entries, tail.f8 archetype) AND the FA015807 character SPAWN's worn-equipment entries
	// (ProtoAcgItem: Key1..4 = the quad, Template1 = id, Hash = archetype). The spawn source is what makes items a
	// character only ever WORE (never carried in a bag snapshot - e.g. Seadog's Shorts) buildable, so the spawn
	// equipment renderer (PlayerSpawnEquipment, gated on Archetype != 0) can draw them. The quad + archetype are
	// exactly what SnapshotItemBuilder / PlayerSpawnEquipment need. Merged result is embedded as
	// Data/archive/captured-items.tsv and imported into the `Items` table. See docs/spawn-dissection-plan.md Phase 5.
	public static class ArchetypeHarvester
	{
		private const uint InventorySubOpcode = 0x737A6DF9;

		// Parses one on-wire packet and merges every (templateId -> CapturedItem) it can recover - from an inventory
		// snapshot's containers AND/OR a character spawn's worn equipment - into `into`. Conflicting identities for the
		// same template are reported (should not happen).
		public static void Harvest(byte[] packetBytes, IDictionary<uint, CapturedItem> into, IList<string> conflicts = null)
		{
			var found = new Dictionary<uint, CapturedItem>();
			try
			{
				if (TryReadInventoryRoot(packetBytes, out var root))
					found = ExtractFromRoot(root);
			}
			catch
			{
				// not a parseable inventory packet - fall through to the spawn-equipment source
			}

			// Also harvest worn equipment from a character spawn (additive; either source may be absent).
			try
			{
				ExtractFromSpawnEquipment(packetBytes, found);
			}
			catch
			{
				// not a parseable spawn packet
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

		// Recovers (template -> CapturedItem) from a character SPAWN's worn equipment. The spawn body holds a
		// ProtoCoreCharacterIIR whose InventoryEntries are the worn items, each a ProtoAcgItem carrying the same
		// identity the inventory snapshot does (Key1.Key1..4 = the Data quad, Template1 = the template id, Hash =
		// the archetype). PlayerSpawnReader scans the body for the character message, so this works on any packet
		// that contains a spawn (FA015807) - we just try it on every packet's Conan body.
		private static void ExtractFromSpawnEquipment(byte[] packetBytes, IDictionary<uint, CapturedItem> into)
		{
			if (packetBytes == null || packetBytes.Length < 12)
				return;

			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(packetBytes.AsSpan(8, 4));
			var bodyStart = 12 + headerLen;
			if (bodyStart >= packetBytes.Length)
				return;

			if (!PlayerSpawnReader.TryReadCharacterMessage(packetBytes[bodyStart..], out var read))
				return;

			foreach (var entry in read.Message.InventoryEntries)
			{
				var acg = entry.Item;
				if (acg?.Key1 == null)
					continue;

				var template = acg.Template1;
				var archetype = acg.Hash;
				if (template == 0 || archetype == 0)
					continue; // need both a template and a real archetype to be buildable

				into[template] = new CapturedItem(acg.Key1.Key1, acg.Key1.Key2, acg.Key1.Key3, acg.Key1.Key4, archetype);
			}
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

			root = InventorySnapshotInspector.ParseInventoryPacketBody(body)
				.Root;
			return true;
		}

		public readonly record struct CapturedItem(uint Data1, uint Data2, uint Data3, uint Data4, uint Archetype);
	}
}
