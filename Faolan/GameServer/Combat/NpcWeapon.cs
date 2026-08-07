using System.Collections.Frozen;
using System.Collections.Generic;
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
		public const uint Bow = 30017;
		public const uint Crossbow = 30018;
		public const uint Thrown = 30019;

		// #623: how often retail puts a combo on an NPC's melee swing. Measured over the corpus
		// (`--report combo-swings`): 427 of 1,504 s2c `Combat:Melee` acks whose actor is a named NPC carry a
		// `combo_id`. The rest are plain swings, so a mob that combos every swing reads nothing like retail's.
		//
		// `Combat:Unarmed` is 0 of 769 - an animal has no weapon, so it has no combo, which falls out of the weapon
		// lookup below rather than needing its own rule.
		public const double MeleeComboShare = 427.0 / 1504.0;

		// The same rate for a SHOT, and it is materially lower: 57 of the 418 captured `Combat:Ranged` acks carry a
		// combo. Every one of those 57 is a `combo_npc_ranger_*` row, because no generic ranged row exists (#676).
		public const double RangedComboShare = 57.0 / 418.0;

		// The client's weapon SEARCH CATEGORIES that are ranged. Only 30017 appears on an NPC in the corpus; the
		// other two are here because they are the same KIND of weapon and a crossbow mob would otherwise silently be
		// treated as melee. That is the direction to be wrong in: the measurement is "a mob that shoots you deals a
		// fifth", not "a mob holding item class 30017 does".
		private static readonly FrozenSet<uint> RangedCategories = new[] { Bow, Crossbow, Thrown }.ToFrozenSet();

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
		// ⚠️ This is the GENERIC set only - the CLASS families are `ClassComboFamily` below, which also records which
		// buckets it deliberately refuses to answer. Gitea #675.
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

		// #675: the CLASS-family `combo_npc_*` rows this NPC may also swing, as the name prefix that selects them, or
		// null when the weapon does not answer which family it is. Retail swings these alongside the generic set - on
		// the same Pillager the corpus has `combo_npc_guardian_polearm_1_sweep` x31 against `combo_npc_generic_polearm`
		// x29 - and their level gate is `Combos.Level` (1/30/50/70), which is why nothing here parses a name.
		//
		// **The archetype is a per-NPC property in general and is NOT recoverable**: it is in no column we hold
		// (`Community`, `MonsterData`, `SuperFamily`, `NpcType`, `Race`, `VoiceSet`, `CarsGroup` and `Flags` all cut
		// straight across the eight observed families), the client ships no NPC-template catalog to read it from, and
		// all 31 combo-swinging templates have zero `NpcSpell` rows. See #675.
		//
		// What IS settled is a subset, and the evidence is the per-SPAWN weapon rather than the per-type gear union -
		// one handle in one capture has one loadout (`--report combo-swings`, the "combo -> the weapon of the actor
		// that swung it" cut):
		//
		//   DAGGER  -> assassin   every one of the 31 captured assassin-family swings is by a dagger-wielder, and all
		//                         8 dagger-armed templates swing the family
		//   POLEARM -> guardian   likewise, 61 swings and all 5 polearm-armed templates
		//
		// ⚠️ And a subset is all it is - three buckets stay ambiguous and are deliberately absent:
		//
		//   SHIELD    the one refuted case. `combo_npc_guardian_shield_*` is swung by 3 of the 6 shield-bearing
		//             templates; the three Vanir ones swing only generic, so a shield does NOT make a guardian.
		//   2h        `barbarian_2h` / `conqueror_2h` / `hox` all appear on two-handers, blunt AND edge, and no
		//             template swings two of them. A per-NPC property with three candidates.
		//   dual 1h   `barbarian_dual` / `conqueror_dual`, same shape.
		//
		// BOW and CROSSBOW need no archetype at all, and are wired since #676 put the shot on its own carrier:
		// `combo_npc_ranger_{bow,crossbow}_*` is the ONLY family at those weapon criteria (26 and 32), there is no
		// `combo_npc_generic_bow` to fall back on, and all 57 captured ranged combos are ranger rows. THROWN (30019)
		// is deliberately absent - no `combo_npc_*_thrown_*` row exists, so a javelin mob throws plain.
		public static string? ClassComboFamily(IEnumerable<INpcEquipment>? equipment)
		{
			return CategoryIn(equipment, MainHandPos) switch
			{
				Dagger   => "combo_npc_assassin_",
				Polearm  => "combo_npc_guardian_polearm_",
				Bow      => "combo_npc_ranger_bow_",
				Crossbow => "combo_npc_ranger_crossbow_",
				_        => null
			};
		}

		// #675: how often a combo swing by an NPC that HAS a class family uses it rather than its generic row. Over
		// the two wired buckets the corpus splits 92 class / 65 generic - the dagger actors 31/13 and the polearm ones
		// 61/52 - so it is a coin flip leaning class, not a preference for either. Applied only after
		// `MeleeComboShare` has already decided the swing carries a combo at all.
		public const double ClassComboShare = 92.0 / (92.0 + 65.0);

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
