using Faolan.Core.Enums;
using Xunit;

namespace Faolan.Tests
{
	// The suffix table is a transcription of the client's own `FUN_01119f20`, so what a gate can usefully
	// assert is that it reproduces the lines a human has read off the screen - the same licence
	// `StatDisplayScaleTests` gives the divisor table next door.
	//
	// The oracle here is the in-client `Gangrenous Stench` tooltip (screenshots), whose every number is
	// the product of a divisor and a suffix. It is the case that makes the two tables' independence visible: the four
	// invulnerability ratings are a ÷100 that draws `%`, `Damage Multiplier` is a ÷1 that draws `%`, and the vitals
	// are a ÷100 that draws nothing. Deriving either table from the other gets two of those three wrong.
	public class StatUnitSuffixTests
	{
		// stat id -> the unit, and the line it was read from.
		public static TheoryData<uint, StatUnit, string> Rendered => new()
		{
			// Gangrenous Stench: "-10% Slashing / Piercing / Crushing / Poison Invulnerability" off a stored -1000.
			{ (uint)CharacterStatId.SlashingInvulnerability, StatUnit.Percent, "Gangrenous Stench: -1000 -> -10%" },
			{ (uint)CharacterStatId.PiercingInvulnerability, StatUnit.Percent, "Gangrenous Stench: -1000 -> -10%" },
			{ (uint)CharacterStatId.CrushingInvulnerability, StatUnit.Percent, "Gangrenous Stench: -1000 -> -10%" },
			{ (uint)CharacterStatId.PoisonInvulnerability, StatUnit.Percent, "Gangrenous Stench: -1000 -> -10%" },

			// The same tooltip's "-5% Damage Multiplier" off a stored -5 - a percent whose divisor is 1.
			{ (uint)CharacterStatId.DamageMultiplier, StatUnit.Percent, "Gangrenous Stench: -5 -> -5%" },

			// A vital is a ÷100 with no suffix: the sheet draws 4285, not 4285%.
			{ (uint)CharacterStatId.HealthMax, StatUnit.None, "character sheet: 428500 -> 4285" },
			{ (uint)CharacterStatId.StaminaMax, StatUnit.None, "character sheet: 205300 -> 2053" },

			// An attribute is a ÷10 with no suffix.
			{ (uint)CharacterStatId.Strength, StatUnit.None, "character sheet: class base 10 -> 1" },

			// A rating draws raw and bare - Initiate's Wraps reads "+32", not "+32%".
			{ (uint)CharacterStatId.HitRating, StatUnit.None, "tooltip: a rating draws bare" },

			// The third unit, and the reason it is carried: a spell's range is metres.
			{ (uint)CharacterStatId.AttackRange, StatUnit.Metres, "the m literal at 0x01fb3ac8" },
			{ (uint)CharacterStatId.SpellColumnRadius, StatUnit.Metres, "SpellColumnRadius, the only other metre id" },

			// The counter-intuitive pair `StatDisplayScaleTests` calls out, and this table explains it. That one
			// records 0x0177 as a ÷1 whose tooltip nevertheless ends in a percent sign, with the note "the trailing %
			// is a format, not a scale". This is that format: both ids draw `%`, and only 0x017C also divides.
			{ (uint)CharacterStatId.FatalityChance, StatUnit.Percent, "0x177: raw value, percent sign" },
			{ (uint)CharacterStatId.SpellFatalityChance, StatUnit.Percent, "0x17C: the ×100 twin, same sign" }
		};

		[Theory]
		[MemberData(nameof(Rendered))]
		public void Suffix_MatchesWhatTheClientDraws(uint statId, StatUnit expected, string source)
		{
			Assert.True(expected == StatUnitSuffix.Of(statId),
				$"stat 0x{statId:X} ({source}): expected {expected}, table says {StatUnitSuffix.Of(statId)}");
		}

		// The suffix and the divisor are different tables and this is the assertion that keeps anyone from
		// collapsing them: a ÷100 that draws nothing and a ÷1 that draws a percent both exist, so neither set is a
		// function of the other.
		[Fact]
		public void UnitAndScale_AreIndependent()
		{
			Assert.Equal(100u, StatDisplayScale.Of((uint)CharacterStatId.HealthMax));
			Assert.Equal(StatUnit.None, StatUnitSuffix.Of((uint)CharacterStatId.HealthMax));

			Assert.Equal(1u, StatDisplayScale.Of((uint)CharacterStatId.DamageMultiplier));
			Assert.Equal(StatUnit.Percent, StatUnitSuffix.Of((uint)CharacterStatId.DamageMultiplier));
		}

		// The literals the client appends, so a caller building a line does not have to know which is which.
		[Fact]
		public void SuffixOf_IsTheClientsLiteral()
		{
			Assert.Equal("%", StatUnitSuffix.SuffixOf((uint)CharacterStatId.DamageMultiplier));
			Assert.Equal("m", StatUnitSuffix.SuffixOf((uint)CharacterStatId.AttackRange));
			Assert.Equal("", StatUnitSuffix.SuffixOf((uint)CharacterStatId.HealthMax));
		}
	}
}
