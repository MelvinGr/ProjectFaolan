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
		// Fallback NPC max health for a creature with NO captured spawn HP. The real per-creature HP is the captured
		// NpcSpawn.Health (the wire value retail sent); this is only used when that's absent (a GM-spawned template,
		// future content). It is NOT in the client RDB - the NpcSpawnDataExport stat tables aren't shipped (#76, see
		// memory rdb_npc_hp_not_in_client) - so this curve is the only source for an un-captured creature.
		public const uint DefaultNpcMaxHealth = 63; // the L1 civilian baseline (was a flat 100 placeholder)

		// The steady civilian-baseline slope past the last anchor (L40->60 grow ~53 HP/level), used to extrapolate.
		private const uint NpcHpPerLevelAboveCap = 53;

		// Captured CIVILIAN-baseline HP per level (NpcSpawns.Health; HP is level-derived - the same value recurs across
		// unrelated creatures, e.g. L20->933 for 43 NPCs, L40->2217 for 109). High-confidence anchors; HP is
		// piecewise-linear between them. Validates to <1% at unsampled levels (L35->1953, L45->2482, L11->386 vs 382).
		private static readonly (uint Level, uint Hp)[] NpcHpAnchors = [(1, 63), (5, 150), (10, 318), (15, 658), (20, 933), (30, 1688), (40, 2217), (50, 2746), (60, 3276)];

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

		// Provisional damage an NPC's swing deals to a player, scaled by the NPC's level (mirrors ResolveSpellDamage;
		// real NPC weapon/mitigation data isn't modelled yet). Level 0 maps to level 1.
		public static uint ResolveNpcAttackDamage(uint npcLevel)
		{
			var lvl = npcLevel == 0
				? 1u
				: npcLevel;
			return 5u + lvl * 3u;
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

		// The divisor turning the raw RDB weapon-damage template values (fields 286/285 = min/max, #395) into a
		// per-hit damage figure. PROVISIONAL: the field IDs are decoded authoritatively (GUIGetMin/MaxDamage), but the
		// exact display/combat scale isn't - ÷100 (the vitals convention) gives sane numbers (a starter dagger ~10, a
		// high-end greatsword ~80). Tune in-client alongside the real damage formula (still server-GDB, undecoded).
		public const int WeaponDamageScale = 100;

		// Melee swing damage: driven by the EQUIPPED WEAPON's damage (#395) instead of the flat level placeholder, so a
		// better weapon hits harder. Each swing ROLLS between the weapon's min and max (rollPercent 0..100 interpolates;
		// the caller passes a random roll for per-swing variance, default 50 = the midpoint), scaled down, plus a small
		// level contribution (so progression still shows and an early low-damage weapon isn't trivial). Falls back to the
		// level placeholder when the attacker has no weapon damage (unarmed, or a weapon whose RDB damage wasn't imported).
		// The divisor turning a raw Combat Rating (0x36B) into a flat melee damage bonus. PROVISIONAL (#399): Combat
		// Rating's real role is hit-rate (offense vs the target's defense), which needs a hit/miss model we don't have;
		// until then a geared attacker's Combat Rating (~14-262) adds a modest offensive bump. Tune in-client.
		public const int CombatRatingDamageDivisor = 10;

		public static int ResolveMeleeDamage(int? weaponMinDamage, int? weaponMaxDamage, ushort casterLevel, int rollPercent = 50, uint combatRating = 0)
		{
			var level = casterLevel == 0
				? (ushort)1
				: casterLevel;

			var combatBonus = (int)combatRating / CombatRatingDamageDivisor;

			if (weaponMinDamage is { } min && weaponMaxDamage is { } max && max > 0)
			{
				var roll = Math.Clamp(rollPercent, 0, 100);
				var rolled = min + (max - min) * roll / 100; // interpolate min..max
				return Math.Max(1, rolled / WeaponDamageScale + level + combatBonus);
			}

			return ResolveSpellDamage(0, level) + combatBonus;
		}

		// The half-mitigation point for the provisional armor curve: the total armor rating at which incoming physical
		// damage is halved. PROVISIONAL (#401) - the real mitigation is the server-GDB TotalArmorValue formula
		// (FUN_0113ba00: armor rating × Strength-scaled constants, then a LEVEL-normalized %), whose float constants
		// aren't extracted. Single armor pieces reach ~1500 and a worn set sums into the thousands, so this is set so a
		// geared set mitigates ~half while a starter's few hundred armor mitigates little; it ignores the real
		// level-normalization. Tune in-client (gaps.md / #401).
		public const uint ArmorHalfMitigation = 2000;

		// The half-mitigation point for the provisional MAGIC-damage curve (Protection rating 0x1C3). Protection values
		// are much smaller than Armor (a high piece ~185 vs armor ~1500), so the half-point is lower. Provisional (#401),
		// tune in-client.
		public const uint ProtectionHalfMitigation = 400;

		// Reduces incoming PHYSICAL damage by the defender's aggregated armor rating (#401), diminishing-returns:
		// mitigated = raw × K / (armor + K), so armor == K halves it and it never fully negates a hit (floor 1). Armor 0
		// (unarmored, or a type with no armor data) returns the raw damage unchanged.
		public static int ApplyPhysicalMitigation(int rawDamage, uint armorRating)
		{
			return Mitigate(rawDamage, armorRating, ArmorHalfMitigation);
		}

		// Reduces incoming MAGIC damage by the defender's aggregated Protection rating - the magical mirror of armor
		// mitigation (same diminishing-returns curve, its own half-point).
		public static int ApplyMagicMitigation(int rawDamage, uint protection)
		{
			return Mitigate(rawDamage, protection, ProtectionHalfMitigation);
		}

		private static int Mitigate(int rawDamage, uint rating, uint halfPoint)
		{
			if (rawDamage <= 0 || rating == 0)
				return rawDamage;

			var mitigated = (double)rawDamage * halfPoint / (rating + halfPoint);
			return Math.Max(1, (int)Math.Round(mitigated, MidpointRounding.AwayFromZero));
		}

		// Critical-hit tuning (#399), driven by the decoded crit stats: Critical Rating (0x138) = chance, Critical
		// Damage Rating (0x2C7) = magnitude. A crit multiplies the hit and rides the wire as DamageResult type 3
		// (2 = normal, 3 = crit/big hit - capture-derived, CombatEffectBuilder). The CHANCE/MAGNITUDE CURVES are still
		// provisional (the real crit formula is server-GDB) but now use the right stats; tune in-client.
		public const int BaseCritDamagePercent = 150; // a crit with no Critical Damage Rating hits for 1.5x
		public const int BaseCritChancePercent = 5; // everyone crits sometimes, even with no Critical Rating gear
		public const int MaxCritChancePercent = 75;

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

		// The critical-hit damage: the normal hit scaled by (base 1.5x + the attacker's Critical Damage Rating, each
		// point ~ +1%). So more Critical Damage Rating -> harder crits. Floored at 1.
		public static int ApplyCritDamage(int damage, uint criticalDamageRating)
		{
			return Math.Max(1, damage * (BaseCritDamagePercent + (int)criticalDamageRating) / 100);
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
	}
}
