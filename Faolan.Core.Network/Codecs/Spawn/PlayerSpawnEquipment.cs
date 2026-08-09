using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// Builds the worn-equipment InventoryEntries for a player's SimpleCharFullUpdate spawn from the character's
	// EQUIPPED item instances. Each worn item becomes a ProtoCoreCharacterIIR.InventoryEntry { pos = the equip slot id,
	// item = ProtoAcgItem(visual) } - the same Item -> ProtoAcgItem encoding the inventory snapshot uses
	// (SnapshotItems): keys = the item's Data1..4 quad, templates = Item.Id, hash = hash. Replaces the
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
					// The item's own level, exactly as the snapshot entry carries it. Measured over the DUMPS (#755,
					// `--report spawn-item-level`): 125,721 of 125,722 captured worn-item entries across 23,478
					// character spawns carry EXACTLY their template's `Items.Level`, with zero disagreements and
					// zero templates carrying two different levels; the one exception is an entry whose template is
					// not in our catalog at all.
					//
					// It was hardcoded `1` until then, and the snapshot entry's identical 8,479/8,479 result was
					// deliberately NOT assumed to transfer - this is a different message written by a different part
					// of retail's server. It had to be measured, and the reason nobody noticed is in the histogram:
					// level 1 is 108,819 of the 125,722 entries, so the constant was right 87% of the time.
					Level = item.Level,
					Hash = AcgHash.ForWire(item.Id, item.Hash)
				}
			};
		}

		// `worn` is the character's Equipped container as slot -> instance (IDatabaseRepository.GetEquippedItems). The
		// 17 worn slots and the 7 vanity slots are treated alike, so vanity items ride the spawn at their equip-slot
		// pos (1002+). The slot id IS the enum value (pos in the capture: MainWeapon = main_hand_left 6, the ranged
		// pair 20/21, vanity 1002+).
		public static List<ProtoCoreCharacterIIR.InventoryEntry> BuildInventoryEntries(IReadOnlyDictionary<CharacterEquipmentSlot, ItemInstance>? worn)
		{
			// This used to REFUSE an item with no harvested hash, on the reasoning that a hash is harvested content so
			// a worn item lacking one is a gap in Items rather than a state to render around. The refusal was right
			// while the token could only be harvested; #723 removed that premise - the token carries no information and
			// the client validates nothing, so BuildEntry mints one and the wearer and everybody else now see the same
			// piece either way.
			//
			// Every worn + vanity item ships - NOTHING is withheld for the vanity filter. Show/hide is driven purely by
			// the bitmask in the spawn header (Character.VanityShownMask, emitted by PlayerSpawnScaffoldRows); the
			// client renders it off that mask (#336).
			return (worn ?? new Dictionary<CharacterEquipmentSlot, ItemInstance>())
				.OrderBy(kv => (uint)kv.Key)
				.Select(kv => BuildEntry(kv.Key, kv.Value.Template))
				.ToList();
		}
	}
}
