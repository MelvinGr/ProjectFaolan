using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Interfaces;

namespace Faolan.GameServer.Combat
{
	// What an NPC is holding, as far as COMBAT cares (#671). Today that is one question - is it shooting or
	// swinging - because retail's ranged mobs deal about a fifth of what a melee mob of the same level does, and
	// nothing else about the weapon reaches the damage line (an NPC's swing is `level * 100`, not a weapon roll).
	//
	// Answered from the item CATALOG rather than from a name: `Items.SearchCategory` is the client's own weapon
	// class, so `NPC_-_Bow_(Male)`, `NPC_Aquilonia_na_medium_bow_(W0061)` and `Pict War Bow` all answer the same way
	// without anyone matching on "bow". Matching on names is the #244 trap wearing a new hat.
	public static class NpcWeapon
	{
		// The equipment slot an NPC's weapon sits in. Every one of the 258 captured bow placements is at Pos 6 and
		// none at any other slot, and the melee weapons sit there too (`Pos 8` is the off-hand: a shield, a hatchet,
		// or a quiver). So the main hand alone decides, and a quiver in the off-hand is not needed as a second signal.
		public const uint MainHandPos = 6;

		// The client's weapon SEARCH CATEGORIES that are ranged. From the shipped catalog, where the 300xx block is
		// one class per value: 30011 1h-edge, 30012 1h-blunt, 30013 2h-edge, 30014 2h-blunt, 30015 dagger,
		// 30016 polearm, and then these three.
		//
		// Only 30017 appears on an NPC in the corpus; the other two are here because they are the same KIND of
		// weapon and a crossbow mob would otherwise silently be treated as melee. That is the direction to be wrong
		// in: the measurement is "a mob that shoots you deals a fifth", not "a mob holding item class 30017 does".
		private static readonly FrozenSet<uint> RangedCategories = new uint[] { 30017, 30018, 30019 }.ToFrozenSet();

		// Whether this NPC attacks at range - i.e. whether its MAIN HAND holds a bow/crossbow/thrown weapon.
		// False for an unarmed NPC and for every animal, which is what makes this safe to ask of any spawn.
		public static bool IsRanged(IEnumerable<INpcEquipment>? equipment)
		{
			if (equipment == null)
				return false;

			foreach (var row in equipment)
			{
				// `Item` is an FK navigation; a row whose Item is not Included would throw, so ask the ROW for the
				// slot first and only touch the item on the one that matters.
				if (row.Pos == MainHandPos && row.Item != null && RangedCategories.Contains(row.Item.SearchCategory))
					return true;
			}

			return false;
		}
	}
}
