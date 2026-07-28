using System;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     Provisional combat math. Spell/ability power and mitigation aren't decoded from captures yet (see
	///     docs/re/quests-and-combat.md), so damage is a simple level-scaled value and <paramref name="spellId" /> is
	///     accepted for future per-spell power but not yet used.
	///     NB (2026-07 investigation): the ApplySpell wire magnitude (`NpcSpellEffect` @29) is a single BYTE rank/scale,
	///     NOT raw damage — the real HP change is the server-computed vitals push (`StatUpdate` health), a GDB formula
	///     of spell-power × rank × mitigation × gear. So it is neither in the client exe nor a plain wire field; closing
	///     it means correlating captured vitals deltas with attacks across the combat captures (gear-dependent, sizable),
	///     not a clean decode. The level-scaled placeholders below are the honest interim, like the (now capture-anchored)
	///     HP and XP curves.
	/// </summary>
	public static class CombatResolver
	{
		public const int BaseCritChancePercent = 5; // everyone crits sometimes, even with no Critical Rating gear

		// Critical-hit tuning (#399), driven by the decoded crit stats: Critical Rating (0x138) = chance, Critical
		// Damage Rating (0x2C7) = magnitude. A crit multiplies the hit and rides the wire as DamageResult type 3
		// (2 = normal, 3 = crit/big hit - capture-derived, CombatEffectBuilder). The CHANCE/MAGNITUDE CURVES are still
		// provisional (the real crit formula is server-GDB) but now use the right stats; tune in-client.
		public const int BaseCritDamagePercent = 150; // a crit with no Critical Damage Rating hits for 1.5x

		// Melee swing damage: driven by the EQUIPPED WEAPON's damage (#395) instead of the flat level placeholder, so a
		// better weapon hits harder. Each swing ROLLS between the weapon's min and max (rollPercent 0..100 interpolates;
		// the caller passes a random roll for per-swing variance, default 50 = the midpoint), scaled down, plus a small
		// level contribution (so progression still shows and an early low-damage weapon isn't trivial). Falls back to the
		// level placeholder when the attacker has no weapon damage (unarmed, or a weapon whose RDB damage wasn't imported).
		// The divisor turning a raw Combat Rating (0x36B) into a flat melee damage bonus. PROVISIONAL (#399): Combat
		// Rating's real role is hit-rate (offense vs the target's defense), which needs a hit/miss model we don't have;
		// until then a geared attacker's Combat Rating (~14-262) adds a modest offensive bump. Tune in-client.
		public const int CombatRatingDamageDivisor = 10;
		public const int MaxCritChancePercent = 75;

		// The divisor turning the raw RDB weapon-damage template values (fields 286/285 = min/max, #395) into a
		// per-hit damage figure. PROVISIONAL: the field IDs are decoded authoritatively (GUIGetMin/MaxDamage), but the
		// exact display/combat scale isn't - ÷100 (the vitals convention) gives sane numbers (a starter dagger ~10, a
		// high-end greatsword ~80). Tune in-client alongside the real damage formula (still server-GDB, undecoded).
		public const int WeaponDamageScale = 100;

		// The half-mitigation point for the provisional armor curve: the total armor rating at which incoming physical
		// damage is halved. PROVISIONAL (#401) - the real mitigation is the server-GDB TotalArmorValue formula
		// (FUN_0113ba00, log tag GameCode/ArmorMitigation). The client SHAPE is now recovered (physical branch, damage
		// types 1/2/3/8):
		//   TotalArmorValue         = clamp0( (C3*0.01 + 1.0)*armor(0x1c0) + (Strength(0x324)-10)*0.1*C4 )  (+ invuln 0x1c6 when flagged)
		//   ApprenticeAdjustedArmor = (2.0 - level(0x36)/stat(0x3c5)) * TotalArmorValue
		//   MitigationModification  = (ApprenticeAdjustedArmor / (level * C1)) * 100      // a percent
		// ⚠️ THE COEFFICIENTS ARE KNOWN (2026-07-28) - this used to say they "need a live Frida dump or in-client
		// tuning" because the four globals read 0.0 in the image. They read 0.0 because they are filled at runtime
		// from the client's own TWEAK TABLE (RDB 1000516), and XrefsTo finds only READS of them, no writer - the tell
		// that a computed pointer fills them. Their roles identify them one-to-one, and the pairing is forced by
		// WHICH BRANCH uses which:
		//     0x02d24704  physical divisor        TWK_TargetPerLevelArmor         137.225
		//     0x02d24714  magical divisor         TWK_TargetPerLevelProtection     46.0625
		//     0x02d24724  magical ability weight  TWK_ProtectionAbilityModifier     0.5
		//     0x02d24734  physical ability weight TWK_ArmorAbilityModifier          2.0
		// MitigationFraction below already uses exactly these, which is why its trace points reproduce retail's
		// numbers - so the formula is retail's, not a fit.
		//
		// The percent term `(x * 0.01 + 1.0) * rating` is BonusArmor (0x1c2) on the physical branch and
		// BonusProtection (0x14e) on the magical one - and passing 0 for it is CORRECT rather than a gap: a census
		// of the [2][9] region across all 3,317 item templates (`--report region-stat-census`) finds NEITHER id on
		// a single item. Nothing in the game's item set grants an armour percentage, so the term is always 1.0.
		// This half-point stands in for that
		// level-normalized, Strength-scaled curve: single armor pieces reach ~1500 and a worn set sums into the thousands,
		// tuned so a geared set mitigates ~half and a starter's few hundred armor little. Tune in-client (gaps.md / #401).
		public const uint ArmorHalfMitigation = 2000;

		// TWK_ArmorAbilityModifier / TWK_TargetPerLevelArmor from the client's own tweak table (RDB 1000516,
		// docs/rdb/client-tweak-table.md). NOT tunable guesses - these are retail's values.
		public const double ArmorAbilityModifier = 2.0;
		public const double TargetArmorPerLevel = 137.225;

		// The magical twins, from the same tweak table: TWK_ProtectionAbilityModifier / TWK_TargetPerLevelProtection.
		public const double ProtectionAbilityModifier = 0.5;
		public const double TargetProtectionPerLevel = 46.0625;

		// Never let armour fully negate a hit. The client's closed form cannot reach 1.0 on its own (its high branch is
		// asymptotic), so this only guards a pathological input.
		public const double MaxMitigationFraction = 0.95;

		// ⚠️ THE PLAYER->NPC HALF OF #401 CANNOT BE SOURCED FROM CAPTURES. Player-side mitigation is wired (the
		// client's own armour/protection reduce incoming damage, WorldSimulation), but an NPC's armour reducing a
		// PLAYER's hit is decided server-side, and retail never puts it on the wire: a census of every stat-update
		// IIR in the corpus - 6,024 packets, 29 distinct stat ids - carries ZERO armour-family stats (448/451/454
		// and the two Bonus percents). tools/capture/scan_npc_armor_stat.py.
		//
		// Three independent checks, so this is not "we did not find it": (1) the stat-update IIR carries no
		// armour-family stat in 6,024 packets; (2) the spawn's own CharacterStats message has NO armour field at
		// all; (3) its `generic_stats` array - the only other place a per-NPC stat could ride - is EMPTY on all
		// 11,373 captured NPC spawns (`--report npc-generic-stats`). The client RDB has no NPC stat tables either
		// (#76). Retail simply never tells the client an NPC's armour, because only its server needs it.
		//
		// So NPC armour has to be MODELLED, like DefaultNpcMaxHealth below, and that is a balance decision rather
		// than a decode. The principled default if it lands: retail's own tweak table defines the armour a level is
		// "targeted" at (TWK_TargetPerLevelArmor x level), and MitigationFraction gives exactly 50% at that value -
		// so `npcArmor = level * TargetArmorPerLevel * k` ties the curve to retail's scale with one tunable k,
		// rather than to a free number.

		// Fallback NPC max health for a creature with NO captured spawn HP. The real per-creature HP is the captured
		// NpcSpawn.Health (the wire value retail sent); this is only used when that's absent (a GM-spawned template,
		// future content). It is NOT in the client RDB - the NpcSpawnDataExport stat tables aren't shipped (#76, see
		// memory rdb_npc_hp_not_in_client) - so this curve is the only source for an un-captured creature.
		public const uint DefaultNpcMaxHealth = 63; // the L1 civilian baseline (was a flat 100 placeholder)

		// The half-mitigation point for the provisional MAGIC-damage curve (Protection rating 0x1C3). Protection values
		// are much smaller than Armor (a high piece ~185 vs armor ~1500), so the half-point is lower. Provisional (#401),
		// tune in-client.
		public const uint ProtectionHalfMitigation = 400;

		// The steady civilian-baseline slope past the last anchor (L40->60 grow ~53 HP/level), used to extrapolate.
		private const uint NpcHpPerLevelAboveCap = 53;

		// Captured CIVILIAN-baseline HP per level (NpcSpawns.Health; HP is level-derived - the same value recurs across
		// unrelated creatures, e.g. L20->933 for 43 NPCs, L40->2217 for 109). High-confidence anchors; HP is
		// piecewise-linear between them. Validates to <1% at unsampled levels (L35->1953, L45->2482, L11->386 vs 382).
		private static readonly (uint Level, uint Hp)[] NpcHpAnchors = [(1, 63), (5, 150), (10, 318), (15, 658), (20, 933), (30, 1688), (40, 2217), (50, 2746), (60, 3276)];

		// The critical-hit damage: the normal hit scaled by (base 1.5x + the attacker's Critical Damage Rating, each
		// point ~ +1%). So more Critical Damage Rating -> harder crits. Floored at 1.
		public static int ApplyCritDamage(int damage, uint criticalDamageRating)
		{
			return Math.Max(1, damage * (BaseCritDamagePercent + (int)criticalDamageRating) / 100);
		}

		// Reduces incoming MAGIC damage by the defender's aggregated Protection rating - the magical mirror of armor
		// mitigation (same diminishing-returns curve, its own half-point).
		public static int ApplyMagicMitigation(int rawDamage, uint protection, uint level = 0, uint attribute = 0)
		{
			if (level == 0)
				return Mitigate(rawDamage, protection, ProtectionHalfMitigation); // no level known: the old curve, unchanged

			return Math.Max(1, (int)Math.Round(rawDamage * (1.0 - ProtectionMitigationFraction(protection, level, attribute))));
		}

		// Reduces incoming PHYSICAL damage by the defender's armour, using the CLIENT'S OWN curve (#401) rather than the
		// invented `K / (armor + K)` one this replaced.
		//
		// Verified against the live client's GameCode/ArmorMitigation trace, which prints every intermediate. Two
		// independent data points reproduce to SEVEN decimals - NecroL80, level 80, armour 2017 + 140 attribute bonus:
		//   apprentice level 1  (the bug in 6d8c7227): adjusted -168246 -> Final Mitigation 0.938747
		//   apprentice level 80 (correct):              adjusted    2157 -> Final Mitigation 0.0982419
		//
		// The curve is LEVEL-NORMALISED against a per-level target, which is the part the old constant could not express:
		// the same armour rating mitigates progressively less as the defender levels.
		public static double MitigationFraction(uint armorRating, uint level, uint strength = 0, double bonusArmorPercent = 0)
		{
			return MitigationFraction(armorRating, level, strength, bonusArmorPercent, TargetArmorPerLevel, ArmorAbilityModifier);
		}

		// The MAGICAL twin of the armour curve (#401), same shape against a different per-level target. Verified against
		// the same client trace, on the two magical lookups it logged for NecroL80 at level 80 (target = 80 x 46.0625 =
		// 3685). The magical branch does NOT log its intermediates - only the shared Roll / Final Mitigation - so these
		// were solved back from the final fraction and then cross-checked against the logged AdjustedRoll:
		//   Protection 342 (Cold/Electrical/Fire): final 0.0464043,   AdjustedRoll 90.7191
		//   Protection  55 (Holy/Unholy):          final 0.00746269,  AdjustedRoll 98.5075
		// Both `adjusted` values come out as EXACTLY the Protection scores on her character sheet, and both rolls match
		// `100 - adjusted/target*100` to six figures - so the branch, the target and the curve are all pinned.
		//
		// `attribute` is Intelligence for damage types 4/5/10 and Wisdom for 6/7 (which of the two the client reads is
		// per damage type). It contributed ZERO in both trace points because the client's stat store carries the
		// BASELINE 10 for those attributes, not the sheet's computed totals (#393) - so the term is real but currently
		// always nil in practice. Kept because it is what the client computes, not because it changes today's numbers.
		public static double ProtectionMitigationFraction(uint protection, uint level, uint attribute = 0, double bonusProtectionPercent = 0)
		{
			return MitigationFraction(protection, level, attribute, bonusProtectionPercent, TargetProtectionPerLevel, ProtectionAbilityModifier);
		}

		private static double MitigationFraction(uint rating, uint level, uint attribute, double bonusPercent, double targetPerLevel, double abilityModifier)
		{
			if (rating == 0 || level == 0)
				return 0;

			// Strength adds armour at 0.1 per point above the baseline 10, scaled by TWK_ArmorAbilityModifier = 2.0. The
			// attribute is at the ×10 RAW wire scale, so a displayed 71 is 710 - that is what makes the trace's
			// AttributeBonus 140 come out exactly: ((710 - 10) / 10) × 2.
			var attributeBonus = attribute > 10
				? (attribute - 10) / 10.0 * abilityModifier
				: 0;
			var total = (bonusPercent * 0.01 + 1.0) * rating + attributeBonus;

			// (2.0 - level / apprenticeLevel) × total. An un-mentored character's apprentice level IS its level, so the
			// term is 1.0 - we send ApprenticeLevel = level for exactly this reason (leaving it unset made the client
			// compute 2 - 80/1 = -78).
			var adjusted = total;
			var target = level * targetPerLevel;

			// The client's closed form, taken when its ARMOR_MITIGATION table has no matching row - which on this build
			// is ALWAYS: the table loads but the vector the lookup walks is empty, so it logs
			// "No mitigation entry found" and falls through. Hence this IS the client's effective behaviour, not a
			// degraded path. If a build ever ships rows, they interpolate a (Min,Max) pair instead (docs/subsystems/combat.md).
			var fraction = adjusted >= target
				? adjusted / (target + adjusted)
				: adjusted / target * 0.5;

			return Math.Clamp(fraction, 0, MaxMitigationFraction);
		}

		// Applies MitigationFraction to a hit. Floors at 1 so a hit is never fully negated.
		public static int ApplyPhysicalMitigation(int rawDamage, uint armorRating, uint level = 0, uint strength = 0)
		{
			if (level == 0)
				return Mitigate(rawDamage, armorRating, ArmorHalfMitigation); // no level known: the old curve, unchanged

			return Math.Max(1, (int)Math.Round(rawDamage * (1.0 - MitigationFraction(armorRating, level, strength))));
		}

		// Crit chance from the attacker's aggregated Critical Rating: base + rating/10 (geared Critical Rating ~ 30-75
		// -> a meaningful bump), capped. Provisional scaling.
		public static int CritChancePercent(uint criticalRating)
		{
			return Math.Min(MaxCritChancePercent, BaseCritChancePercent + (int)(criticalRating / 10));
		}

		// Whether a swing crits, given the attacker's Critical Rating and a 0..99 roll (the caller supplies the roll so
		// this stays pure/testable; CombatService rolls with Random.Shared).
		public static bool IsCriticalHit(uint criticalRating, int rollPercent)
		{
			return rollPercent < CritChancePercent(criticalRating);
		}

		// NPC max health from its level: piecewise-linear over the captured baseline anchors, +53/level beyond the
		// last. Level 0 maps to the level-1 value. Tougher creatures are a multiplier on this in retail; without
		// per-creature data the civilian baseline is the best single estimate.
		public static uint NpcMaxHealthForLevel(uint level)
		{
			var lvl = level == 0
				? 1u
				: level;

			if (lvl <= NpcHpAnchors[0].Level)
				return NpcHpAnchors[0].Hp;

			for (var i = 1; i < NpcHpAnchors.Length; i++)
			{
				if (lvl > NpcHpAnchors[i].Level)
					continue;

				var (l0, h0) = NpcHpAnchors[i - 1];
				var (l1, h1) = NpcHpAnchors[i];
				return h0 + (uint)((ulong)(h1 - h0) * (lvl - l0) / (l1 - l0)); // linear interpolation
			}

			var last = NpcHpAnchors[^1];
			return last.Hp + (lvl - last.Level) * NpcHpPerLevelAboveCap;
		}

		// Provisional XP awarded for killing an NPC of the given level. Level-scaled (a real XP curve / level
		// difference scaling isn't modelled yet, like the damage values). Level 0 maps to the level-1 award.
		public static uint ResolveKillXp(uint npcLevel)
		{
			var lvl = npcLevel == 0
				? 1u
				: npcLevel;
			return 10u + lvl * 25u;
		}

		public static int ResolveMeleeDamage(int? weaponMinDamage, int? weaponMaxDamage, ushort casterLevel, int rollPercent = 50, uint combatRating = 0)
		{
			var level = casterLevel == 0
				? (ushort)1
				: casterLevel;

			var combatBonus = (int)combatRating / CombatRatingDamageDivisor;

			if (weaponMinDamage is { } min && weaponMaxDamage is { } max and > 0)
			{
				var roll = Math.Clamp(rollPercent, 0, 100);
				var rolled = min + (max - min) * roll / 100; // interpolate min..max
				return Math.Max(1, rolled / WeaponDamageScale + level + combatBonus);
			}

			return ResolveSpellDamage(0, level) + combatBonus;
		}

		// Damage an NPC's swing deals to a player, MEASURED from retail rather than invented. Every captured floating
		// number is a DamageResultIIR, and joining it to the attacker's spawn level gives the curve directly
		// (`--report npc-damage`). The physical-school medians, which is what NPC melee is - no captured NPC swing
		// uses a magical school:
		//
		//     level 1   91..133   (486 hits)      level 4   628   (46)
		//     level 2  163..268   (153)           level 5  481..567 (357)
		//     level 3  264..282   (256)           level 6   613   (19)
		//
		// i.e. very close to 100 per level, which is also ~1.5% of a same-level player's health pool - and the same
		// fraction the level-80 rows show, so the line holds across the range rather than fitting the beach.
		//
		// ⚠️ The previous curve was `5 + 3 * level`, i.e. 8 at level 1 against a captured NPC/player health pool in
		// the THOUSANDS - about 15x too weak, and the mismatch was invisible because nothing compared the two. The
		// scale is settled: total damage dealt to a killed NPC equals the health its spawn declared, ratio 0.73..1.10
		// over the corpus, so damage and health share one unit.
		public const uint NpcAttackDamagePerLevel = 100;

		public static uint ResolveNpcAttackDamage(uint npcLevel)
		{
			return Math.Max(1u, npcLevel) * NpcAttackDamagePerLevel;
		}

		// Spell damage: the level-scaled base plus the caster's aggregated Magic Damage rating (#399), added flat like
		// retail's spell-damage bonus. magicDamage is 0 for NPC casts / callers without gear. Per-spell power isn't
		// decoded (every spell shares the base), so this is still provisional; Magic Damage at least makes geared
		// casters hit harder.
		public static int ResolveSpellDamage(uint spellId, ushort casterLevel, uint magicDamage = 0)
		{
			var level = casterLevel == 0
				? (ushort)1
				: casterLevel;
			return 20 + level * 5 + (int)magicDamage;
		}

		// Provisional mana cost for a cast. Per-spell costs aren't decoded from captures yet (like damage), so this
		// is a simple level-scaled value just so casting visibly drains the mana bar (the spell/mana analogue of the
		// melee stamina cost).
		public static uint ResolveSpellManaCost(uint spellId, ushort casterLevel)
		{
			var level = casterLevel == 0
				? (ushort)1
				: casterLevel;
			return (uint)(10 + level * 2);
		}

		private static int Mitigate(int rawDamage, uint rating, uint halfPoint)
		{
			if (rawDamage <= 0 || rating == 0)
				return rawDamage;

			var mitigated = (double)rawDamage * halfPoint / (rating + halfPoint);
			return Math.Max(1, (int)Math.Round(mitigated, MidpointRounding.AwayFromZero));
		}
	}
}
