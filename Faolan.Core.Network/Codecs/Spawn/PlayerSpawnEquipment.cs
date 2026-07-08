using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Protobuf;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// Builds the worn-equipment InventoryEntries for a player's SimpleCharFullUpdate spawn from the character's
	// CharacterEquipment. Each worn item becomes a ProtoCoreCharacterIIR.InventoryEntry { pos = the equip slot id,
	// item = ProtoAcgItem(visual) } - the same Item -> ProtoAcgItem encoding the inventory snapshot uses
	// (SnapshotItemBuilder): keys = the item's Data1..4 quad, templates = Item.Id, hash = hash. Replaces the
	// captured player's equipment in the spawn (see SpawnPacketBuilder.SetCharacterMessage / PlayerSpawnReader).
	public static class PlayerSpawnEquipment
	{
		public static ProtoCoreCharacterIIR.InventoryEntry BuildEntry(CharacterEquipmentSlot pos, Item item)
		{
			var key = new ProtoDecryptionKey
			{
				Key1 = item.Data1,
				Key2 = item.Data2,
				Key3 = item.Data3,
				Key4 = item.Data4
			};

			return new ProtoCoreCharacterIIR.InventoryEntry
			{
				Pos = (uint)pos,
				Item = new ProtoAcgItem
				{
					Key1 = key,
					Key2 = key,
					Key3 = key,
					Template1 = item.Id,
					Template2 = item.Id,
					Level = 1,
					Hash = item.Hash
				}
			};
		}

		public static List<ProtoCoreCharacterIIR.InventoryEntry> BuildInventoryEntries(CharacterEquipment equipment)
		{
			var entries = new List<ProtoCoreCharacterIIR.InventoryEntry>();
			if (equipment == null)
				return entries;

			// Every CharacterEquipmentSlot (the 17 worn slots + the 5 vanity slots) via the entity's slot indexer, so
			// vanity items ride the spawn at their equip-slot pos (1002+). The slot id IS the enum value (pos in the
			// capture: MainWeapon = main_hand_left 6, the ranged pair 20/21, vanity 1002+).
			foreach (var slot in Enum.GetValues<CharacterEquipmentSlot>())
			{
				var item = equipment[slot];
				if (item == null || item.Hash == 0) // only fully-buildable items (real identity + hash)
					continue;

				entries.Add(BuildEntry(slot, item));
			}

			return entries.OrderBy(e => e.Pos).ToList();
		}
	}
}
