using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Protobuf;

namespace Faolan.GameServer.Spawn
{
	// Builds / lifts an NPC's worn-equipment InventoryEntries (the CharacterIIR f23 list) to and from
	// NpcSpawnEquipment rows, so the equipment generates from the DB instead of the captured NpcSpawn.Data blob
	// (Gitea #185). The NPC counterpart of PlayerSpawnEquipment: the captured NPC items follow the same ProtoAcgItem
	// shape the player builder emits - key1==key2==key3 (the GUID quad), template1==template2, no template3 - so a
	// row stores just the quad + Template + Level + Hash and BuildEntries reproduces the wire item byte-for-byte.
	public static class NpcSpawnEquipmentBuilder
	{
		// Rebuilds the InventoryEntries (ordered by slot) from the stored rows.
		public static List<ProtoCoreCharacterIIR.InventoryEntry> BuildEntries(IEnumerable<NpcSpawnEquipment> rows)
		{
			return rows.OrderBy(r => r.Pos).Select(BuildEntry).ToList();
		}

		public static ProtoCoreCharacterIIR.InventoryEntry BuildEntry(NpcSpawnEquipment row)
		{
			var key = new ProtoDecryptionKey
			{
				Key1 = row.Key1,
				Key2 = row.Key2,
				Key3 = row.Key3,
				Key4 = row.Key4
			};

			return new ProtoCoreCharacterIIR.InventoryEntry
			{
				Pos = row.Pos,
				Item = new ProtoAcgItem
				{
					Key1 = key,
					Key2 = key,
					Key3 = key,
					Template1 = row.Template,
					Template2 = row.Template,
					Level = row.Level,
					Hash = row.Hash
				}
			};
		}

		// Lifts a captured InventoryEntry into a row, but ONLY if it matches the reproducible shape (the three keys
		// identical, the two templates identical, no template3). Returns false for any entry outside that shape, so
		// the importer can decline the whole NPC and leave its equipment to the Data blob rather than store something
		// the builder would not reproduce byte-for-byte.
		public static bool TryLift(ProtoCoreCharacterIIR.InventoryEntry entry, out NpcSpawnEquipment row)
		{
			row = null;
			var item = entry?.Item;
			var k = item?.Key1;
			if (k == null || item.Key2 == null || item.Key3 == null)
				return false;
			if (!SameKey(k, item.Key2) || !SameKey(k, item.Key3))
				return false;
			if (item.Template1 != item.Template2 || item.ShouldSerializeTemplate3())
				return false;

			row = new NpcSpawnEquipment
			{
				Pos = entry.Pos,
				Key1 = k.Key1,
				Key2 = k.Key2,
				Key3 = k.Key3,
				Key4 = k.Key4,
				Template = item.Template1,
				Level = item.Level,
				Hash = item.Hash
			};

			// Byte-exact gate: only accept entries the rebuilt row reproduces exactly (guards any item carrying
			// fields the row doesn't model - extra ProtoAcgItem fields, an absent level/hash, etc.).
			if (!BuildEntry(row).ToProtobuf().SequenceEqual(entry.ToProtobuf()))
			{
				row = null;
				return false;
			}

			return true;
		}

		private static bool SameKey(ProtoDecryptionKey a, ProtoDecryptionKey b)
		{
			return a.Key1 == b.Key1 && a.Key2 == b.Key2 && a.Key3 == b.Key3 && a.Key4 == b.Key4;
		}
	}
}
