using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;

namespace Faolan.GameServer.Inventory
{
	// Which equipment slot does this item go into?
	//
	// The answer is entirely in the client's own item-template property table, stored on `Item` by `RdbItemImporter`:
	// `Placement` (a bitmask of `CharacterEquipmentSlot` ids the item may occupy, 0 = not equippable), `DefaultPos`
	// (the preferred one when the mask offers several) and `OccupySlots` (everything consumed once worn). All three
	// are named by the client's stat-name table and carried by all 5,071 decrypted templates; see `Item.Placement`
	// for how the reading was validated against every worn item the corpus records.
	//
	// A hand bit is not a slot: `Placement` bits 6 and 8 mean "a main hand" and "an off hand", while slots 20/21 are
	// weapon set 2 and are set-neutral - either set holds any weapons - so a hand bit resolves to set 1 (6/8) or set 2
	// (20/21) by what is free rather than by anything about the item. That is the one decision this class makes that
	// the template does not; everything else it reads off.
	public static class EquipSlotResolver
	{
		// The two weapon sets, main hand then off hand. Set 1 is `Placement`'s own numbering, which is why a hand bit is
		// a slot id there and a translation here.
		private static readonly CharacterEquipmentSlot[][] WeaponSets =
		[
			[CharacterEquipmentSlot.MainWeapon, CharacterEquipmentSlot.OffHandWeapon],
			[CharacterEquipmentSlot.RangedWeapon, CharacterEquipmentSlot.Ammo]
		];

		private const int MainHandBit = (int)CharacterEquipmentSlot.MainWeapon;
		private const int OffHandBit = (int)CharacterEquipmentSlot.OffHandWeapon;

		/// <summary>Every slot the item's <c>Placement</c> mask names, preferred slot first.</summary>
		public static IReadOnlyList<CharacterEquipmentSlot> CandidateSlots(Item item)
		{
			var bits = SetBits(item.Placement).ToList();
			if (bits.Count == 0)
				return [];

			// `DefaultPos` wins when it is one of them: it is not derivable from the mask (45 off-hand focus items carry
			// the one-hander mask 6|8 and default to the off hand), so it has to be read rather than computed.
			var ordered = bits.OrderBy(b => b == item.DefaultPos ? 0 : 1).ThenBy(b => b).ToList();

			var slots = new List<CharacterEquipmentSlot>();
			foreach (var bit in ordered)
			{
				if (bit is MainHandBit or OffHandBit)
				{
					// A hand bit stands for that hand in either set, set 1 first.
					var hand = bit == MainHandBit ? 0 : 1;
					foreach (var set in WeaponSets)
						slots.Add(set[hand]);
					continue;
				}

				slots.Add((CharacterEquipmentSlot)bit);
			}

			return slots;
		}

		/// <summary>
		///     The first candidate slot that is free, given the slots already worn. Null when the item is not
		///     equippable at all, or when every slot it could take is occupied.
		/// </summary>
		public static CharacterEquipmentSlot? ResolveFreeSlot(Item item, IReadOnlySet<CharacterEquipmentSlot> occupied)
		{
			foreach (var slot in CandidateSlots(item))
			{
				if (occupied.Contains(slot))
					continue;

				// A two-hander needs its partner slot free too, or equipping it would silently displace whatever is
				// there. `OccupySlots` names the extra hand (280 templates - 2h edge/blunt, polearms, thrown).
				if (Occupies(item, slot).Any(occupied.Contains))
					continue;

				return slot;
			}

			return null;
		}

		/// <summary>
		///     Every slot worn in <paramref name="slot" /> consumes, excluding <paramref name="slot" /> itself.
		///     Empty for everything but a two-hander.
		/// </summary>
		public static IReadOnlyList<CharacterEquipmentSlot> Occupies(Item item, CharacterEquipmentSlot slot)
		{
			var extra = item.OccupySlots & ~item.Placement;
			if (extra == 0)
				return [];

			// The extra bits are stated in set 1's numbering; a weapon worn in set 2 consumes set 2's partner.
			var set = WeaponSets.FirstOrDefault(s => s.Contains(slot)) ?? WeaponSets[0];
			return SetBits(extra)
				.Select(bit => bit == MainHandBit ? set[0] : bit == OffHandBit ? set[1] : (CharacterEquipmentSlot)bit)
				.Where(s => s != slot)
				.ToList();
		}

		private static IEnumerable<int> SetBits(uint mask)
		{
			for (var bit = 0; bit < 32; bit++)
			{
				if ((mask >> bit & 1) != 0)
					yield return bit;
			}
		}
	}
}
