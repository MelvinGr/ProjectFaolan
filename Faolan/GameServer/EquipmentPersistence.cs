using System;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	// Persists the player's equip/unequip actions (0x642CD3D6 / MoveEquippedToFromInventory) to the DB so gear
	// changes survive logout. The world-burst replay then reflects the saved CharacterEquipment on next login.
	// NOTE: reflecting an item moved into an equip slot the captured snapshot has no entry for (e.g. a ring) needs
	// ADDING an equipped entry, not just the in-place replace we do today - see docs/spawn-dissection-plan.md.
	public partial class GameServerListener
	{
		// Player equipped an item: bag slot `fromBagSlot` -> equip slot `targetSlot`, carrying the item's template
		// handle (the p_items/Items id). Persists targetSlot = item and clears the bag source if it held it.
		private async Task PersistEquip(INetworkClient client, CharacterEquipmentSlot targetSlot, uint itemHandle)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			try
			{
				var item = await Database.GetItem(itemHandle);
				if (item == null)
				{
					Logger.LogInformation("Equip not persisted: item template {Handle} is not a DB item (captured-only).", itemHandle);
					return;
				}

				var equipment = await Database.GetCharacterEquipment(character.Id) ?? new CharacterEquipment
				{
					Id = character.Id,
					CharacterId = character.Id
				};

				if (!SetEquipmentSlot(equipment, targetSlot, item))
				{
					Logger.LogWarning("Equip not persisted: unmapped equip slot {Slot}.", targetSlot);
					return;
				}

				// The item left the bag: clear FirstPosBag if that is where it was.
				if (equipment.FirstPosBag != null && equipment.FirstPosBag.Id == item.Id)
					equipment.FirstPosBag = null;

				await Database.SaveCharacterEquipment(equipment);
				Logger.LogInformation("Persisted EQUIP: {Name} ({Id}) -> {Slot} for character {CharacterId}.", item.Name, item.Id, targetSlot, character.Id);
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex, "Failed to persist equip of item {Handle} to {Slot}.", itemHandle, targetSlot);
			}
		}

		// Player unequipped the item in equip slot `fromSlot` (into the bag). Persists fromSlot = empty and parks the
		// item in the bag (FirstPosBag) so it is not lost.
		private async Task PersistUnequip(INetworkClient client, CharacterEquipmentSlot fromSlot)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			try
			{
				var equipment = await Database.GetCharacterEquipment(character.Id);
				if (equipment == null)
					return;

				var current = GetEquipmentSlot(equipment, fromSlot);
				if (current == null)
				{
					Logger.LogInformation("Unequip not persisted: equip slot {Slot} holds no DB item (captured-only).", fromSlot);
					return;
				}

				SetEquipmentSlot(equipment, fromSlot, null);
				equipment.FirstPosBag = current; // park it in the bag

				await Database.SaveCharacterEquipment(equipment);
				Logger.LogInformation("Persisted UNEQUIP: {Name} ({Id}) from {Slot} -> bag for character {CharacterId}.", current.Name, current.Id, fromSlot, character.Id);
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex, "Failed to persist unequip from {Slot}.", fromSlot);
			}
		}

		// Maps an equip slot to its CharacterEquipment item nav. Mirrors EquippedSlotItem but keyed by
		// the CharacterEquipmentSlot enum.
		public static Item GetEquipmentSlot(CharacterEquipment e, CharacterEquipmentSlot slot)
		{
			return slot switch
			{
				CharacterEquipmentSlot.Neck       => e.Neck,
				CharacterEquipmentSlot.Head       => e.Head,
				CharacterEquipmentSlot.Chest      => e.Chest,
				CharacterEquipmentSlot.MainWeapon => e.MainHandLeft,
				CharacterEquipmentSlot.Belt       => e.Belt,
				CharacterEquipmentSlot.LeftRing   => e.RingLeft,
				CharacterEquipmentSlot.RightRing  => e.RingRight,
				CharacterEquipmentSlot.Cape       => e.Cloak,
				CharacterEquipmentSlot.Shoulder   => e.Shoulder,
				CharacterEquipmentSlot.Boots      => e.Boots,
				CharacterEquipmentSlot.Hands      => e.Hands,
				CharacterEquipmentSlot.Legs       => e.Legs,
				CharacterEquipmentSlot.Wrist      => e.Wrist,
				_                                 => null
			};
		}

		// Sets an equip slot's CharacterEquipment item nav. Returns false for an unmapped slot.
		public static bool SetEquipmentSlot(CharacterEquipment e, CharacterEquipmentSlot slot, Item item)
		{
			switch (slot)
			{
				case CharacterEquipmentSlot.Neck:
					e.Neck = item;
					return true;
				case CharacterEquipmentSlot.Head:
					e.Head = item;
					return true;
				case CharacterEquipmentSlot.Chest:
					e.Chest = item;
					return true;
				case CharacterEquipmentSlot.MainWeapon:
					e.MainHandLeft = item;
					return true;
				case CharacterEquipmentSlot.Belt:
					e.Belt = item;
					return true;
				case CharacterEquipmentSlot.LeftRing:
					e.RingLeft = item;
					return true;
				case CharacterEquipmentSlot.RightRing:
					e.RingRight = item;
					return true;
				case CharacterEquipmentSlot.Cape:
					e.Cloak = item;
					return true;
				case CharacterEquipmentSlot.Shoulder:
					e.Shoulder = item;
					return true;
				case CharacterEquipmentSlot.Boots:
					e.Boots = item;
					return true;
				case CharacterEquipmentSlot.Hands:
					e.Hands = item;
					return true;
				case CharacterEquipmentSlot.Legs:
					e.Legs = item;
					return true;
				case CharacterEquipmentSlot.Wrist:
					e.Wrist = item;
					return true;
				default:
					return false;
			}
		}
	}
}
