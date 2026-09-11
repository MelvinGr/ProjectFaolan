using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Spawn
{
	/// <summary>
	///     Builds the worn-equipment InventoryEntries for a player's SimpleCharFullUpdate spawn from the character's
	///     equipped item instances. Each worn item becomes an InventoryEntry whose <c>pos</c> is the equip slot id and
	///     whose item is a ProtoAcgItem - the same Item-to-ProtoAcgItem encoding the inventory snapshot uses: the keys are
	///     the item's key quad, both templates its id, and the hash its hash.
	/// </summary>
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
					// The item's own level, exactly as the snapshot entry carries it. Measured over the dumps
					// (--report spawn-item-level): 125,721 of 125,722 captured worn-item entries across 23,478
					// character spawns carry exactly their template's Items.Level, with no disagreements and no
					// template carrying two different levels; the one exception is an entry whose template is not in
					// our catalog at all. It is not a constant - level 1 accounts for 108,819 of those entries, so a
					// hardcoded 1 is right 87% of the time and wrong the rest.
					Level = item.Level,
					Hash = AcgHash.ForWire(item.Id, item.Hash)
				}
			};
		}

		/// <summary>
		///     Builds every worn entry. <paramref name="worn" /> is the character's Equipped container as slot to instance
		///     (IDatabaseRepository.GetEquippedItems). The 17 worn slots and the 7 vanity slots are treated alike, so vanity
		///     items ride the spawn at their equip-slot position. The slot id is the enum value: in the capture the main
		///     weapon is main_hand_left 6, the ranged pair 20 and 21, and vanity 1002 and up.
		/// </summary>
		public static List<ProtoCoreCharacterIIR.InventoryEntry> BuildInventoryEntries(IReadOnlyDictionary<CharacterEquipmentSlot, ItemInstance>? worn)
		{
			// An item with no harvested hash still ships: the token carries no information and the client validates
			// nothing, so BuildEntry mints one and the wearer and everybody else see the same piece.
			//
			// Every worn and vanity item ships; nothing is withheld for the vanity filter. Show and hide are driven
			// purely by the bitmask in the spawn header (Character.VanityShownMask, emitted by
			// PlayerSpawnScaffoldRows), which is what the client renders off.
			return (worn ?? new Dictionary<CharacterEquipmentSlot, ItemInstance>())
				.OrderBy(kv => (uint)kv.Key)
				.Select(kv => BuildEntry(kv.Key, kv.Value.Template))
				.ToList();
		}
	}
}
