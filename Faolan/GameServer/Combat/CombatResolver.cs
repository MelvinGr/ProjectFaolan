using System;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     Combat math: damage, mitigation, crit and the per-level NPC curves.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Every constant here is one of two things, and the comment on each says which. A <b>measured</b> one is
	///         derived from the capture corpus with its sample size recorded; change it only with new evidence, and name
	///         the capture. A <b>provisional</b> one is plausible rather than decoded, because the real formula is
	///         server-side GDB; tune it in-client freely.
	///     </para>
	///     <para>
	///         Re-derive the measured ones with:
	///     </para>
	///     <code>
	///     dotnet run --project Faolan.Importer -- --report npc-damage --captures &lt;data-repo&gt;/Captures
	///     </code>
	///     <para>
	///         It joins every captured damage record to the attacker's and target's spawn level and prints
	///         hits/min/median/mean/max per level per damage school, with the sample size beside each number, so a row
	///         built on four hits is visibly not a curve.
	///     </para>
	///     <para>
	///         The two damage packets are different things, which is why censusing one as the other reports no magical
	///         NPCs at all:
	///     </para>
	///     <list type="bullet">
	///         <item>DamageResultIIR 0xFEC4158A - the melee hit. One per swing, large: 91..133 at level 1.</item>
	///         <item>
	///             StatDamageIIR 0xE9F24184 - spell hits and damage-over-time ticks. Small and repeating (8, 8, 8...)
	///             for a bleed, large for a real cast. Its post-<c>delta</c> word is the damage school, not a stat id.
	///         </item>
	///     </list>
	///     <para>
	///         Both are sent in both directions, so one report measures NPC-&gt;player and player-&gt;NPC.
	///     </para>
	///     <para>
	///         Damage, spawn health and player health share one unit, which is what lets a damage constant be read
	///         against <c>NpcSpawn.Health</c> directly. Pairing a hit with the target's next health update does not
	///         establish that - retail pushes only four such pairs in the whole corpus - but the kill does: total damage
	///         dealt to an NPC that died equals the health its own spawn declared, ratio 0.73..1.10.
	///     </para>
	///     <para>
	///         The corpus only reaches level 6 densely: levels 1-6 have hundreds of hits each, above that it is single
	///         digits (level 41: 20 hits spanning 182..28,368; levels 42/44/80: 1-4 hits). The measured curves are
	///         extrapolated past level 6, so the high end is not verified. Open work tracked in Gitea #479, which lists
	///         the fights worth recording.
	///     </para>
	/// </remarks>
	public static class CombatResolver
	{
		// Provisional. Nothing in the corpus separates a crit from a big hit - DamageResult carries no crit flag (the
		// two trailing words are always 0), so a crit is just a larger number and cannot be counted. It is measurable
		// in-client, though: the retail client's combat log (`/logcombat on`) labels them - "%s hit %s for %d points
		// of damage. Critical Hit!" - so this constant and BaseCritDamagePercent are waiting on a log, not a decode.
		// See docs/subsystems/combat.md.
		public const int BaseCritChancePercent = 5; // everyone crits sometimes, even with no Critical Rating gear

		// Critical-hit tuning, driven by the decoded crit stats: Critical Rating (0x138) is the chance, Critical Damage
		// Rating (0x2C7) the magnitude. A crit multiplies the hit and rides the wire as DamageResult type 3 (2 = normal,
		// 3 = crit or big hit - capture-derived, CombatEffectBuilder). Provisional for the same reason as the chance:
		// unmeasurable from the wire, because a crit is not labelled as one.
		public const int BaseCritDamagePercent = 150; // a crit with no Critical Damage Rating hits for 1.5x

		// The divisor turning a raw Combat Rating (0x36B) into a flat melee damage bonus. Provisional: Combat Rating's
		// real role is hit rate (offense against the target's defense), which needs a hit/miss model that does not exist
		// here; until then a geared attacker's Combat Rating (~14-262) adds a modest offensive bump. Tune in-client.
		public const int CombatRatingDamageDivisor = 10;
		public const int MaxCritChancePercent = 75;

		// The divisor turning the raw RDB weapon-damage template values (fields 286/285 = min/max) into a per-hit
		// damage figure. Provisional in its value: the field ids are decoded authoritatively (GUIGetMin/MaxDamage) but
		// the display/combat scale is not, and the vitals convention of /100 gives sane numbers (a starter dagger ~10,
		// a high-end greatsword ~80).
		//
		// The corpus constrains it as a fraction rather than as a damage figure: retail's player melee takes ~10% of a
		// normal mob's health per hit at levels 1-2 (~10 hits to kill) and 4..7% at levels 3-5 (`--report npc-damage`,
		// the FRACTION section). Tune against that fraction, never against the report's absolute medians - a level
		// bucket mixes a 63-health critter with a 5,000-health elite and a 300,000-health city guard, so "the median
		// hit at level 1 is 465..691" averages incomparable fights, while the per-target fraction is a stable 10%.
		public const int WeaponDamageScale = 100;

		// The half-mitigation point for the fallback armor curve: the total armor rating at which incoming physical
		// damage is halved. Provisional, and only reached when the defender's level is unknown - MitigationFraction,
		// the client's own formula with the client's own tweak-table coefficients, is the real path.
		//
		// The client's physical branch (damage types 1/2/3/8, FUN_0113ba00, log tag GameCode/ArmorMitigation) is:
		//   TotalArmorValue         = clamp0( (C3*0.01 + 1.0)*armor(0x1c0) + (Strength(0x324)-10)*0.1*C4 )  (+ invuln 0x1c6 when flagged)
		//   ApprenticeAdjustedArmor = (2.0 - level(0x36)/stat(0x3c5)) * TotalArmorValue
		//   MitigationModification  = (ApprenticeAdjustedArmor / (level * C1)) * 100      // a percent
		//
		// The four coefficients read 0.0 in the image because they are filled at runtime from the client's tweak table
		// (RDB 1000516); XrefsTo finds only reads of them. Their roles and the branch each is used from pair them
		// one-to-one:
		//     0x02d24704  physical divisor        TWK_TargetPerLevelArmor         137.225
		//     0x02d24714  magical divisor         TWK_TargetPerLevelProtection     46.0625
		//     0x02d24724  magical ability weight  TWK_ProtectionAbilityModifier     0.5
		//     0x02d24734  physical ability weight TWK_ArmorAbilityModifier          2.0
		//
		// The percent term `(x * 0.01 + 1.0) * rating` is BonusArmor (0x1c2) on the physical branch and BonusProtection
		// (0x14e) on the magical one, and passing 0 for it is correct rather than a gap: a census of the [2][9] region
		// across all 3,317 item templates (`--report region-stat-census`) finds neither id on a single item, so nothing
		// in the item set grants an armour percentage and the term is always 1.0.
		public const uint ArmorHalfMitigation = 2000;

		// What retail puts in `StatDamageIIR.SpellBaseId` for the stat ids 0, 5 and 10: nothing. Measured over the
		// corpus - 3,059 of 3,061 such records carry 0 (all 50 with statId 10, all 2,892 with statId 0, all 119 with
		// statId 5); the two exceptions name an id absent from the whole spell catalog.
		//
		// That population does not include health, and health is the one that moves the bar. Measured over statId
		// 0x1B (27) on the player's own handle - 434 records across the corpus - retail names a real spell in 391 of
		// them. The only combination it ever pairs with 0 is school 12, the world as the damage source (all 43, which
		// is exactly the school-12 count): a fall or a hazard, which genuinely has no spell behind it.
		//
		// So use this constant for a stat the measurement covers, and the real base id for a health record, because
		// that is what retail states. A zeroed id does not void the record - the client's apply (`StatDamageIIR_t`
		// slot 2, `FUN_008d5960`) touches `spellBaseId` only under `if (deathReason == 10)` and the health write above
		// it runs unconditionally - but there is no reason to send one where retail sends a spell.
		public const uint RetailSpellBaseId = 0;

		// TWK_ArmorAbilityModifier and TWK_TargetPerLevelArmor from the client's own tweak table (RDB 1000516,
		// docs/rdb/client-tweak-table.md). These are retail's values, not tunable guesses.
		public const double ArmorAbilityModifier = 2.0;
		public const double TargetArmorPerLevel = 137.225;

		// The magical twins, from the same tweak table: TWK_ProtectionAbilityModifier / TWK_TargetPerLevelProtection.
		public const double ProtectionAbilityModifier = 0.5;
		public const double TargetProtectionPerLevel = 46.0625;

		// Never let armour fully negate a hit. The client's closed form cannot reach 1.0 on its own (its high branch is
		// asymptotic), so this only guards a pathological input.
		public const double MaxMitigationFraction = 0.95;

		// An NPC's armour reducing a player's hit cannot be sourced from captures: retail decides it server-side and
		// never puts it on the wire. Three independent checks say so, so this is not simply a failed search: the
		// stat-update IIR carries no armour-family stat (448/451/454 and the two Bonus percents) in 6,024 packets over
		// 29 distinct stat ids (tools/capture/scan_npc_armor_stat.py); the spawn's own CharacterStats message has no
		// armour field at all; and its `generic_stats` array - the only other place a per-NPC stat could ride - is
		// empty on all 11,373 captured NPC spawns (`--report npc-generic-stats`). The client RDB ships no NPC stat
		// tables either.
		//
		// NPC armour therefore has to be modelled, like DefaultNpcMaxHealth below, which is a balance decision rather
		// than a decode. The principled default if it lands: retail's tweak table defines the armour a level is
		// targeted at (TWK_TargetPerLevelArmor x level) and MitigationFraction gives exactly 50% at that value, so
		// `npcArmor = level * TargetArmorPerLevel * k` ties the curve to retail's scale with one tunable k.
		//
		// Player-side mitigation is wired: the client's own armour and protection reduce incoming damage in
		// WorldSimulation.

		// The half-mitigation point for the fallback magic-damage curve (Protection rating 0x1C3). Protection values are
		// much smaller than armor (a high piece ~185 against armor ~1500), so the half-point is lower. Provisional; tune
		// in-client.
		public const uint ProtectionHalfMitigation = 400;

		// The steady civilian-baseline slope past the last anchor (L40->60 grow ~53 HP/level), used to extrapolate.
		private const uint NpcHpPerLevelAboveCap = 53;

		// Captured civilian-baseline HP per level (NpcSpawns.Health; HP is level-derived - the same value recurs across
		// unrelated creatures, e.g. level 20 -> 933 for 43 NPCs, level 40 -> 2217 for 109). HP is piecewise-linear
		// between the anchors, and validates to under 1% at unsampled levels (level 35 -> 1953, level 45 -> 2482,
		// level 11 -> 386 against a captured 382).
		//
		// This is also why NpcSpawn.Health spans 9 to 1,108,500 without being broken: retail's world holds two
		// populations. Ordinary mobs follow this curve (a level-1 is 63, a level-10 is 318) while guards, bosses and
		// target dummies carry thousands to millions and are not meant to die, so a per-level average over
		// NpcSpawn.Health mixes the two and means nothing.
		private static readonly (uint Level, uint Hp)[] NpcHpAnchors = [(1, 63), (5, 150), (10, 318), (15, 658), (20, 933), (30, 1688), (40, 2217), (50, 2746), (60, 3276)];

		// The critical-hit damage: the normal hit scaled by (base 1.5x + the attacker's Critical Damage Rating, each
		// point ~ +1%). So more Critical Damage Rating -> harder crits. Floored at 1.
		public static int ApplyCritDamage(int damage, uint criticalDamageRating)
		{
			return Math.Max(1, damage * (BaseCritDamagePercent + (int)criticalDamageRating) / 100);
		}

		// A spell's own critical chance, in hundredths of a percent, plus what the caster's Critical Rating adds on the
		// same curve a swing uses. RDB field 147 `SpellCriticalChance`; 250 is 2.50%, its modal value.
		//
		// A spell whose record states no chance does not crit at all, rather than falling back to the melee base. That
		// base exists because everyone crits sometimes with a weapon; extending it to the 20,278 spell records that
		// state nothing would grant every one of them a 5% crit on no evidence, where the 1,224 that do state one are
		// the client saying which spells crit and how often.
		public static int SpellCritChanceHundredths(uint spellCriticalChanceHundredths, uint criticalRating)
		{
			return spellCriticalChanceHundredths == 0
				? 0
				: Math.Min(MaxCritChancePercent * 100, (int)spellCriticalChanceHundredths + ((int)criticalRating * 10));
		}

		// The critical damage of a spell hit: the spell's own bonus over 100%, plus the caster's Critical Damage
		// Rating at a point per percent (the melee rule).
		//
		// RDB field 129 `SpellCriticalDamage` is that bonus in hundredths of a percent, and its modal value - 5000 on
		// 607 of the 1,228 records that carry it - is +50%, i.e. the same 1.5x <see cref="BaseCritDamagePercent" />
		// assumes, so the client's spell table corroborates that constant. A record stating none takes the base.
		public static int ApplySpellCritDamage(int damage, uint spellCriticalDamageHundredths, uint criticalDamageRating)
		{
			var bonusHundredths = spellCriticalDamageHundredths > 0
				? (int)spellCriticalDamageHundredths
				: (BaseCritDamagePercent - 100) * 100;

			return Math.Max(1, (int)((long)damage * (PercentScale + bonusHundredths + ((int)criticalDamageRating * 100)) / PercentScale));
		}

		/// <summary>
		///     Reduces incoming magic damage by the defender's aggregated Protection rating - the magical mirror of
		///     armor mitigation, with the same diminishing-returns curve and its own half-point.
		/// </summary>
		public static int ApplyMagicMitigation(int rawDamage, uint protection, uint level = 0, uint attribute = 0)
		{
			if (level == 0)
				return Mitigate(rawDamage, protection, ProtectionHalfMitigation); // no level known: the fallback curve

			return Math.Max(1, (int)Math.Round(rawDamage * (1.0 - ProtectionMitigationFraction(protection, level, attribute))));
		}

		/// <summary>
		///     Reduces incoming physical damage by the defender's armour, using the client's own curve.
		/// </summary>
		/// <remarks>
		///     <para>
		///         The curve is level-normalised against a per-level target, so the same armour rating mitigates
		///         progressively less as the defender levels.
		///     </para>
		///     <para>
		///         Verified against the live client's GameCode/ArmorMitigation trace, which prints every intermediate.
		///         Two independent data points reproduce to seven decimals - NecroL80, level 80, armour 2017 plus a 140
		///         attribute bonus:
		///     </para>
		///     <code>
		///     apprentice level 1:  adjusted -168246 -&gt; Final Mitigation 0.938747
		///     apprentice level 80: adjusted    2157 -&gt; Final Mitigation 0.0982419
		///     </code>
		/// </remarks>
		public static double MitigationFraction(uint armorRating, uint level, uint strength = 0, double bonusArmorPercent = 0)
		{
			return MitigationFraction(armorRating, level, strength, bonusArmorPercent, TargetArmorPerLevel, ArmorAbilityModifier);
		}

		/// <summary>
		///     The magical twin of the armour curve: the same shape against a different per-level target.
		/// </summary>
		/// <remarks>
		///     <para>
		///         Verified against the same client trace, on the two magical lookups it logged for NecroL80 at level 80
		///         (target = 80 x 46.0625 = 3685). The magical branch does not log its intermediates - only the shared
		///         Roll and Final Mitigation - so these were solved back from the final fraction and cross-checked
		///         against the logged AdjustedRoll:
		///     </para>
		///     <code>
		///     Protection 342 (Cold/Electrical/Fire): final 0.0464043,  AdjustedRoll 90.7191
		///     Protection  55 (Holy/Unholy):          final 0.00746269, AdjustedRoll 98.5075
		///     </code>
		///     <para>
		///         Both adjusted values come out as exactly the Protection scores on that character's sheet, and both
		///         rolls match <c>100 - adjusted/target*100</c> to six figures, so the branch, the target and the curve
		///         are all pinned.
		///     </para>
		///     <para>
		///         <paramref name="attribute" /> is Intelligence for damage types 4/5/10 and Wisdom for 6/7; which of the
		///         two the client reads is per damage type. It contributes nothing in practice today because the client's
		///         stat store carries the baseline 10 for those attributes rather than the sheet's computed totals. The
		///         term is kept because it is what the client computes.
		///     </para>
		/// </remarks>
		public static double ProtectionMitigationFraction(uint protection, uint level, uint attribute = 0, double bonusProtectionPercent = 0)
		{
			return MitigationFraction(protection, level, attribute, bonusProtectionPercent, TargetProtectionPerLevel, ProtectionAbilityModifier);
		}

		private static double MitigationFraction(uint rating, uint level, uint attribute, double bonusPercent, double targetPerLevel, double abilityModifier)
		{
			if (rating == 0 || level == 0)
				return 0;

			// Strength adds armour at 0.1 per point above the baseline 10, scaled by TWK_ArmorAbilityModifier = 2.0. The
			// attribute is at the x10 raw wire scale, so a displayed 71 is 710 - that is what makes the trace's
			// AttributeBonus 140 come out exactly: ((710 - 10) / 10) x 2.
			var attributeBonus = attribute > 10
				? (attribute - 10) / 10.0 * abilityModifier
				: 0;
			var total = (bonusPercent * 0.01 + 1.0) * rating + attributeBonus;

			// (2.0 - level / apprenticeLevel) x total. An un-mentored character's apprentice level is its own level, so
			// the term is 1.0. ApprenticeLevel must therefore be sent as the character's level; left unset the client
			// computes 2 - 80/1 = -78.
			var target = level * targetPerLevel;

			// The client's closed form, taken when its ARMOR_MITIGATION table has no matching row - which on this build
			// is always: the table loads but the vector the lookup walks is empty, so it logs "No mitigation entry
			// found" and falls through. This is therefore the client's effective behaviour, not a degraded path. A build
			// that ships rows interpolates a (Min,Max) pair instead (docs/subsystems/combat.md).
			var fraction = total >= target
				? total / (target + total)
				: total / target * 0.5;

			return Math.Clamp(fraction, 0, MaxMitigationFraction);
		}

		/// <summary>Applies <see cref="MitigationFraction" /> to a hit. Floors at 1 so a hit is never fully negated.</summary>
		public static int ApplyPhysicalMitigation(int rawDamage, uint armorRating, uint level = 0, uint strength = 0)
		{
			if (level == 0)
				return Mitigate(rawDamage, armorRating, ArmorHalfMitigation); // no level known: the fallback curve

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

		/// <summary>
		///     NPC max health from its level: piecewise-linear over the captured baseline anchors, +53 per level beyond
		///     the last. Level 0 maps to the level-1 value.
		/// </summary>
		/// <remarks>
		///     Tougher creatures are a multiplier on this in retail; without per-creature data the civilian baseline is
		///     the best single estimate.
		/// </remarks>
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

		/// <summary>
		///     Provisional XP awarded for killing an NPC of the given level. Level-scaled; a real XP curve and
		///     level-difference scaling are not modelled. Level 0 maps to the level-1 award.
		/// </summary>
		public static uint ResolveKillXp(uint npcLevel)
		{
			var lvl = npcLevel == 0
				? 1u
				: npcLevel;
			return 10u + lvl * 25u;
		}

		/// <summary>
		///     Melee swing damage, driven by the equipped weapon's damage so a better weapon hits harder.
		/// </summary>
		/// <remarks>
		///     Each swing rolls between the weapon's min and max (<paramref name="rollPercent" /> 0..100 interpolates;
		///     the caller passes a random roll for per-swing variance, and the default 50 is the midpoint), scaled down
		///     by <see cref="WeaponDamageScale" />, plus a small level contribution so progression still shows and an
		///     early low-damage weapon is not trivial. Falls back to the level-scaled spell placeholder when the attacker
		///     has no weapon damage - unarmed, or a weapon whose RDB damage was not imported.
		/// </remarks>
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

		// Damage an NPC's swing deals to a player, measured from retail. Every captured floating number is a
		// DamageResultIIR, and joining it to the attacker's spawn level gives the curve directly (`--report
		// npc-damage`). The physical-school medians, which is what NPC melee is - no captured NPC swing uses a
		// magical school:
		//
		//     level 1   91..133   (486 hits)      level 4   628   (46)
		//     level 2  163..268   (153)           level 5  481..567 (357)
		//     level 3  264..282   (256)           level 6   613   (19)
		//
		// i.e. very close to 100 per level, which is also ~1.5% of a same-level player's health pool.
		//
		// The line holds at the top of the range too, measured against a level-80 player in
		// `NecroL80_mage-necromancer_login_combat{,2}` (`--report npc-damage`, the BY ATTACKER section):
		//
		//     level 75 Hired Bodyguard      7 hits   median 7324   vs 7500   = 0.98 x model
		//     level 75 Zingaran Operative  10 hits   median 7200   vs 7500   = 0.96 x model
		//
		// Two unrelated templates, independently within 4% of `level * 100` at level 75. The second capture carries the
		// client's own combat log for the same fight, so those wire figures are confirmed against what the player saw
		// (67-92 displayed, wire x100) rather than inferred.
		//
		// This constant is the swing against a player at the mob's own level. Retail scales it down hard once the mob
		// is well below the player - see LevelGapPercent below - so the low `x model` ratios in the report are that
		// penalty, not evidence against this constant.
		//
		// A second per-archetype factor is visible and also unmodelled: ranged mobs sit near 0.2 even at gap 0
		// (`Poacher Marksman` 0.17, `Bat Pict Archer` 0.21) where same-level melee is ~1.0.
		public const uint NpcAttackDamagePerLevel = 100;

		/// <summary>
		///     The damage an NPC's swing deals to a player, in display units.
		/// </summary>
		/// <remarks>
		///     <para>
		///         <see cref="NpcAttackDamagePerLevel" /> is in wire units and everything this damage lands in is in
		///         display units, so the conversion here is mandatory: <c>NpcHpAnchors</c> starts at 63 for level 1,
		///         <c>NpcSpawn.Health</c> is 63 where the same row's wire <c>Life</c> is 6300,
		///         <see cref="ResolveMeleeDamage" /> already divides by <see cref="WeaponDamageScale" />, and a player's
		///         MaxHealth is the /100 vitals value. Left on the wire scale, a level-1 mob swings 100 into a 63-point
		///         pool and a level-3 swings 300 into a level-1 player's 88 - every hit lethal on contact.
		///     </para>
		///     <para>
		///         The conversion reads the scale off the stat (<c>CharacterStatId.HealthMax.GetScale()</c>) rather than
		///         a second hand-written 100, because this damage is spent against a health pool and must move if that
		///         scale ever does. The measured curve is deliberately left in wire units: its whole evidence base is in
		///         wire numbers, so rewriting it would make every citation on it unreadable. Floored at 1 so a level-1
		///         swing still does something.
		///     </para>
		///     <para>
		///         Sanity, in display units: level 1 deals 1 against a level-1 player's ~88 pool, 1.1% a hit, which is the
		///         ~1.5% of a same-level player's pool the measurement states; and the player's own ~6 a swing against a
		///         63-health critter is the ~10 hits to kill that <see cref="WeaponDamageScale" /> is pinned to.
		///     </para>
		/// </remarks>
		/// <param name="npcLevel">The attacker's level, which sets the base curve.</param>
		/// <param name="targetLevel">
		///     Who is being hit: retail's level-gap penalty is applied against this, so pass the defender's level. A
		///     caller measuring the bare per-level curve passes <paramref name="npcLevel" /> for both, which is the
		///     un-penalised case by construction.
		/// </param>
		/// <param name="ranged">Whether the mob shoots rather than swings (<c>NpcWeapon.IsRanged</c>); see RangedPercent.</param>
		public static uint ResolveNpcAttackDamage(uint npcLevel, uint targetLevel, bool ranged = false)
		{
			return Math.Max(1u, NpcAttackDamageWire(npcLevel, targetLevel, ranged) / CharacterStatId.HealthMax.GetScale());
		}

		/// <summary>
		///     The same curve before the display conversion, i.e. in the units the captures are in.
		/// </summary>
		/// <remarks>
		///     Anything scoring captured damage against the model must use this overload, not
		///     <see cref="ResolveNpcAttackDamage" />: captured damage is a wire number, so comparing it against the
		///     display-unit result is silently off by a factor of 100 and still looks like evidence.
		///     <c>--report npc-damage</c> is the caller this exists for.
		/// </remarks>
		public static uint NpcAttackDamageWire(uint npcLevel, uint targetLevel, bool ranged = false)
		{
			var scale = LevelGapPercent(npcLevel, targetLevel) * (ranged ? RangedPercent : 100u) / 100 * TopBandPercent(npcLevel) / 100;
			return Math.Max(1u, npcLevel) * NpcAttackDamagePerLevel * scale / 100;
		}

		// The per-level line breaks at the top of the range: a level 78-80 mob hits about 2.8x what
		// `level * NpcAttackDamagePerLevel` predicts, where a level-75 one hits exactly what it predicts.
		//
		// Measured off `--report npc-damage`'s BY ATTACKER rows. The comparison is clean because both bands sit at a
		// level gap the penalty leaves alone (gap 0-2 up here, gap 5 at level 75), so nothing but the base curve is in
		// play:
		//
		//     level  attacker                      NpcType  hits  x model
		//        74  Hired Bodyguard                     4    11     1.01
		//        75  Zingaran Operative                  4    10     0.96
		//        75  Hired Bodyguard                     4    19     0.99
		//        75  Zingaran Stockpiler                 4    14     1.35
		//        --  ------------------------------  -----  ----  -------
		//        78  Wharf Rat Thug                      4    18     2.80
		//        79  Wharf Rat Thug                      4    13     3.00
		//        80  Ravenous Crocodile Follower         4    32     2.72
		//        80  Boros                               4    19     2.74
		//        79  Argus the Thief                     2    20     3.53
		//        80  Eerika of the Maidens               2    18     3.41
		//
		// The factor belongs to the level band, not to a creature rank: `Wharf Rat Thug`, `Boros` and `Ravenous
		// Crocodile Follower` are all `NpcStatCore.NpcType = 4` - the untitled majority, no nameplate rank, no
		// `Normalset` - and they sit at 2.72-3.00 alongside the two that really are `MiniBoss`. An ordinary mob in the
		// band hits like the bosses do.
		//
		// Applied to levels 78-80 only, which is exactly where it was measured. The corpus has no NPC->player hits at
		// 76 or 77, so the knee sits somewhere in (75, 78] and placing it would be inventing; those two levels keep the
		// plain line rather than inherit an unmeasured boundary.
		//
		// 2.8 is the untitled median (2.72-3.00), not the median of all six (~2.9): the MiniBoss pair sits about 1.25x
		// above the untitled ones and is not modelled, and under-hitting is the cheap direction to be wrong in - the
		// same bias `LevelGapPercent` takes for the mob-above-player case.
		private static uint TopBandPercent(uint npcLevel)
		{
			return npcLevel >= TopBandFirstLevel ? TopBandDamagePercent : 100u;
		}

		// The first level the measured band covers. 76 and 77 are not in it - see TopBandPercent.
		private const uint TopBandFirstLevel = 78;

		// The band's factor, from the four untitled rows (2.72 / 2.74 / 2.80 / 3.00 over 82 hits).
		private const uint TopBandDamagePercent = 280;

		// A ranged mob deals about a fifth of what a melee mob of the same level does - a factor independent of the
		// level gap, and visible at gap 0-1 where the per-level curve itself is verified.
		//
		// Measured by joining `--report npc-damage`'s BY ATTACKER rows to each attacker's main-hand weapon class
		// (`Items.SearchCategory`, the client's own catalog value rather than a name match):
		//
		//     weapon class   templates  hits   median x model
		//     bow (30017)            7   127   0.36        <- and 119 of those hits are the three below
		//     1h edge (30011)       16   488   1.01
		//     dagger  (30015)       10   122   1.02
		//     1h blunt(30012)        7    63   1.15
		//     2h blunt(30014)        3    75   1.00
		//     polearm (30016)        3   289   1.39
		//     none (animals)        19   388   1.15
		//
		// The three well-sampled bow templates are `Poacher Marksman` 8758 at 0.17 (31 hits), `Poacher Marksman`
		// 13961 at 0.18 (18) and `Bat Pict Archer` 937 at 0.21 (70) - two zones, three templates, 119 hits, all at
		// gap 0-1. The other four bow rows are 1-4 hits each and two of them are the known archetype outliers
		// (`Gaud Guardsman`, `Nemedian Sagittarius`), so the factor is taken from the 119 and not from the median of
		// seven rows of wildly different weight.
		//
		// Every melee class sits at ~1.0, which is what makes this a ranged factor rather than a per-weapon damage
		// model. A weapon roll would not do that: the bow's own catalog damage (819-5733) is higher than the 1h edge's
		// (505-2864), i.e. the opposite of what is measured.
		private const uint RangedPercent = 19;

		// Retail's level-gap penalty: a mob well below the player barely scratches them, and the drop is a cliff rather
		// than a slope - full damage out to a five-level gap, then two steps to a ~0.11 floor that holds all the way
		// down. Returned as a percent so the arithmetic above stays integral.
		//
		// Measured by (attacker template, mob level, player level) - `--report npc-damage`, the BY ATTACKER section.
		// Keyed by template because one name spans several templates and levels, and per-attacker because a level
		// bucket mixes archetypes. `x model` is the measured median over `level * 100`:
		//
		//     gap 0-1   0.77 .. 1.57   11 beach templates, 700+ hits   (a same-level player)
		//     gap 5     0.96, 0.99, 1.35   43 hits, 3 templates at level 75
		//     gap 6     0.42 (11 hits, template 15441), 0.76 (1 hit)
		//     gap 7     0.11, 0.13     14 hits, templates 15437/15441 at level 73
		//     gap 8-10  0.10 .. 0.14   26 hits, templates 15436/15441 at level 70-72
		//     gap 20-21 0.11           9 hits, Striped Boar at level 59/60
		//     gap 27+   0.03 .. 0.11   drifts slightly lower; one floor covers it
		//
		// The single strongest piece is template 15441 `Hired Bodyguard` against one level-80 player, by its own
		// level: 729 at 70, 732 at 71, 938 at 73, 3125 at 74, 7402 at 75. A 10x swing over five of the attacker's
		// levels with the defender, its gear and the attacker's template all held fixed. That is also why it is not
		// armour - a level-normalised mitigation term cannot differ 8x across three levels of attacker - and the same
		// fight carries the client's combat log, showing 7-14 displayed from a level-72 mob and 67-92 from a
		// level-75 one.
		//
		// This is a measured table, not a recovered rule: retail decides the penalty server-side and the client's tweak
		// table (RDB 1000516) carries no level-difference constant - only TWK_TargetPerLevelArmor/Protection and
		// TWK_Consequence_XPGainDecayRate. The two plateaus are well sampled; the step between them rests on one
		// template's 12 hits at gap 6, so that is the number to revisit when a capture at gaps 2-4 or another gap-6
		// fight arrives.
		//
		// The mirror case - a mob above the player - is deliberately not modelled and returns 100. Two rows measure it
		// (level-42 Nemedians against a level-23 player, 5.15 and 6.99) and they point the right way, but they are one
		// hit each, and a higher-level mob already hits harder through `level * 100`. Amplifying damage 6x on two hits
		// would be inventing, and a mob a few levels up one-shotting a player is the expensive direction to be wrong in.
		//
		// Two archetypes sit off this table and neither is evidence against the gap curve: ranged mobs far below it
		// (`Poacher Marksman` 0.17 at gap 0, modelled as `RangedPercent`), and a pair of true outliers far above it -
		// `Black Ring Assassin` at 45x (`Normalset` 13, 3.15M health) and `Gaud Guardsman` at 24x (`Normalset` 32,
		// 30.5M health, a city guard). Both outliers carry a large `Normalset` and a health pool two orders up, the
		// shape a skull-tier creature has; neither is modelled.
		public static uint LevelGapPercent(uint npcLevel, uint targetLevel)
		{
			var gap = (long)targetLevel - Math.Max(1u, npcLevel);
			return gap switch
			{
				<= FullDamageGap => 100,
				FullDamageGap + 1 => GapStepPercent,
				_ => FarBelowPercent
			};
		}

		// The largest gap that still deals full damage. Measured 0.96/0.99/1.35 at exactly this gap, against 0.42 one
		// level further down.
		private const int FullDamageGap = 5;

		// The one step between the plateaus, at gap 6. The 11-hit measurement (0.42) rather than the mean with the
		// single 0.76 sighting beside it - that one is a `Vibrant Scorpion`, an archetype the same report shows
		// sitting above the curve at every gap.
		private const uint GapStepPercent = 42;

		// The floor, from gap 7 down: 0.10-0.14 over 39 hits and six templates from gap 7 to gap 21.
		private const uint FarBelowPercent = 11;

		/// <summary>
		///     Spell damage: the spell's own stated hit where it has one, plus the caster's aggregated Magic Damage
		///     rating added flat like retail's spell-damage bonus, all scaled by the caster's feat boost for this
		///     spell's line.
		/// </summary>
		/// <remarks>
		///     <para>
		///         <paramref name="spellHit" /> is the per-spell power in display units: the magnitude the spell's own
		///         template states on health, rolled within its stated range at the caster's level. `Ice Strike (Rank 6)`
		///         states -16300..-18950 on stat 27, which is the <i>163-189.5 points of Health</i> the client's own
		///         tooltip renders, so the number is the client's rather than a curve fitted to it.
		///         <c>SpellPoolEffects</c> resolves it - level band, then the ramp inside the band; 2,149 spells carry one.
		///     </para>
		///     <para>
		///         Zero means the spell states no hit, and the flat <c>20 + level*5</c> placeholder answers then. That is
		///         still most casts in practice - an ability with no template row, an NPC cast - so the placeholder is
		///         narrow rather than absent: a spell that states nothing must not fall to 0 damage.
		///     </para>
		///     <para>
		///         <paramref name="spellLinePercentHundredths" /> is the caster's feat boost for this spell's line;
		///         3500 = +35%, what `Decomposition` rank 5 grants every rank of Flesh to Worms. It scales the whole
		///         result, Magic Damage included, because the client's own wording is "Increase to damage or healing on
		///         &lt;spell&gt;" rather than a bonus to one term. Applied last, so the percent is over the finished number.
		///     </para>
		/// </remarks>
		public static int ResolveSpellDamage(uint spellId, ushort casterLevel, uint magicDamage = 0, int spellLinePercentHundredths = 0, int spellHit = 0)
		{
			var level = casterLevel == 0
				? (ushort)1
				: casterLevel;
			var b = spellHit > 0
				? spellHit
				: 20 + level * 5;

			return ApplyPercentBonus(b + (int)magicDamage, spellLinePercentHundredths);
		}

		/// <summary>
		///     A percent bonus stored x100, applied over a finished damage number.
		/// </summary>
		/// <remarks>
		///     Shared by the spell-line feat boost and the combo feat bonuses so the two cannot drift on rounding or on
		///     the floor rule. A debuff-shaped negative can only ever take the hit to 1, never to 0 or below: a boosted
		///     attack that stops dealing damage would read as a miss.
		/// </remarks>
		public static int ApplyPercentBonus(int damage, int percentHundredths)
		{
			return percentHundredths == 0
				? damage
				: Math.Max(1, (int)((long)damage * (PercentScale + percentHundredths) / PercentScale));
		}

		// A percent modifier is stored x100 (3500 = 35.00%), so 100% is 10000.
		private const int PercentScale = 10000;

		// The client's TKW_MagicalDpsAbilityModifier tweak (RDB 1000516; the client's own misspelling of TWK_), in
		// per-mille so the arithmetic stays integral: 0.6 x the caster's scaling attribute is added to their Magic
		// Damage. Recovered from the client rather than fitted - its magic-damage accessor FUN_01118ce0 reads the tweak
		// into XMM0, multiplies by the attribute score and truncates:
		//
		//     MOVSS XMM0, [0x02d24370]     ; the tweak's value (the Tweak_c object at 0x2d24368 + 8)
		//     CALL  0x007184d0             ; attribute score -> XMM0
		//     MULSS XMM0, [ESP+0xc]        ; x tweak
		//     MULSS XMM0, [0x01f24468]     ; x 100 (the wire's hundredths)
		//     CVTTSS2SI EBX, XMM0          ; truncate, not round
		//
		// so `magicDamage = schoolStat + trunc(attribute x 0.6) + flatGearMagicDamage` in display units.
		// See docs/rdb/spell-scaling.md.
		private const uint MagicalDpsAbilityModifierPerMille = 600;

		// Which attribute a caster's magic damage scales off. The client picks it by class, not by spell
		// (FUN_01118ce0): Wisdom for every caster except Necromancer (0x29), Herald of Xotli (0x2B) and Demonologist
		// (0x2C), which use Intelligence.
		//
		// The RDB confirms this independently. Spell property field 847 (Spell.ScalingAttribute) names an attribute per
		// spell, and grouping the 716 records that carry one by their spell line's class reproduces the rule exactly:
		// Necromancer 153, Herald 41 and Demonologist 126 records on Intelligence; Priest of Mitra 32, Tempest of Set
		// 48 and Dark Templar 3 on Wisdom, and the Conqueror's 5 on Strength - a non-caster taking the physical branch.
		//
		// `spellScalingAttribute` is that field and it overrides the class rule, which is the only reason it exists
		// since the class rule already covers every spell. 0, the overwhelming majority of templates, means no
		// override; a Demonologist's six Wisdom-scaled spells are what it is for.
		public static CharacterStatId MagicDamageAttribute(ConanClass casterClass, uint spellScalingAttribute = 0)
		{
			if (spellScalingAttribute != 0)
				return (CharacterStatId)spellScalingAttribute;

			return casterClass is ConanClass.Necromancer or ConanClass.HeraldOfXotli or ConanClass.Demonologist
				? CharacterStatId.Intelligence
				: CharacterStatId.Wisdom;
		}

		/// <summary>
		///     A caster's Magic Damage in display units: the flat rating their worn gear grants, plus the
		///     attribute-scaled term the client adds on top (<see cref="MagicDamageAttribute" /> x 0.6). This is the
		///     value that goes into <see cref="ResolveSpellDamage" />'s <c>magicDamage</c>.
		/// </summary>
		public static uint ResolveMagicDamage(uint gearMagicDamage, ConanClass casterClass, VitalAttributes attributes, uint spellScalingAttribute = 0)
		{
			var score = MagicDamageAttribute(casterClass, spellScalingAttribute) switch
			{
				CharacterStatId.Strength => attributes.Strength,
				CharacterStatId.Constitution => attributes.Constitution,
				CharacterStatId.Intelligence => attributes.Intelligence,
				CharacterStatId.Wisdom => attributes.Wisdom,
				CharacterStatId.Dexterity => attributes.Dexterity,
				_ => 0u
			};

			// Truncating, like the client's CVTTSS2SI - not rounding.
			return gearMagicDamage + score * MagicalDpsAbilityModifierPerMille / 1000;
		}

		// The mana a cast costs, from retail's own formula. The client reads the spell's RDB field 132
		// (Spell.ManaCost, carried raw) in two modes, both of which land in the wire's hundredths and are divided by
		// 100 for display:
		//
		//   percent mode  cost = manaMax * field / 100      Ice Strike 9 -> 9% of the pool
		//   flat mode     cost = field                      Dark Knowledge (Rank 4) 42200 -> 422 mana
		//
		// Which mode is in force is stated by the record, not inferred: bit 2 of the spell record's flags word,
		// imported into Spell.ManaCostIsPercent, is the same bit the client reads at `spell + 0xC8`.
		//
		// A cost of 0 means free, which is retail's reading rather than a gap papered over: the client's property
		// accessor returns 0 for a record with no field 132, and 3,783 of the 5,496 records that do carry it carry a
		// literal 0. A level-scaled fallback would charge mana for the buffs and toggles retail hands out for nothing.
		public static uint ResolveSpellManaCost(ushort spellManaCostField, uint casterMaxMana, bool isPercent) =>
			isPercent
				? (uint)((long)casterMaxMana * spellManaCostField / 100)
				: (uint)(spellManaCostField / 100); // wire hundredths -> the pool's own display units

		private static int Mitigate(int rawDamage, uint rating, uint halfPoint)
		{
			if (rawDamage <= 0 || rating == 0)
				return rawDamage;

			var mitigated = (double)rawDamage * halfPoint / (rating + halfPoint);
			return Math.Max(1, (int)Math.Round(mitigated, MidpointRounding.AwayFromZero));
		}
	}
}
