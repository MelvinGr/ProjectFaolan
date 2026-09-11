using System.Collections.Frozen;
using System.Collections.Generic;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Enums;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     What an NPC is holding, as far as combat cares: whether it shoots or swings, and which melee combo its
	///     swings carry. Both are properties of the weapon rather than of the creature, so both are answered here.
	/// </summary>
	/// <remarks>
	///     Answered from the item catalog rather than from a name: <c>Items.SearchCategory</c> is the client's own
	///     weapon class, so <c>NPC_-_Bow_(Male)</c>, <c>NPC_Aquilonia_na_medium_bow_(W0061)</c> and
	///     <c>Pict War Bow</c> all answer the same way without anyone matching on "bow".
	/// </remarks>
	public static class NpcWeapon
	{
		// The equipment slots an NPC's weapons sit in. Over the whole shipped spawn set no weapon-class item appears
		// in any other slot: Pos 6 holds 1,451 weapons and nothing else, Pos 8 holds a shield (288), a quiver (271) or
		// a second weapon (170). Pos 7 is the belt, which is why reading "the low slots are the weapons" picks up
		// girdles.
		public const uint MainHandPos = 6;
		public const uint OffHandPos = 8;

		// The client's weapon search categories. The 300xx block is one class per value, and the shipped catalog
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

		// How often retail puts a combo on an NPC's melee swing. Measured over the corpus (`--report combo-swings`):
		// 427 of 1,504 s2c `Combat:Melee` acks whose actor is a named NPC carry a `combo_id`. The rest are plain
		// swings, so a mob that combos every swing reads nothing like retail's.
		//
		// `Combat:Unarmed` is 0 of 769 - an animal has no weapon, so it has no combo, which falls out of the weapon
		// lookup below rather than needing its own rule.
		public const double MeleeComboShare = 427.0 / 1504.0;

		// The same rate for a shot, and it is materially lower: 57 of the 418 captured `Combat:Ranged` acks carry a
		// combo. Every one of those 57 is a `combo_npc_ranger_*` row, because no generic ranged row exists.
		public const double RangedComboShare = 57.0 / 418.0;

		// The ranged weapon categories. Only 30017 appears on an NPC in the corpus; the other two are here because
		// they are the same kind of weapon and a crossbow mob would otherwise silently be treated as melee. That is
		// the direction to be wrong in: the measurement is "a mob that shoots you deals a fifth", not "a mob holding
		// item class 30017 does".
		private static readonly FrozenSet<uint> RangedCategories = new[] { Bow, Crossbow, Thrown }.ToFrozenSet();

		// Every category that is a weapon - the seven melee classes plus the three ranged. Shield is deliberately out:
		// carrying one is not being armed, and no NPC in the shipped data holds only a shield.
		private static readonly FrozenSet<uint> WeaponCategories =
			new[] { OneHandEdge, TwoHandEdge, OneHandBlunt, TwoHandBlunt, Dagger, Polearm, Bow, Crossbow, Thrown }.ToFrozenSet();

		/// <summary>
		///     Whether this NPC attacks at range - that is, whether its main hand holds a bow, crossbow or thrown
		///     weapon. False for an unarmed NPC and for every animal, which makes this safe to ask of any spawn.
		/// </summary>
		public static bool IsRanged(IEnumerable<INpcEquipment>? equipment)
		{
			return RangedCategories.Contains(CategoryIn(equipment, MainHandPos));
		}

		/// <summary>
		///     Whether a hostile-coloured NPC actually engages a player who walks past, as opposed to merely being
		///     coloured hostile. False for a humanoid carrying nothing in either hand - a civilian.
		/// </summary>
		/// <remarks>
		///     <para>
		///         The colour and the aggression are two different facts, and retail keeps them apart: the Town Crier
		///         (14572) is <c>cCategory = 1</c> unanimously - 62 votes across two captures and two characters - and
		///         never fights, spawning and despawning as the player walks past him for a whole session with no
		///         attack, no hate list and no threat.
		///     </para>
		///     <para>
		///         Neither half of the rule works alone. Perception (<c>SightSkill</c> and friends being null) is a
		///         distinctive 7% of hostiles, but that 7% contains the Crow gang, the Wharf Rat gang and the Nemedian
		///         Scouts - genuinely aggressive mobs. Combat capability alone is not it either: 102 hostile templates
		///         carry no weapon, and most of those are animals, which fight perfectly well without one.
		///     </para>
		///     <para>
		///         The conjunction separates them, validated on the population that refutes the perception reading: of
		///         the 18 hostile templates with no senses at all, every genuine mob is armed - Crow
		///         Harrier/Nightblade/Roofhugger/Stalker, Nemedian Archer and three Scouts, five Wharf Rats, Wulfhere
		///         (14 of 14) - and every civilian is unarmed: Aelfrith, Ambassador Nidas, Chancellor Publius,
		///         Dexitheus and the Town Crier. <c>Frozen Hatred</c> and <c>Parasitic Soul</c> are unarmed too and
		///         stay aggressive, because they are not humanoid. Across all 352 hostile templates the rule selects
		///         ten: the four level-80 named city NPCs above, two Dockworkers, two Slaves of Acheron, the Town
		///         Crier, and one uncertain case (<c>Vanir Acolyte</c>, level 20).
		///     </para>
		///     <para>
		///         It gates initiation only. A passive NPC still fights back when attacked - the recent-attacker path
		///         is not behind this gate - so it cannot make anything unkillable or inert, only unprovoked.
		///     </para>
		///     <para>
		///         <paramref name="humanoid" /> comes from whether the spawn carries a morph table, which is
		///         harvest-dependent: a humanoid whose morphs were never captured reads as an animal and stays
		///         aggressive. That errs toward leaving mobs alone rather than pacifying them, which is the safe
		///         direction for a rule this shape.
		///     </para>
		/// </remarks>
		public static bool Initiates(IEnumerable<INpcEquipment>? equipment, bool humanoid)
		{
			if (!humanoid)
				return true; // an animal needs no weapon to mean it

			// A weapon category, not "the slot holds something". The hand slots carry other things - the Town Crier's
			// own equipment rows include a necklace and shoulders - so a non-empty test would call him armed and the
			// rule would select nothing at all.
			return WeaponCategories.Contains(CategoryIn(equipment, MainHandPos)) || WeaponCategories.Contains(CategoryIn(equipment, OffHandPos));
		}

		/// <summary>
		///     The <c>Combos</c> row name whose id retail puts on this NPC's melee swings, or null when it has no melee
		///     weapon - unarmed, an animal, or an archer (see <see cref="MeleeComboShare" />).
		/// </summary>
		/// <remarks>
		///     The whole <c>combo_npc_generic_*</c> set is exactly these nine, and the corpus says the choice is the
		///     weapon's rather than the creature's: over the 484 captured NPC combo swings every actor holding a
		///     polearm swung <c>generic_polearm</c>, every 2h-blunt one <c>generic_2hb</c>, and so on with no
		///     crossover - and one NPC type with two loadouts (Bat Pict Mauler, template 8799 polearm against 13960
		///     club) swings the two different combos. The off hand only refines the one-handed cases, which is why it
		///     is consulted there and nowhere else. This is the generic set only; the class families are
		///     <see cref="ClassComboFamily" />.
		/// </remarks>
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

		/// <summary>
		///     The class-family <c>combo_npc_*</c> rows this NPC may also swing, as the name prefix that selects them,
		///     or null when the weapon does not answer which family it is.
		/// </summary>
		/// <remarks>
		///     <para>
		///         Retail swings these alongside the generic set - on the same Pillager the corpus has
		///         <c>combo_npc_guardian_polearm_1_sweep</c> x31 against <c>combo_npc_generic_polearm</c> x29 - and
		///         their level gate is <c>Combos.Level</c> (1/30/50/70), which is why nothing here parses a name.
		///     </para>
		///     <para>
		///         This is the fallback. The archetype is a per-template property that is in no column the spawn body
		///         carries (<c>Community</c>, <c>MonsterData</c>, <c>SuperFamily</c>, <c>NpcType</c>, <c>Race</c>,
		///         <c>VoiceSet</c>, <c>CarsGroup</c> and <c>Flags</c> all cut straight across the eight observed
		///         families) and in no client table, so it is harvested into <c>NpcStatCore.ComboClass</c> from the
		///         captured swings. Where that column is set, <c>NpcClassComboIds</c> selects on it and the weapon
		///         only narrows through <see cref="MeetsComboCriteria" />; this switch answers the roughly 97% of
		///         templates no capture ever caught swinging one.
		///     </para>
		///     <para>
		///         What the weapon settles on its own is a subset, and the evidence is the per-spawn weapon rather
		///         than the per-type gear union, since one handle in one capture has one loadout
		///         (<c>--report combo-swings</c>, the "combo -&gt; the weapon of the actor that swung it" cut): dagger
		///         gives assassin, where all 31 captured assassin-family swings are by a dagger-wielder and all 8
		///         dagger-armed templates swing the family; polearm gives guardian, likewise 61 swings and all 5
		///         polearm-armed templates.
		///     </para>
		///     <para>
		///         Three buckets a weapon rule cannot express, which the harvested archetype answers instead: a
		///         shield is refuted outright, since <c>combo_npc_guardian_shield_*</c> is swung by 3 of the 6
		///         shield-bearing templates and the other three are not Guardians - Vanir Champion Elite wears a
		///         shield and swings the Barbarian fatality rows; a two-hander carries <c>barbarian_2h</c>,
		///         <c>conqueror_2h</c> and <c>hox</c>, blunt and edge alike, with no template swinging two of them;
		///         and dual one-handers have the same shape with <c>barbarian_dual</c> and <c>conqueror_dual</c>.
		///     </para>
		///     <para>
		///         Bow and crossbow need no archetype: <c>combo_npc_ranger_{bow,crossbow}_*</c> is the only family at
		///         those weapon criteria (26 and 32), there is no <c>combo_npc_generic_bow</c> to fall back on, and
		///         all 57 captured ranged combos are ranger rows. Thrown (30019) is deliberately absent - no
		///         <c>combo_npc_*_thrown_*</c> row exists, so a javelin mob throws plain.
		///     </para>
		///     <para>
		///         The client states each row's class in the combo XML (<c>Combo.NpcClass</c>) and states no weapon
		///         requirement for the assassin family - <c>combo_npc_assassin_*</c> carries no <c>criteria</c>
		///         attribute at all, as <c>bear_shaman</c>, <c>dark_templar</c> and <c>hox</c> do not. So
		///         dagger-to-assassin is a capture correlation (8 of 8 dagger-armed templates swing it) presented as a
		///         weapon rule, and a dagger-armed non-assassin would swing the wrong family. Polearm, bow and
		///         crossbow are firmer: their rows state criteria 25, 26 and 32, and at each of those the class family
		///         is unique.
		///     </para>
		/// </remarks>
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

		/// <summary>
		///     Whether this NPC's loadout satisfies a class combo's own weapon <c>criteria</c> - the client's
		///     attribute, which replaces the name-prefix reading once <c>NpcStatCore.ComboClass</c> says which class.
		/// </summary>
		/// <remarks>
		///     <para>
		///         0 is no requirement at all and is what the assassin, bear-shaman, dark-templar, hox and heimdall
		///         families carry, so those swing on any weapon - the corpus has dark_templar swung with a 1hEdge and
		///         hox with a 2hEdge.
		///     </para>
		///     <para>
		///         28 is widened to any two-hander, which is capture-driven rather than a reading of the number. On the
		///         generic set 28 is <c>2he</c> and 29 is <c>2hb</c>, but retail swings the criteria-28 class rows
		///         (<c>barbarian_2h</c>, <c>conqueror_2h</c>) with two-handed blunt weapons in the corpus -
		///         LargeWarclub, Warhammer, HeavyBludgeon, SledgeHammer, BattleMaul - so on a class family it reads as
		///         "a two-hander". Refusing 2hb there would silence exactly the mobs the archetype was harvested from.
		///     </para>
		/// </remarks>
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

		// How often a combo swing by an NPC that has a class family uses it rather than its generic row. Over the two
		// wired buckets the corpus splits 92 class / 65 generic - the dagger actors 31/13 and the polearm ones 61/52 -
		// so it is a coin flip leaning class rather than a preference for either. Applied only after
		// `MeleeComboShare` has decided the swing carries a combo at all.
		public const double ClassComboShare = 92.0 / (92.0 + 65.0);

		/// <summary>
		///     How far this NPC can attack from, in world units - its main-hand weapon's <c>Attack Range</c>, or
		///     <paramref name="meleeDefault" /> when it holds nothing that states one.
		/// </summary>
		/// <remarks>
		///     The client's own item stat, property field 287 (`CharacterStatId.AttackRange`), stored x100. Measured
		///     across the NPC weapons in the corpus it is a clean split - every melee weapon reads 200 (2.0 units) and
		///     every bow 1750 (17.5) - which is what lets an archer hold its distance instead of running up to swing.
		///     <para>
		///         It is the weapon's number rather than an AI constant because the client has no AI stand-off tuning
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

		// The item class worn in `slot`, or 0 when the slot is empty. `Item` is an FK navigation and a row whose Item
		// was not Included would throw, so test the row's slot first and only touch the item on the one that matters.
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
