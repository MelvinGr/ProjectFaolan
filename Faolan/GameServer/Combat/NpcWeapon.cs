using System.Collections.Frozen;
using System.Collections.Generic;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Enums;

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

		// Every category that IS a weapon - the seven melee classes plus the three ranged. Shield is deliberately out:
		// carrying one is not being armed, and no NPC in the shipped data holds only a shield.
		private static readonly FrozenSet<uint> WeaponCategories =
			new[] { OneHandEdge, TwoHandEdge, OneHandBlunt, TwoHandBlunt, Dagger, Polearm, Bow, Crossbow, Thrown }.ToFrozenSet();

		// Whether this NPC attacks at range - i.e. whether its MAIN HAND holds a bow/crossbow/thrown weapon.
		// False for an unarmed NPC and for every animal, which is what makes this safe to ask of any spawn.
		public static bool IsRanged(IEnumerable<INpcEquipment>? equipment)
		{
			return RangedCategories.Contains(CategoryIn(equipment, MainHandPos));
		}

		/// <summary>
		///     Whether a hostile-coloured NPC actually ENGAGES a player who walks past, as opposed to merely being
		///     coloured hostile. False for an armed humanoid carrying nothing in either hand - a civilian.
		/// </summary>
		/// <remarks>
		///     ⭐ **The colour and the aggression are two different facts and retail keeps them apart** (#822). The
		///     Town Crier (14572) is `cCategory = 1` unanimously - 62 votes across two captures and two characters -
		///     and never fights: he spawns and despawns as the player walks past him for a whole session with no
		///     attack, no hate list, no threat. Reading one flag as both is what made him attack.
		///     <para />
		///     ⚠️ Two candidate discriminators were tried and failed before this one. **Perception** (`SightSkill` and
		///     friends being NULL) is a distinctive 7% of hostiles, but that 7% contains the Crow gang, the Wharf Rat
		///     gang and the Nemedian Scouts - genuinely aggressive mobs - so it cannot mean passive. **Combat
		///     capability alone** is not it either: 102 hostile templates carry no weapon and most are animals, which
		///     fight perfectly well without one.
		///     <para />
		///     ⭐ It is the CONJUNCTION that separates them, and it was validated on the very population that killed
		///     the perception idea. Of the 18 hostile templates with no senses at all, every genuine mob is ARMED -
		///     Crow Harrier/Nightblade/Roofhugger/Stalker, Nemedian Archer and three Scouts, five Wharf Rats, Wulfhere
		///     (14 of 14) - and every civilian is unarmed: Aelfrith, Ambassador Nidas, Chancellor Publius, Dexitheus
		///     and the Town Crier. `Frozen Hatred` and `Parasitic Soul` are unarmed too and stay aggressive, because
		///     they are not humanoid.
		///     <para />
		///     Across all 352 hostile templates the rule selects TEN: the four level-80 named city NPCs above, two
		///     Dockworkers, two Slaves of Acheron, the Town Crier, and one uncertain case (`Vanir Acolyte`, level 20).
		///     Nine of ten are unmistakably civilians.
		///     <para />
		///     ⚠️ It gates INITIATION only. A passive NPC still fights back when attacked - `RecentAttackerOf` is not
		///     behind this gate - so this cannot make anything unkillable or inert, only unprovoked.
		///     <para />
		///     ⚠️ `humanoid` comes from whether the spawn carries a MORPH table, which is harvest-dependent: a
		///     humanoid whose morphs were never captured reads as an animal and stays aggressive. That errs toward
		///     leaving mobs alone rather than pacifying them, which is the safe direction for a rule this shape.
		/// </remarks>
		public static bool Initiates(IEnumerable<INpcEquipment>? equipment, bool humanoid)
		{
			if (!humanoid)
				return true; // an animal needs no weapon to mean it

			// ⚠️ A WEAPON category, not "the slot holds something". The hand slots carry other things - the Town
			// Crier's own equipment rows include a necklace and shoulders - so a non-empty test would call him armed
			// and the rule would select nothing at all.
			return WeaponCategories.Contains(CategoryIn(equipment, MainHandPos)) || WeaponCategories.Contains(CategoryIn(equipment, OffHandPos));
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
		// ⚠️ **This is the FALLBACK.** The archetype is a per-TEMPLATE property that is in no column the spawn body
		// carries (`Community`, `MonsterData`, `SuperFamily`, `NpcType`, `Race`, `VoiceSet`, `CarsGroup` and `Flags`
		// all cut straight across the eight observed families) and in no client table, so it is HARVESTED into
		// `NpcStatCore.ComboClass` from the captured swings (#675). Where that column is set, `NpcClassComboIds`
		// selects on it and the weapon only narrows through `MeetsComboCriteria`; this switch answers the ~97% of
		// templates no capture ever caught swinging one.
		//
		// What the WEAPON settles on its own is a subset, and the evidence is the per-SPAWN weapon rather than the
		// per-type gear union - one handle in one capture has one loadout (`--report combo-swings`, the "combo -> the
		// weapon of the actor that swung it" cut):
		//
		//   DAGGER  -> assassin   every one of the 31 captured assassin-family swings is by a dagger-wielder, and all
		//                         8 dagger-armed templates swing the family
		//   POLEARM -> guardian   likewise, 61 swings and all 5 polearm-armed templates
		//
		// ⚠️ And a subset is all it is - three buckets a weapon rule cannot express, which is what the harvested
		// archetype answers instead:
		//
		//   SHIELD    the one REFUTED case, and the archetype says why. `combo_npc_guardian_shield_*` is swung by 3 of
		//             the 6 shield-bearing templates because the other three are not Guardians - Vanir Champion Elite
		//             wears a shield and swings the BARBARIAN fatality rows. A shield does not make a guardian.
		//   2h        `barbarian_2h` / `conqueror_2h` / `hox` all appear on two-handers, blunt AND edge, and no
		//             template swings two of them - a per-TEMPLATE property with three candidates.
		//   dual 1h   `barbarian_dual` / `conqueror_dual`, same shape.
		//
		// BOW and CROSSBOW need no archetype at all, and are wired since #676 put the shot on its own carrier:
		// `combo_npc_ranger_{bow,crossbow}_*` is the ONLY family at those weapon criteria (26 and 32), there is no
		// `combo_npc_generic_bow` to fall back on, and all 57 captured ranged combos are ranger rows. THROWN (30019)
		// is deliberately absent - no `combo_npc_*_thrown_*` row exists, so a javelin mob throws plain.
		//
		// ⚠️ The CLIENT states each row's class in the combo XML (`Combo.NpcClass`), and it does NOT state a weapon
		// requirement for the assassin family - `combo_npc_assassin_*` carries no `criteria` attribute at all, the
		// way `bear_shaman` / `dark_templar` / `hox` do not. So `Dagger -> assassin` is a CAPTURE correlation (8 of 8
		// dagger-armed templates swing it) presented as a weapon rule, and a dagger-armed non-assassin would swing
		// the wrong family. `Polearm` / `Bow` / `Crossbow` are on firmer ground: their rows DO state criteria 25 / 26
		// / 32, and at each of those criteria the class family is unique. #675.
		public static (string NamePrefix, ConanClass Class)? ClassComboFamily(IEnumerable<INpcEquipment>? equipment)
		{
			return CategoryIn(equipment, MainHandPos) switch
			{
				Dagger   => ("combo_npc_assassin_", ConanClass.Assassin),
				Polearm  => ("combo_npc_guardian_polearm_", ConanClass.Guardian),
				Bow      => ("combo_npc_ranger_bow_", ConanClass.Ranger),
				Crossbow => ("combo_npc_ranger_crossbow_", ConanClass.Ranger),
				_        => null
			};
		}

		// #675: whether this NPC's loadout satisfies a class combo's own weapon `criteria` - the client's `criteria`
		// attribute, which is what replaces the name-prefix reading once `NpcStatCore.ComboClass` says WHICH class.
		// `0` is no requirement at all and is what the assassin / bear-shaman / dark-templar / hox / heimdall families
		// carry, so those swing on any weapon (the corpus has dark_templar swung with a 1hEdge and hox with a 2hEdge).
		//
		// ⚠️ **`28` is widened to ANY two-hander, and that is capture-driven rather than a reading of the number.**
		// On the generic set 28 is `2he` and 29 is `2hb`, but retail swings the criteria-28 CLASS rows
		// (`barbarian_2h`, `conqueror_2h`) with two-handed BLUNT weapons in the corpus - LargeWarclub, Warhammer,
		// HeavyBludgeon, SledgeHammer, BattleMaul - so on a class family it reads as "a two-hander". Refusing 2hb
		// there would silence exactly the mobs the archetype was harvested from.
		public static bool MeetsComboCriteria(int criteria, IEnumerable<INpcEquipment>? equipment)
		{
			var main = CategoryIn(equipment, MainHandPos);
			var off = CategoryIn(equipment, OffHandPos);

			return criteria switch
			{
				0  => true,
				23 => main == OneHandEdge,
				25 => main == Polearm,
				26 => main == Bow,
				27 => main == OneHandBlunt,
				28 => main is TwoHandEdge or TwoHandBlunt,
				29 => main == TwoHandBlunt,
				30 => main == Dagger,
				32 => main == Crossbow,
				52 => main is OneHandEdge or OneHandBlunt or Dagger && off is OneHandEdge or OneHandBlunt or Dagger,
				_  => false // an unseen criteria code is a gap, and swinging on it would be a guess
			};
		}

		// #675: how often a combo swing by an NPC that HAS a class family uses it rather than its generic row. Over
		// the two wired buckets the corpus splits 92 class / 65 generic - the dagger actors 31/13 and the polearm ones
		// 61/52 - so it is a coin flip leaning class, not a preference for either. Applied only after
		// `MeleeComboShare` has already decided the swing carries a combo at all.
		public const double ClassComboShare = 92.0 / (92.0 + 65.0);

		// The item class worn in `slot`, or 0 when the slot is empty. `Item` is an FK navigation; a row whose Item is
		// not Included would throw, so ask the ROW for the slot first and only touch the item on the one that matters.
		/// <summary>
		///     How far this NPC can attack from, in world units - its MAIN HAND weapon's `Attack Range` (#838), or
		///     <paramref name="meleeDefault" /> when it holds nothing that states one.
		/// </summary>
		/// <remarks>
		///     The client's own item stat, property field 287 (`CharacterStatId.AttackRange`), stored x100. Measured
		///     across the NPC weapons in the corpus it is a clean split - every melee weapon reads 200 (2.0 units) and
		///     every bow 1750 (17.5) - which is what lets an archer hold its distance instead of running up to swing.
		///     <para>
		///         It is the WEAPON's number rather than an AI constant because the client has no AI stand-off tuning
		///         (`--report rdb-tweaks` names `AIDefaults_MaximumChaseRadius` and the deaggro delay, nothing about
		///         range) and a mob's attack record names no target, so the distance cannot be measured off the corpus
		///         either. The weapon is where retail keeps it.
		///     </para>
		/// </remarks>
		public static float AttackRange(IEnumerable<INpcEquipment>? equipment, float meleeDefault)
		{
			if (equipment == null)
				return meleeDefault;

			foreach (var row in equipment)
				if (row.Pos == MainHandPos && row.Item?.AttackRange is > 0 and var range)
					return range / 100f;

			return meleeDefault;
		}

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
