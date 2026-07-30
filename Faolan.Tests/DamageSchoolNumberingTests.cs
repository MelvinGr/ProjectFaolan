using Faolan.Core.Enums;
using Xunit;

namespace Faolan.Tests
{
	// The damage-school numbering (#487). Cold used to sit at 5 with everything after it one too high, which made a
	// necromancer's Pestilential Blast decode as Holy and their Ice Strike as Siege.
	//
	// Two independent oracles pin it, and they agree:
	//
	//   1. the client's own combat log (`/logcombat on`, recorded alongside the pcap in
	//      NecroL80_mage-necromancer_login_combat2) names `unholy damage` = 7 and `frost damage` = 10;
	//   2. every per-school stat family in LDB category 615 lists NINE schools with Cold absent from the run -
	//      Invulnerability 902..910, Damage 995..1003, Damage (ranged) ..988..994, Protection 926..931,
	//      Combat Rating 1007..1011 - so numbering that run from 1 puts Unholy at 7 and leaves Cold at 10.
	public class DamageSchoolNumberingTests
	{
		// The combat log's two named schools - the observations the old numbering could not produce.
		[Fact]
		public void TheCombatLogsNamedSchools_Match()
		{
			Assert.Equal(7u, (uint)DamageSchool.Unholy); // "unholy damage"
			Assert.Equal(10u, (uint)DamageSchool.Cold); // "frost damage"
		}

		// The nine-school run the client's stat table lists, in order, with Cold NOT among them.
		[Theory]
		[InlineData(DamageSchool.Slashing, 1)]
		[InlineData(DamageSchool.Piercing, 2)]
		[InlineData(DamageSchool.Crushing, 3)]
		[InlineData(DamageSchool.Fire, 4)]
		[InlineData(DamageSchool.Electrical, 5)]
		[InlineData(DamageSchool.Holy, 6)]
		[InlineData(DamageSchool.Unholy, 7)]
		[InlineData(DamageSchool.Poison, 8)]
		[InlineData(DamageSchool.Siege, 9)]
		public void TheNineSchools_FollowTheClientsStatTableOrder(DamageSchool school, uint expected)
		{
			Assert.Equal(expected, (uint)school);
		}

		// Cold is APPENDED after the nine, not inserted at 5. Asserted as a relationship rather than a bare number so
		// the intent survives: whatever Siege is, Cold is the next one.
		[Fact]
		public void Cold_ComesAfterTheNine_NotInTheMiddle()
		{
			Assert.Equal((uint)DamageSchool.Siege + 1, (uint)DamageSchool.Cold);
			Assert.True((uint)DamageSchool.Cold > (uint)DamageSchool.Fire + 1, "Cold must not sit immediately after Fire - that was the #487 bug");
		}

		// The two values outside the school range proper. Environmental is what fall damage and hazards carry (#495 /
		// #504); Heal marks a restore and, uniquely, only ever appears with a non-negative delta.
		[Fact]
		public void TheNonSchoolValues_AreOutsideTheRun()
		{
			Assert.Equal(12u, (uint)DamageSchool.Environmental);
			Assert.Equal(23u, (uint)DamageSchool.Heal);
			Assert.True((uint)DamageSchool.Environmental > (uint)DamageSchool.Cold);
		}

		// A necromancer's own spells must no longer read as the wrong element - the symptom that opened the ticket.
		[Fact]
		public void ANecromancersSpells_NoLongerDecodeAsHolyAndSiege()
		{
			Assert.NotEqual(DamageSchool.Holy, (DamageSchool)7u); // Pestilential Blast / Flesh to Worms / Gangrenous Stench
			Assert.NotEqual(DamageSchool.Siege, (DamageSchool)10u); // Ice Strike / Chill
			Assert.Equal(DamageSchool.Unholy, (DamageSchool)7u);
			Assert.Equal(DamageSchool.Cold, (DamageSchool)10u);
		}
	}
}
