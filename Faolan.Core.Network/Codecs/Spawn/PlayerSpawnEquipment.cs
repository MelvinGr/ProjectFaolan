using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// Builds the worn-equipment InventoryEntries for a player's SimpleCharFullUpdate spawn from the character's
	// CharacterEquipment. Each worn item becomes a ProtoCoreCharacterIIR.InventoryEntry { pos = the equip slot id,
	// item = ProtoAcgItem(visual) } - the same Item -> ProtoAcgItem encoding the inventory snapshot uses
	// (SnapshotItemBuilder): keys = the item's Data1..4 quad, templates = Item.Id, hash = hash. Replaces the
	// captured player's equipment in the spawn (see SpawnPacketBuilder.SetCharacterMessage / SpawnBodyReader).
	public static class PlayerSpawnEquipment
	{
		public static ProtoCoreCharacterIIR.InventoryEntry BuildEntry(CharacterEquipmentSlot pos, Item item)
		{
			var key = item.Key;

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
				if (item == null)
					continue; // slot not worn

				// An item WITHOUT a hash cannot be described to other clients, and skipping it silently is the worst
				// half-measure available: the wearer sees the piece (their own client rendered it from their inventory),
				// while everybody else sees them without it, and nothing says why. The NPC path already refuses this
				// (NpcSpawnEquipmentBuilder.BuildEntry: "say what's missing"); an equipped item is the same promise -
				// a hash is harvested content, so a worn item lacking one is a gap in Items, not a state to render around.
				if (item.Hash == 0)
				{
					throw new InvalidOperationException(
						$"Equipped item {item.Id} '{item.Name}' in slot {slot} has no hash, so it cannot be put on the wire - other players would see the wearer without it. Harvest the item's hash.");
				}

				// Every worn + vanity item ships - NOTHING is withheld for the vanity filter. Show/hide is driven purely
				// by the bitmask in the spawn header (CharacterEquipment.VanityShownMask, emitted by
				// PlayerSpawnScaffoldRows); the client renders it off that mask (#336).
				entries.Add(BuildEntry(slot, item));
			}

			return entries.OrderBy(e => e.Pos).ToList();
		}
	}
}
