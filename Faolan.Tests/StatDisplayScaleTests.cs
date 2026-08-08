using Faolan.Core.Enums;
using Xunit;

namespace Faolan.Tests
{
	// The scale table stopped being a hand-maintained list and became a transcription of the client's own tooltip
	// renderer (#251, FUN_0149a300 + the two bool(Int32Stat_e) predicates it dispatches through). What licenses that
	// swap is that the transcription REPRODUCES every value the old table had established the slow way - each one an
	// in-client experiment against a rendered number - so those readings live on here as the gate rather than as
	// comments beside a switch nobody can re-derive.
	//
	// Two independent methods produced them:
	//   - the character sheet, via the `.setstat <id> <raw>` GM command (2026-07-27): push a raw value, read what the
	//     sheet draws, scale = raw / drawn.
	//   - item and feat TOOLTIPS, matched by value against the wire number we hold for that item/feat.
	public class StatDisplayScaleTests
	{
		// stat id -> the scale a human read off the screen, and how.
		public static TheoryData<uint, uint, string> Measured => new TheoryData<uint, uint, string>
			{
				// --- vitals: wire 428500 drew 4285 health, 205300 drew 2053 stamina.
				{ (uint)CharacterStatId.HealthMax, 100, "sheet: 428500 -> 4285" },
				{ (uint)CharacterStatId.StaminaMax, 100, "sheet: 205300 -> 2053" },
				{ (uint)CharacterStatId.ManaMax, 100, "sheet, with the vitals group" },

				// --- attributes: a 10-wire class base draws as 1 on a fresh Necromancer.
				{ (uint)CharacterStatId.Strength, 10, "sheet: class base 10 -> 1" },
				{ (uint)CharacterStatId.Constitution, 10, "sheet: class base 10 -> 1" },
				{ (uint)CharacterStatId.Intelligence, 10, "sheet: class base 10 -> 1" },
				{ (uint)CharacterStatId.Wisdom, 10, "sheet: class base 10 -> 1" },
				{ (uint)CharacterStatId.Dexterity, 10, "sheet: class base 10 -> 1" },
				// The sixth attribute, and the one id the client hardcodes beside the attribute run.
				{ (uint)CharacterStatId.Ferocity, 10, ".setstat 1403 10000 -> 1000" },

				// --- weapon-damage percents: the feat `Lotus Blades` holds 3800 on rank 1 and its tooltip reads +38%.
				{ (uint)CharacterStatId.WeaponDamageMelee, 100, "tooltip: Lotus Blades 3800 -> +38%" },
				{ (uint)CharacterStatId.WeaponDamageRanged, 100, "tooltip: the ranged twin of 1095" },

				// --- chance percents: 10000 wire == 100.00 percentage points.
				{ (uint)CharacterStatId.EvadeChance, 100, ".setstat 254 10000 -> 100.0% -> 200.0%" },
				{ (uint)CharacterStatId.ChanceOfOffhandAttack, 100, ".setstat 1071 10000 -> 0.0% -> 100.0%" },
				{ (uint)CharacterStatId.Immunity, 100, ".setstat 326 10000 -> 105.0% -> 205.0%" },
				{ (uint)CharacterStatId.SpellFatalityChance, 100, ".setstat 380 10000 -> 0.0% -> 100%" },
				// A multiplier, proven against an UNEQUAL base (451 = 4000 drew 8000, i.e. x2, not a flat +10000).
				{ (uint)CharacterStatId.BonusProtection, 100, ".setstat 334, re-probed against base 4000" },

				// --- measured x1. The client prints these through its integer formatter.
				{ (uint)CharacterStatId.Armor, 1, ".setstat 448 10000 -> 10002 (= 10000 + base 2)" },
				{ (uint)CharacterStatId.HitRating, 1, ".setstat 310 10000 -> 10000 in the Rating column" },
				{ (uint)CharacterStatId.CriticalDamageRating, 1, ".setstat 711 10000 -> 10000" },
				{ (uint)CharacterStatId.Protection, 1, ".setstat 451 10000 -> 10000, feeds all five schools" },
				{ (uint)CharacterStatId.CombatRating, 1, ".setstat 875 10000 -> 10003" },
				{ (uint)CharacterStatId.ImmunityRating, 1, "tooltip: Initiate's Wraps 32 -> +32" },
				{ (uint)CharacterStatId.CombatRatingFire, 1, "tooltip: Red Edge 11" },
				{ (uint)CharacterStatId.OffhandRating, 1, "tooltip: Red Edge 5" },
				{ (uint)CharacterStatId.CriticalRating, 1, "tooltip: Skirmisher 75, Helm 45" },

				// --- the two the old table flagged as the warning against inferring a scale from a neighbour: despite
				// the names, 0x0177 renders its raw value verbatim (the trailing % is a FORMAT, not a scale) while its
				// 0x017C sibling five ids away is a real ×100.
				{ (uint)CharacterStatId.FatalityChance, 1, ".setstat 375 10000 -> \"10000.0 %\"" },
				{ (uint)CharacterStatId.HateModifier, 1, ".setstat 170 10000 -> \"10000.0 %\"" },

				// --- tooltips, matched by value across several items.
				{ (uint)CharacterStatId.MagicDamage, 100, "tooltip: Royal Signet +52 Magic Damage from 5200" },
				{ (uint)CharacterStatId.NaturalManaRegen, 100, "tooltip: Initiate's Wraps 500 -> +5" },
				{ (uint)CharacterStatId.NaturalStaminaRegen, 100, "tooltip: Defiler Belt 150 -> +1.5" },
				{ (uint)CharacterStatId.MagicDamageHoly, 100, "tooltip: Poderes 2400 -> 24" }
			};

		[Theory]
		[MemberData(nameof(Measured))]
		public void TranscribedRule_ReproducesEveryHandMeasuredScale(uint statId, uint expected, string how)
		{
			Assert.Equal(expected, StatDisplayScale.Of(statId));
			Assert.Equal(expected, ((CharacterStatId)statId).GetScale());
			Assert.NotEmpty(how); // the provenance is the point of the row; keep it non-empty
		}

		// The ten stats #251 recorded as unreachable by measurement, because a level-1 character's sheet does not
		// render them. They are what the transcription buys: pinned here so a later re-transcription cannot quietly
		// drop them back to the ×1 default that used to hide a wrong scale.
		[Theory]
		[InlineData(920, 100)] // Attack Rating
		[InlineData(922, 100)] // Defense Rating
		[InlineData(923, 100)] // Magic Rating
		[InlineData(925, 100)] // Magical Resist
		[InlineData(252, 100)] // Miss Chance
		[InlineData(535, 100)] // Healing Efficiency
		[InlineData(861, 100)] // Magic Damage Modifier
		[InlineData(974, 1)] //   Damage Deflection
		[InlineData(1037, 1)] //  Knockback Resistance
		[InlineData(156, 1)] //   Chance of Being Staggered
		public void PreviouslyUnmeasurableStats_AreAnswered(uint statId, uint expected)
		{
			Assert.Equal(expected, StatDisplayScale.Of(statId));
		}

		// The five ids that sit in BOTH percent predicates. The client multiplies the two divisors (an explicit MULSS
		// against the same 100.0 constant), so ×10000 is the rule and not a transcription slip - which is worth a gate
		// of its own, because it is the one result here that looks like a bug.
		[Theory]
		[InlineData(0x0FF)] // Evade Level Growth
		[InlineData(0x13C)] // Variance
		[InlineData(0x13D)]
		[InlineData(0x140)] // Magic Variance
		[InlineData(0x397)] // Critical Blast Chance
		public void ComposedPercentStats_AreTenThousand(uint statId)
		{
			Assert.Equal(10000u, StatDisplayScale.Of(statId));
		}

		// An id in neither set is ×1 by the client's own integer-printing branch - a real answer now, not a default.
		[Fact]
		public void UnlistedStatsAreUnscaled()
		{
			Assert.Equal(1u, StatDisplayScale.Of(0));
			Assert.Equal(1u, StatDisplayScale.Of(0x7FFFFFFF));
		}
	}
}
