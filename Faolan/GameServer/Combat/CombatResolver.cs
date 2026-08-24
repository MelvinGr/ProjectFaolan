using System;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;

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
	// ---------------------------------------------------------------------------------------------------------
	// WHERE THESE NUMBERS COME FROM (read this before changing one)
	//
	// Combat constants here are one of two things, and the comment on each says which:
	//   MEASURED  - derived from the capture corpus, with the sample size recorded. Change one only with new
	//               evidence, and say what capture it came from.
	//   PROVISIONAL - invented to be plausible because nothing decodes it yet (the real formula is server-side
	//               GDB, which we do not have). Tune freely in-client; that is what it is for.
	//
	// HOW TO RE-DERIVE THE MEASURED ONES
	//
	//     dotnet run --project Faolan.Importer -- --report npc-damage --captures <data-repo>/Captures
	//
	// It joins every captured damage record to the attacker's/target's SPAWN LEVEL and prints
	// hits/min/median/mean/max per level per damage school - the sample size sits next to each number, so a row
	// built on four hits is visibly not a curve.
	//
	// THE TWO DAMAGE PACKETS ARE DIFFERENT THINGS (#349, and the reason a first census reported "no magical NPCs"):
	//   DamageResultIIR 0xFEC4158A - the MELEE HIT. One per swing, big: 91..133 at level 1.
	//   StatDamageIIR   0xE9F24184 - SPELL hits and DoT TICKS. Small and repeating (8, 8, 8...) for a bleed;
	//                                large for a real cast. Its post-`delta` word is the damage SCHOOL, not a
	//                                stat id - censusing it as a stat finds nothing and reads as "no casters".
	// Both are sent in BOTH directions, so the same report measures NPC->player and player->NPC.
	//
	// THE SCALE IS SETTLED, which is what lets a damage number be compared with a health pool at all. Pairing each
	// hit with the target's next health update does NOT work - retail pushes only 4 such pairs in the whole corpus.
	// What works is the kill: the total damage dealt to an NPC that DIED equals the health its own spawn declared
	// (ratio 0.73..1.10). So damage, spawn health and player health share one unit, and a damage constant can be
	// read against NpcSpawn.Health directly. Getting this wrong is how `5 + 3 * level` survived: 8 damage against a
	// pool of thousands, with nothing in the code comparing the two.
	//
	// ⚠️ THE CORPUS ONLY REACHES LEVEL 6. Levels 1-6 have hundreds of hits each; above that it is single digits
	// (level 41: 20 hits spanning 182..28,368; levels 42/44/80: 1-4 hits). The measured curves are extrapolated
	// past level 6 and nobody should treat the high end as verified - Gitea #479 tracks the capture that would fix
	// it, and lists which fights are worth recording.
	// ---------------------------------------------------------------------------------------------------------
	public static class CombatResolver
	{
		// PROVISIONAL. Nothing in the corpus separates a crit from a big hit - DamageResult carries no crit flag
		// (the two trailing words are always 0), so a crit is just a larger number and cannot be counted.
		// ⚠️ But it IS measurable in-client: the retail client's own combat log (`/logcombat on`) labels them -
		// "%s hit %s for %d points of damage. Critical Hit!" - so these two constants are waiting on a log, not on
		// a decode. See docs/subsystems/combat.md.
		public const int BaseCritChancePercent = 5; // everyone crits sometimes, even with no Critical Rating gear

		// Critical-hit tuning (#399), driven by the decoded crit stats: Critical Rating (0x138) = chance, Critical
		// Damage Rating (0x2C7) = magnitude. A crit multiplies the hit and rides the wire as DamageResult type 3
		// (2 = normal, 3 = crit/big hit - capture-derived, CombatEffectBuilder). The CHANCE/MAGNITUDE CURVES are still
		// provisional (the real crit formula is server-GDB) but now use the right stats; tune in-client.
		// PROVISIONAL, same reason: unmeasurable from the wire, because a crit is not labelled as one.
		public const int BaseCritDamagePercent = 150; // a crit with no Critical Damage Rating hits for 1.5x

		// Melee swing damage: driven by the EQUIPPED WEAPON's damage (#395) instead of the flat level placeholder, so a
		// better weapon hits harder. Each swing ROLLS between the weapon's min and max (rollPercent 0..100 interpolates;
		// the caller passes a random roll for per-swing variance, default 50 = the midpoint), scaled down, plus a small
		// level contribution (so progression still shows and an early low-damage weapon isn't trivial). Falls back to the
		// level placeholder when the attacker has no weapon damage (unarmed, or a weapon whose RDB damage wasn't imported).
		// The divisor turning a raw Combat Rating (0x36B) into a flat melee damage bonus. PROVISIONAL (#399): Combat
		// Rating's real role is hit-rate (offense vs the target's defense), which needs a hit/miss model we don't have;
		// until then a geared attacker's Combat Rating (~14-262) adds a modest offensive bump. Tune in-client.
		// PROVISIONAL.
		public const int CombatRatingDamageDivisor = 10;
		public const int MaxCritChancePercent = 75;

		// The divisor turning the raw RDB weapon-damage template values (fields 286/285 = min/max, #395) into a
		// per-hit damage figure. PROVISIONAL: the field IDs are decoded authoritatively (GUIGetMin/MaxDamage), but the
		// exact display/combat scale isn't - ÷100 (the vitals convention) gives sane numbers (a starter dagger ~10, a
		// high-end greatsword ~80). Tune in-client alongside the real damage formula (still server-GDB, undecoded).
		// PROVISIONAL in its VALUE, but the corpus constrains it - and the useful form of that constraint is a
		// FRACTION, not a damage figure. Retail's player melee takes ~10% of a normal mob's health per hit at
		// levels 1-2 (~10 hits to kill) and 4..7% at 3-5 (`--report npc-damage`, the FRACTION section).
		//
		// ⚠️ Do NOT tune this against the absolute medians in the section above it. A level bucket mixes a
		// 63-health critter with a 5,000-health elite and a 300,000-health city guard, so "the median hit at level
		// 1 is 465..691" is an average over incomparable fights; measured as a fraction of each target's own
		// health it is a stable 10%. Reading the absolute number is what briefly made this look 50x wrong.
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
		// PROVISIONAL, and only reached when the level is unknown - MitigationFraction (the retail formula, with
		// retail's own tweak-table coefficients) is the real path.
		public const uint ArmorHalfMitigation = 2000;

		// What retail puts in `StatDamageIIR.SpellBaseId`: nothing. Measured over the corpus - **3,059 of 3,061 records
		// carry 0**, and it is total for every stat id we send (all 50 with statId 10, all 2,892 with statId 0, all 119
		// with statId 5); the two exceptions name an id absent from the whole spell catalog.
		//
		// Both emitters go through this one constant (the spell-cast hit, #767, and the DoT tick, #770) so they cannot
		// drift apart again - the DoT path was left passing `buff.BuffId >> 8` when the cast path was fixed, precisely
		// because that IS a real spell id and so produced no client error to notice.
		public const uint RetailSpellBaseId = 0;

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
		// Capture-anchored NPC health by level - and the reason NpcSpawn.Health spans 9 to 1,108,500 without being
		// broken: retail's world genuinely holds two populations. Ordinary mobs follow this curve (a level-1 is 63,
		// a level-10 is 318), while guards, bosses and target dummies carry thousands to millions and are not meant
		// to die. Any per-level average over NpcSpawn.Health mixes the two and means nothing.
		private static readonly (uint Level, uint Hp)[] NpcHpAnchors = [(1, 63), (5, 150), (10, 318), (15, 658), (20, 933), (30, 1688), (40, 2217), (50, 2746), (60, 3276)];

		// The critical-hit damage: the normal hit scaled by (base 1.5x + the attacker's Critical Damage Rating, each
		// point ~ +1%). So more Critical Damage Rating -> harder crits. Floored at 1.
		public static int ApplyCritDamage(int damage, uint criticalDamageRating)
		{
			return Math.Max(1, damage * (BaseCritDamagePercent + (int)criticalDamageRating) / 100);
		}

		// #633: a SPELL's own critical chance, in hundredths of a percent, plus what the caster's Critical Rating adds
		// on the same curve a swing uses. RDB field 147 `SpellCriticalChance` - 250 is 2.50%, which is its modal value.
		//
		// ⚠️ A spell whose record states NO chance does not crit at all, rather than falling back to the melee base.
		// The melee base exists because "everyone crits sometimes" with a weapon; extending it to the 20,278 spell
		// records that state nothing would be giving every one of them a 5% crit on no evidence, where the 1,224 that
		// DO state one are the client saying which spells crit and how often.
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
		// 607 of the 1,228 records that carry it - is +50%, i.e. the 1.5x <see cref="BaseCritDamagePercent" /> already
		// assumed. So the client's own spell table corroborates a constant this file had written down as provisional
		// and "waiting on an in-client combat log". A record stating none takes that base.
		public static int ApplySpellCritDamage(int damage, uint spellCriticalDamageHundredths, uint criticalDamageRating)
		{
			var bonusHundredths = spellCriticalDamageHundredths > 0
				? (int)spellCriticalDamageHundredths
				: (BaseCritDamagePercent - 100) * 100;

			return Math.Max(1, (int)((long)damage * (PercentScale + bonusHundredths + ((int)criticalDamageRating * 100)) / PercentScale));
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
			var target = level * targetPerLevel;

			// The client's closed form, taken when its ARMOR_MITIGATION table has no matching row - which on this build
			// is ALWAYS: the table loads but the vector the lookup walks is empty, so it logs
			// "No mitigation entry found" and falls through. Hence this IS the client's effective behaviour, not a
			// degraded path. If a build ever ships rows, they interpolate a (Min,Max) pair instead (docs/subsystems/combat.md).
			var fraction = total >= target
				? total / (target + total)
				: total / target * 0.5;

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
		// i.e. very close to 100 per level, which is also ~1.5% of a same-level player's health pool.
		//
		// ✅ NO LONGER AN EXTRAPOLATION (2026-07-29, #479). The high-level captures this line was waiting for exist
		// (`NecroL80_mage-necromancer_login_combat{,2}`, a level-80 necromancer in Poitain), and the line HOLDS at the
		// top of the range - `--report npc-damage`, the BY ATTACKER section, measured against a level-80 player:
		//
		//     level 75 Hired Bodyguard      7 hits   median 7324   vs 7500   = 0.98 x model
		//     level 75 Zingaran Operative  10 hits   median 7200   vs 7500   = 0.96 x model
		//
		// Two unrelated templates, independently within 4% of `level * 100` at level 75. The second capture carries
		// the client's own combat log for the same fight, so those wire figures are confirmed against what the player
		// SAW (67-92 displayed, wire x100) rather than inferred.
		//
		// ⚠️ This line is the swing against a player AT THE MOB'S OWN LEVEL. Retail scales it down hard once the mob
		// is well below the player - see LevelGapPercent below, which is where that lives - so the low `x model`
		// ratios in the report are that penalty, not evidence against this constant.
		//
		// A second per-archetype factor is visible and also unmodelled: RANGED mobs sit near 0.2 even at gap 0
		// (`Poacher Marksman` 0.17, `Bat Pict Archer` 0.21) where same-level melee is ~1.0.
		//
		// ⚠️ The previous curve was `5 + 3 * level`, i.e. 8 at level 1 against a captured NPC/player health pool in
		// the THOUSANDS - about 15x too weak, and the mismatch was invisible because nothing compared the two. The
		// scale is settled: total damage dealt to a killed NPC equals the health its spawn declared, ratio 0.73..1.10
		// over the corpus, so damage and health share one unit.
		public const uint NpcAttackDamagePerLevel = 100;

		// ⚠️ THE CONSTANT ABOVE IS IN WIRE UNITS, AND EVERYTHING THIS DAMAGE LANDS IN IS IN DISPLAY UNITS (#572). It has
		// to be converted here or an NPC hits a hundred times too hard. Every other figure in this fight is display:
		// `NpcHpAnchors` starts at 63 for level 1, `NpcSpawn.Health` is 63 where the same row's wire `Life` is 6300,
		// `ResolveMeleeDamage` already divides by `WeaponDamageScale`, and a player's MaxHealth is the ÷100 vitals
		// value (`CharacterStatId.GetScale`). Only this line was left on the wire scale, so a level-1 mob swung 100
		// into a 63-point pool and a level-3 swung 300 into a level-1 player's 88 - every hit lethal on contact, which
		// is exactly what it did in-client.
		//
		// The conversion is the SAME ÷100 the vitals stats declare, taken from the stat itself rather than a second
		// hand-written 100, because this damage is spent against a health pool and must move if that scale ever does.
		// The measured curve is deliberately left in wire units: its whole evidence base above is in wire numbers
		// (medians per level, the level-75 cross-checks), and rewriting it to `level * 1` would make every one of those
		// citations unreadable. Floored at 1 so a level-1 swing still does something.
		//
		// Sanity, in display units: level 1 deals 1 against a level-1 player's ~88 pool = 1.1% a hit, which is the
		// "~1.5% of a same-level player's health pool" the measurement above states; and the player's own ~6 a swing
		// against a 63-health critter is the ~10 hits to kill that `WeaponDamageScale` is pinned to.
		//
		// `targetLevel` is who is being HIT: retail's level-gap penalty is applied here (#488), so pass the defender's
		// level rather than the mob's. A caller measuring the bare per-level curve passes `npcLevel` for both, which
		// is the un-penalised case by construction.
		//
		// `ranged` is whether the mob SHOOTS rather than swings (`NpcWeapon.IsRanged`) - see RangedPercent.
		public static uint ResolveNpcAttackDamage(uint npcLevel, uint targetLevel, bool ranged = false)
		{
			return Math.Max(1u, NpcAttackDamageWire(npcLevel, targetLevel, ranged) / CharacterStatId.HealthMax.GetScale());
		}

		// The same curve BEFORE the display conversion, i.e. in the units the CAPTURES are in.
		//
		// ⚠️ This exists because the two scales were compared against each other for real. `--report npc-damage`
		// scores every captured attacker against `ResolveNpcAttackDamage`, and captured damage is a WIRE number - so
		// when #572 moved the resolver's return to display units, every `x model` in that report silently multiplied
		// by 100. A level-1 `Pillager` read as "139x the model" instead of 1.39x, and #671's whole elite/archetype
		// analysis is read off that column. A measurement tool that is wrong by a constant is worse than one that is
		// missing, because it still looks like evidence.
		public static uint NpcAttackDamageWire(uint npcLevel, uint targetLevel, bool ranged = false)
		{
			var scale = LevelGapPercent(npcLevel, targetLevel) * (ranged ? RangedPercent : 100u) / 100;
			return Math.Max(1u, npcLevel) * NpcAttackDamagePerLevel * scale / 100;
		}

		// A RANGED mob deals about a fifth of what a melee mob of the same level does - a factor independent of the
		// level gap, and visible at gap 0-1 where the per-level curve itself is verified (#671).
		//
		// Measured by joining `--report npc-damage`'s BY ATTACKER rows to each attacker's MAIN-HAND weapon class
		// (`Items.SearchCategory`, the client's own catalog value - not a name match, #244):
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
		// ⚠️ Every melee class sits at ~1.0, which is what makes this a RANGED factor rather than a per-weapon damage
		// model. A weapon roll would not do that: the bow's own catalog damage (819-5733) is HIGHER than the 1h
		// edge's (505-2864), i.e. the opposite of what is measured.
		private const uint RangedPercent = 19;

		// Retail's LEVEL-GAP penalty: a mob well below the player barely scratches them, and the drop is a CLIFF, not
		// a slope - full damage out to a five-level gap, then two steps to a ~0.11 floor that holds all the way down.
		// Returned as a percent so the arithmetic above stays integral.
		//
		// Measured by (attacker TEMPLATE, mob level, player level) - `--report npc-damage`, the BY ATTACKER section.
		// Keyed by TEMPLATE because one NAME spans several templates and levels (#244), and per-attacker because a
		// level bucket mixes archetypes. `x model` is the measured median over `level * 100`:
		//
		//     gap 0-1   0.77 .. 1.57   11 beach templates, 700+ hits   (a same-level player)
		//     gap 5     0.96, 0.99, 1.35   43 hits, 3 templates at level 75
		//     gap 6     0.42 (11 hits, template 15441), 0.76 (1 hit)
		//     gap 7     0.11, 0.13     14 hits, templates 15437/15441 at level 73
		//     gap 8-10  0.10 .. 0.14   26 hits, templates 15436/15441 at level 70-72
		//     gap 20-21 0.11           9 hits, Striped Boar at level 59/60
		//     gap 27+   0.03 .. 0.11   drifts slightly lower; one floor covers it
		//
		// The single strongest piece is template 15441 `Hired Bodyguard` against ONE level-80 player, by its own
		// level: 729 at 70, 732 at 71, 938 at 73, 3125 at 74, 7402 at 75. A 10x swing over five of the ATTACKER's
		// levels with the defender, its gear and the attacker's template all held fixed. That is also why it is not
		// armour - a level-normalised mitigation term cannot differ 8x across three levels of attacker - and the same
		// fight carries the client's combat log, showing 7-14 displayed from a level-72 mob and 67-92 from a
		// level-75 one.
		//
		// ⚠️ A MEASURED TABLE, NOT A RECOVERED RULE. Retail decides this server-side: the client's tweak table
		// (RDB 1000516) carries no level-difference constant - only TWK_TargetPerLevelArmor/Protection and
		// TWK_Consequence_XPGainDecayRate - so there is nothing in the client to read, and it is undocumented
		// publicly. The two plateaus are well sampled; the STEP between them rests on one template's 12 hits at
		// gap 6, so that is the number to revisit when a capture at gaps 2-4 or another gap-6 fight arrives.
		//
		// ⚠️ The mirror case - a mob ABOVE the player - is deliberately NOT modelled and returns 100. Two rows
		// measure it (level-42 Nemedians against a level-23 player, 5.15 and 6.99) and they point the right way, but
		// they are ONE hit each; and a higher-level mob already hits harder through `level * 100`. Amplifying damage
		// 6x on two hits would be inventing, and the failure mode - a mob a few levels up one-shotting a player -
		// is the expensive direction to get wrong.
		//
		// ⚠️ Elite/boss attackers sit far ABOVE this table (`Black Ring Assassin`, and the level-80 `Eerika of the
		// Maidens` / `Boros` / `Ravenous Crocodile Follower` cluster at ~3x) and ranged mobs far below it
		// (`Poacher Marksman` 0.17 at gap 0). Both are per-ARCHETYPE and independent of the gap; neither is modelled
		// here, and neither is evidence against the gap curve.
		//
		// ⛔ `Wild Boar` used to be cited here as a 2.15x elite outlier at gap 55. It is not one: its nine hits were
		// mob-on-mob damage that the report counted as NPC->player, and the row is gone (#479 - see combat.md).
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

		// Spell damage: the level-scaled base plus the caster's aggregated Magic Damage rating (#399), added flat like
		// retail's spell-damage bonus. magicDamage is 0 for NPC casts / callers without gear. Per-spell power isn't
		// decoded (every spell shares the base), so this is still provisional; Magic Damage at least makes geared
		// casters hit harder.
		// `spellLinePercentHundredths` is the caster's FEAT boost for this spell's line (#590) - 3500 = +35%, which is
		// what `Decomposition` rank 5 grants every rank of Flesh to Worms. It scales the whole result, Magic Damage
		// included, because the client's own wording is "Increase to damage or healing on <spell>" rather than a
		// bonus to one term. Applied last so the percent is over the finished number.
		public static int ResolveSpellDamage(uint spellId, ushort casterLevel, uint magicDamage = 0, int spellLinePercentHundredths = 0)
		{
			var level = casterLevel == 0
				? (ushort)1
				: casterLevel;
			return ApplyPercentBonus(20 + level * 5 + (int)magicDamage, spellLinePercentHundredths);
		}

		// A percent bonus stored x100, applied over a FINISHED damage number. Shared by the spell-line feat boost
		// (#590) and the combo feat bonuses (#697) so the two cannot drift on rounding or on the floor rule.
		//
		// A debuff-shaped negative can only ever take the hit to 1, never to 0 or below - a boosted attack that stops
		// dealing damage would read as a miss.
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
		// Damage. RECOVERED FROM THE CLIENT, not fitted - its magic-damage accessor FUN_01118ce0 reads the tweak into
		// XMM0, multiplies by the attribute score and truncates:
		//
		//     MOVSS XMM0, [0x02d24370]     ; the tweak's value (the Tweak_c object at 0x2d24368 + 8)
		//     CALL  0x007184d0             ; attribute score -> XMM0
		//     MULSS XMM0, [ESP+0xc]        ; x tweak
		//     MULSS XMM0, [0x01f24468]     ; x 100 (the wire's hundredths)
		//     CVTTSS2SI EBX, XMM0          ; truncate, not round
		//
		// so `magicDamage = schoolStat + trunc(attribute x 0.6) + flatGearMagicDamage` in DISPLAY units.
		// See docs/rdb/spell-scaling.md.
		private const uint MagicalDpsAbilityModifierPerMille = 600;

		// Which ATTRIBUTE a caster's magic damage scales off. The client picks it by CLASS, not by spell
		// (FUN_01118ce0): Wisdom for every caster, EXCEPT Necromancer (0x29), Herald of Xotli (0x2B) and Demonologist
		// (0x2C), which use Intelligence.
		//
		// ✅ CONFIRMED INDEPENDENTLY by the RDB. Spell property field 847 (Spell.ScalingAttribute) names an attribute
		// per SPELL, and grouping the 716 records that carry one by their spell line's class reproduces this rule
		// exactly: Necromancer 153, Herald 41 and Demonologist 126 records on Intelligence; Priest of Mitra 32,
		// Tempest of Set 48 and Dark Templar 3 on Wisdom (and the Conqueror's 5 on Strength, a non-caster taking the
		// physical branch). Two unrelated sources, no disagreement.
		//
		// `spellScalingAttribute` is that field, and it OVERRIDES the class rule - which is the only reason it exists,
		// since the class rule already covers every spell. 0 (the overwhelming majority of templates) means "no
		// override"; a Demonologist's six Wisdom-scaled spells are what it is for.
		public static CharacterStatId MagicDamageAttribute(ConanClass casterClass, uint spellScalingAttribute = 0)
		{
			if (spellScalingAttribute != 0)
				return (CharacterStatId)spellScalingAttribute;

			return casterClass is ConanClass.Necromancer or ConanClass.HeraldOfXotli or ConanClass.Demonologist
				? CharacterStatId.Intelligence
				: CharacterStatId.Wisdom;
		}

		/// <summary>
		///     A caster's Magic Damage in DISPLAY units: the flat rating their worn gear grants, plus the
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

		// The mana a cast costs, RETAIL'S OWN FORMULA rather than a placeholder (#629). The client reads the spell's
		// RDB field 132 (Spell.ManaCost, carried raw) in two modes, both of which land in the wire's hundredths and
		// are divided by 100 for display:
		//
		//   percent mode  cost = manaMax * field / 100      Ice Strike 9 -> 9% of the pool
		//   flat mode     cost = field                      Dark Knowledge (Rank 4) 42200 -> 422 mana
		//
		// Which mode is in force is STATED by the record, not inferred (#630): bit 2 of the spell record's flags word,
		// imported into Spell.ManaCostIsPercent, is the same bit the client reads at `spell + 0xC8`.
		//
		// A cost of 0 means FREE, which is retail's reading too rather than a gap papered over: the client's property
		// accessor returns 0 for a record with no field 132, and 3,783 of the 5,496 records that DO carry it carry a
		// literal 0. The old level-scaled placeholder (`10 + level * 2`) is gone - it charged mana for the buffs and
		// toggles retail hands out for nothing.
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
