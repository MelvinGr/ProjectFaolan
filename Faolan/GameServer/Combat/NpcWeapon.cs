using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Interfaces;

namespace Faolan.GameServer.Combat
{
	// What an NPC is holding, as far as COMBAT cares (#671, #623). Two questions live here - is it shooting or
	// swinging, and which melee COMBO its swings carry - and both are answered from the same place, because both are
	// properties of the weapon rather than of the creature.
	//
	// Answered from the item CATALOG rather than from a name: `Items.SearchCategory` is the client's own weapon
	// class, so `NPC_-_Bow_(Male)`, `NPC_Aquilonia_na_medium_bow_(W0061)` and `Pict War Bow` all answer the same way
	// without anyone matching on "bow". Matching on names is the #244 trap wearing a new hat.
	public static class NpcWeapon
	{
		// The equipment slots an NPC's weapons sit in. Over the whole shipped spawn set NO weapon-class item appears
		// in any other slot: Pos 6 holds 1,451 weapons and nothing else, Pos 8 holds a shield (288), a quiver (271)
		// or a second weapon (170). Pos 7 is the belt, which is why a naive "the low slots are the weapons" read
		// picks up girdles.
		public const uint MainHandPos = 6;
		public const uint OffHandPos = 8;

		// The client's weapon SEARCH CATEGORIES. The 300xx block is one class per value, and the shipped catalog
		// separates them perfectly - every 30011 name carries the `1hEdge` token, every 30012 `2hEdge`, and so on
		// across all seven, with no row in two buckets.
		public const uint Shield = 30005;
		public const uint OneHandEdge = 30011;
		public const uint TwoHandEdge = 30012;
		public const uint OneHandBlunt = 30013;
		public const uint TwoHandBlunt = 30014;
		public const uint Dagger = 30015;
		public const uint Polearm = 30016;

		// #623: how often retail puts a combo on an NPC's melee swing. Measured over the corpus
		// (`--report combo-swings`): 427 of 1,504 s2c `Combat:Melee` acks whose actor is a named NPC carry a
		// `combo_id`. The rest are plain swings, so a mob that combos every swing reads nothing like retail's.
		//
		// `Combat:Unarmed` is 0 of 769 - an animal has no weapon, so it has no combo, which falls out of the weapon
		// lookup below rather than needing its own rule. `Combat:Ranged` is 57 of 418 (13.6%) and is NOT modelled:
		// the ranged combos are the `combo_npc_ranger_{bow,crossbow}_*` class family, and there is no
		// `combo_npc_generic_bow` row to fall back on.
		public const double MeleeComboShare = 427.0 / 1504.0;

		// The client's weapon SEARCH CATEGORIES that are ranged. Only 30017 appears on an NPC in the corpus; the
		// other two are here because they are the same KIND of weapon and a crossbow mob would otherwise silently be
		// treated as melee. That is the direction to be wrong in: the measurement is "a mob that shoots you deals a
		// fifth", not "a mob holding item class 30017 does".
		private static readonly FrozenSet<uint> RangedCategories = new uint[] { 30017, 30018, 30019 }.ToFrozenSet();

		// Whether this NPC attacks at range - i.e. whether its MAIN HAND holds a bow/crossbow/thrown weapon.
		// False for an unarmed NPC and for every animal, which is what makes this safe to ask of any spawn.
		public static bool IsRanged(IEnumerable<INpcEquipment>? equipment)
		{
			return RangedCategories.Contains(CategoryIn(equipment, MainHandPos));
		}

		// #623: the `Combos` row name whose id retail puts on this NPC's melee swings, or null when it has no melee
		// weapon (unarmed, an animal, or an archer - see MeleeComboShare).
		//
		// The whole `combo_npc_generic_*` set is exactly these nine, and the corpus says the choice is the WEAPON's,
		// not the creature's: over the 484 captured NPC combo swings, every actor holding a polearm swung
		// `generic_polearm`, every 2h-blunt one `generic_2hb`, and so on with no crossover - and one NPC TYPE with
		// two loadouts (Bat Pict Mauler, template 8799 polearm vs 13960 club) swings the two different combos.
		// The off hand only ever refines the one-handed cases, which is why it is consulted there and nowhere else.
		//
		// ⚠️ This is the GENERIC set only. Retail also swings per-class families (`combo_npc_guardian_polearm_1_sweep`,
		// `combo_npc_assassin_unholy_30_doomstrike`, ...) roughly half the time on the same mobs, and their trailing
		// number is a minimum LEVEL gate (1/30/50/70, with no violation across the 30 captured rows). What is missing
		// is the class ARCHETYPE per NPC - it is not the weapon (a 2h-edge mob swings `barbarian_2h` or `hox`
		// depending on the creature) and `NpcStatCore.ProfessionType` is null on all but one of them. Gitea #675.
		public static string? MeleeComboName(IEnumerable<INpcEquipment>? equipment)
		{
			var main = CategoryIn(equipment, MainHandPos);
			var off = CategoryIn(equipment, OffHandPos);

			return main switch
			{
				OneHandEdge when off == Shield      => "combo_npc_generic_1he_shield",
				OneHandEdge when off == OneHandEdge => "combo_npc_generic_dual_1h",
				OneHandEdge                         => "combo_npc_generic_1he",
				OneHandBlunt                        => "combo_npc_generic_1hb",
				TwoHandEdge                         => "combo_npc_generic_2he",
				TwoHandBlunt                        => "combo_npc_generic_2hb",
				Dagger when off == Dagger           => "combo_npc_generic_dualdaggers",
				Dagger                              => "combo_npc_generic_dagger",
				Polearm                             => "combo_npc_generic_polearm",
				_                                   => null
			};
		}

		// The item class worn in `slot`, or 0 when the slot is empty. `Item` is an FK navigation; a row whose Item is
		// not Included would throw, so ask the ROW for the slot first and only touch the item on the one that matters.
		private static uint CategoryIn(IEnumerable<INpcEquipment>? equipment, uint slot)
		{
			if (equipment == null)
				return 0;

			foreach (var row in equipment)
				if (row.Pos == slot && row.Item != null)
					return row.Item.SearchCategory;

			return 0;
		}
	}
}
