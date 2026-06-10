using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Protobuf;

namespace Faolan.GameServer.Spawn
{
	// Builds the worn-equipment InventoryEntries for a player's SimpleCharFullUpdate spawn from the character's
	// CharacterEquipment. Each worn item becomes a ProtoCoreCharacterIIR.InventoryEntry { pos = the equip slot id,
	// item = ProtoAcgItem(visual) } - the same Item -> ProtoAcgItem encoding the inventory snapshot uses
	// (SnapshotItemBuilder): keys = the item's Data1..4 quad, templates = Item.Id, hash = archetype. Replaces the
	// captured player's equipment in the spawn (see SpawnPacketBuilder.SetCharacterMessage / PlayerSpawnReader).
	public static class PlayerSpawnEquipment
	{
		// Worn CharacterEquipment slot -> the spawn's equip-slot id (CharacterEquipmentSlot enum; pos in the
		// capture). MainWeapon is main_hand_LEFT (the melee pair is 6/8; the ranged pair MainHandRight/
		// OffHandRight is 20/21) - matching EquippedSlotItem and the equip/unequip persistence.
		private static readonly (CharacterEquipmentSlot Slot, System.Func<CharacterEquipment, Item> Get)[] SlotMap =
		[
			(CharacterEquipmentSlot.Neck, e => e.Neck),
			(CharacterEquipmentSlot.Head, e => e.Head),
			(CharacterEquipmentSlot.Tokens, e => e.Tokens),
			(CharacterEquipmentSlot.Chest, e => e.Chest),
			(CharacterEquipmentSlot.MainWeapon, e => e.MainHandLeft),
			(CharacterEquipmentSlot.Belt, e => e.Belt),
			(CharacterEquipmentSlot.OffHandWeapon, e => e.OffHandLeft),
			(CharacterEquipmentSlot.LeftRing, e => e.RingLeft),
			(CharacterEquipmentSlot.RightRing, e => e.RingRight),
			(CharacterEquipmentSlot.Cape, e => e.Cloak),
			(CharacterEquipmentSlot.Shoulder, e => e.Shoulder),
			(CharacterEquipmentSlot.Boots, e => e.Boots),
			(CharacterEquipmentSlot.Hands, e => e.Hands),
			(CharacterEquipmentSlot.Legs, e => e.Legs),
			(CharacterEquipmentSlot.Wrist, e => e.Wrist),
			(CharacterEquipmentSlot.RangedWeapon, e => e.MainHandRight),
			(CharacterEquipmentSlot.Ammo, e => e.OffHandRight),
		];

		public static List<ProtoCoreCharacterIIR.InventoryEntry> BuildInventoryEntries(CharacterEquipment equipment)
		{
			var entries = new List<ProtoCoreCharacterIIR.InventoryEntry>();
			if (equipment == null)
				return entries;

			foreach (var (slot, get) in SlotMap)
			{
				var item = get(equipment);
				if (item == null || item.Archetype == 0) // only fully-buildable items (real identity + archetype)
					continue;

				entries.Add(BuildEntry((uint)slot, item));
			}

			return entries.OrderBy(e => e.Pos).ToList();
		}

		public static ProtoCoreCharacterIIR.InventoryEntry BuildEntry(uint pos, Item item)
		{
			var key = new ProtoDecryptionKey
			{
				Key1 = item.Data1,
				Key2 = item.Data2,
				Key3 = item.Data3,
				Key4 = item.Data4,
			};

			return new ProtoCoreCharacterIIR.InventoryEntry
			{
				Pos = pos,
				Item = new ProtoAcgItem
				{
					Key1 = key,
					Key2 = key,
					Key3 = key,
					Template1 = item.Id,
					Template2 = item.Id,
					Level = 1,
					Hash = item.Archetype,
				},
			};
		}
	}
}
