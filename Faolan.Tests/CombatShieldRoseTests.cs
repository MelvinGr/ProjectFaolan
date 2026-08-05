using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.GameServer.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Directional combat (#20) - the defender's shield rose. Every expectation here is a measurement, so the class
	// doubles as the record of the decode:
	//   * the 10 packed distributions and the "one shield moves" transition shape come from the 110 CombatShieldIIR
	//     records / 39 transitions in the capture corpus;
	//   * the mitigation table comes from the client's own balance sheet (RDB 1000516 `Combat_PvE{0..3}ShieldBonus`
	//     0.2/0/-0.33/-0.66 and `Combat_PvP{0..3}ShieldBonus` 0.2/0/-0.15/-0.30), which the client registers and never
	//     reads - so they are the SERVER's numbers;
	//   * the direction ability ids are the LDB names of the swing tail's last word (769094 Upper Left Attack,
	//     769095 Upper Right Attack, 769099 Overhead Attack).
	public class CombatShieldRoseTests
	{
		// Every distinct value the corpus carries, with its occurrence count. All ten sum to three shields.
		public static readonly TheoryData<uint> CapturedDistributions = [21u, 111u, 210u, 102u, 3u, 12u, 30u, 201u, 120u, 300u];

		[Theory, MemberData(nameof(CapturedDistributions))]
		public void EveryCapturedDistribution_SpreadsExactlyThreeShields(uint value)
		{
			var (left, mid, right) = CombatShieldDistribution.Unpack(value);
			Assert.Equal(CombatShieldRose.TotalShields, left + mid + right);
		}

		[Theory, MemberData(nameof(CapturedDistributions))]
		public void EveryCapturedDistribution_IsOneTheRollCanProduce(uint value)
		{
			var rolled = new HashSet<uint>();
			var random = new Random(1);
			for (var i = 0; i < 1000; i++)
				rolled.Add(CombatShieldRose.Roll(random));

			Assert.Contains(value, rolled);
		}

		[Fact]
		public void Roll_OnlyEverProducesTheTenCapturedDistributions()
		{
			var random = new Random(7);
			for (var i = 0; i < 2000; i++)
			{
				var (left, mid, right) = CombatShieldDistribution.Unpack(CombatShieldRose.Roll(random));
				Assert.Equal(CombatShieldRose.TotalShields, left + mid + right);
			}
		}

		// 34 of the 39 captured transitions move exactly one shield between two arcs (4 re-send the same value; the one
		// 3-shield jump reads as a lossy capture collapsing steps). So a shift is a single-shield move, always.
		[Fact]
		public void Shift_MovesExactlyOneShieldToADifferentArc()
		{
			var random = new Random(11);
			var value = CombatShieldRose.Roll(random);
			for (var i = 0; i < 500; i++)
			{
				var shifted = CombatShieldRose.Shift(value, random);
				var before = Arcs(value);
				var after = Arcs(shifted);

				Assert.Equal(CombatShieldRose.TotalShields, after.Sum(c => c));
				var delta = after.Zip(before, (a, b) => (int)a - (int)b).ToArray();
				Assert.Equal(1, delta.Count(d => d == 1));
				Assert.Equal(1, delta.Count(d => d == -1));
				Assert.Equal(1, delta.Count(d => d == 0));
				value = shifted;
			}
		}

		[Theory, InlineData(111u), InlineData(3u), InlineData(300u)]
		public void Shift_NeverTakesAShieldOffAnEmptyArc(uint start)
		{
			var random = new Random(3);
			var value = start;
			for (var i = 0; i < 200; i++)
			{
				value = CombatShieldRose.Shift(value, random);
				var (left, mid, right) = CombatShieldDistribution.Unpack(value);
				Assert.True(left <= CombatShieldRose.TotalShields && mid <= CombatShieldRose.TotalShields && right <= CombatShieldRose.TotalShields);
				Assert.Equal(CombatShieldRose.TotalShields, left + mid + right);
			}
		}

		// A value that is not a three-shield rose has nothing to move: the shift is a no-op rather than inventing one.
		[Theory, InlineData(0u), InlineData(999u)]
		public void Shift_LeavesANonRoseValueAlone(uint value)
		{
			Assert.Equal(value, CombatShieldRose.Shift(value, new Random(5)));
		}

		[Fact]
		public void NextShiftSeconds_StaysInsideTheCapturedCadence()
		{
			var random = new Random(13);
			for (var i = 0; i < 500; i++)
			{
				var seconds = CombatShieldRose.NextShiftSeconds(random);
				Assert.InRange(seconds, CombatShieldRose.MinShiftSeconds, CombatShieldRose.MaxShiftSeconds);
			}
		}

		[Theory, InlineData(769094u, ShieldArc.Left), InlineData(769096u, ShieldArc.Left), InlineData(769095u, ShieldArc.Right),
		 InlineData(769097u, ShieldArc.Right), InlineData(769099u, ShieldArc.Mid)]
		public void ArcFor_MapsTheDirectionAbilitiesToTheirArc(uint abilityId, ShieldArc expected)
		{
			Assert.Equal(expected, CombatShieldRose.ArcFor(abilityId));
		}

		// A combo step / spell / plain unarmed jab carries no direction ability, so it takes no arc modifier at all -
		// the flat behaviour every non-directional path had before this landed.
		[Theory, InlineData(0u), InlineData(769098u), InlineData(108900u)]
		public void ArcFor_IsNullForAnUndirectedAbility(uint abilityId)
		{
			Assert.Null(CombatShieldRose.ArcFor(abilityId));
		}

		// The client's Combat_PvE{n}ShieldBonus table, read straight off RDB 1000516.
		[Theory, InlineData(0u, 1.20), InlineData(1u, 1.00), InlineData(2u, 0.67), InlineData(3u, 0.34)]
		public void DamageMultiplier_IsThePvEShieldBonusTable(uint shieldsOnArc, double expected)
		{
			// Put `shieldsOnArc` guards on Mid and swing overhead into them.
			var distribution = CombatShieldDistribution.Pack(0, shieldsOnArc, CombatShieldRose.TotalShields - shieldsOnArc);
			Assert.Equal(expected, CombatShieldRose.DamageMultiplier(distribution, ShieldArc.Mid), 3);
		}

		[Theory, InlineData(0u, 1.20), InlineData(1u, 1.00), InlineData(2u, 0.85), InlineData(3u, 0.70)]
		public void DamageMultiplier_IsThePvPShieldBonusTable(uint shieldsOnArc, double expected)
		{
			var distribution = CombatShieldDistribution.Pack(0, shieldsOnArc, CombatShieldRose.TotalShields - shieldsOnArc);
			Assert.Equal(expected, CombatShieldRose.DamageMultiplier(distribution, ShieldArc.Mid, true), 3);
		}

		[Fact]
		public void DamageMultiplier_IsUnchangedForAnUndirectedSwing()
		{
			Assert.Equal(1.0, CombatShieldRose.DamageMultiplier(300, null));
		}

		// The point of the system: the same swing is worth ~3.5x more into the unguarded arc than into the stacked one.
		[Fact]
		public void ApplyArcMitigation_RewardsAimingAtTheWeakestArc()
		{
			const uint distribution = 300; // three shields on Left, none on Mid/Right
			Assert.Equal(34u, CombatShieldRose.ApplyArcMitigation(100, distribution, ShieldArc.Left));
			Assert.Equal(120u, CombatShieldRose.ApplyArcMitigation(100, distribution, ShieldArc.Mid));
			Assert.Equal(120u, CombatShieldRose.ApplyArcMitigation(100, distribution, ShieldArc.Right));
		}

		// A landed hit still lands: a fully guarded arc reduces a 1-damage swing to 1, never to 0 (a 0 would read as a
		// miss on the wire, which the arc system does not model).
		[Fact]
		public void ApplyArcMitigation_NeverErasesALandedHit()
		{
			Assert.Equal(1u, CombatShieldRose.ApplyArcMitigation(1, 300, ShieldArc.Left));
			Assert.Equal(0u, CombatShieldRose.ApplyArcMitigation(0, 300, ShieldArc.Left));
		}

		// #620: a player's rose is the only one that arrives from OUTSIDE, so it is the only one that needs the client's
		// own overflow rule applied to it - FUN_0113de90 resets stat 0xA5 to 111 when the digits sum above three.
		[Theory, InlineData(300u, 300u), InlineData(111u, 111u), InlineData(21u, 21u), InlineData(0u, 0u), InlineData(333u, 111u), InlineData(999u, 111u),
		 InlineData(1000u, 111u), InlineData(uint.MaxValue, 111u)]
		public void Sanitize_AppliesTheClientsOwnOverflowClamp(uint sent, uint expected)
		{
			Assert.Equal(expected, CombatShieldRose.Sanitize(sent));
		}

		[Fact]
		public void ApplyArcMitigation_LeavesAnUnrolledRoseAlone()
		{
			// An NPC that never engaged has no rose; a swing at it is unmitigated in every arc.
			Assert.Equal(100u, CombatShieldRose.ApplyArcMitigation(100, 0, ShieldArc.Mid));
		}

		// #621: an NPC's swing carries no direction ability, so the sector is geometric. Facing is a yaw-only
		// quaternion read as 2*atan2(qy, qw) with yaw measured from +Z toward +X (chardcmove-grammar.md), and the
		// renderer is left-handed Y-up, so +X of the defender's forward is the defender's RIGHT.
		[Theory]
		// Defender facing +Z (identity). An attacker due north is head-on; due south is behind.
		[InlineData(0f, 0f, 10f, CombatShieldRose.StruckSector.Mid)]
		[InlineData(0f, 0f, -10f, CombatShieldRose.StruckSector.Behind)]
		[InlineData(0f, 10f, 1f, CombatShieldRose.StruckSector.Right)]
		[InlineData(0f, -10f, 1f, CombatShieldRose.StruckSector.Left)]
		// The 30-degree boundary: 20 degrees off dead ahead is still Mid, 45 is not.
		[InlineData(0f, 3.64f, 10f, CombatShieldRose.StruckSector.Mid)]
		[InlineData(0f, 10f, 10f, CombatShieldRose.StruckSector.Right)]
		[InlineData(0f, -10f, 10f, CombatShieldRose.StruckSector.Left)]
		// Turn the defender 180 degrees and every sector flips with them.
		[InlineData(180f, 0f, 10f, CombatShieldRose.StruckSector.Behind)]
		[InlineData(180f, 0f, -10f, CombatShieldRose.StruckSector.Mid)]
		[InlineData(180f, 10f, -1f, CombatShieldRose.StruckSector.Left)]
		public void SectorFor_ReadsTheAttackersBearingAgainstTheDefendersFacing(float facingDegrees, float attackerX, float attackerZ,
			CombatShieldRose.StruckSector expected)
		{
			var yaw = float.DegreesToRadians(facingDegrees);
			var facing = new Quaternion(0f, MathF.Sin(yaw / 2f), 0f, MathF.Cos(yaw / 2f));

			Assert.Equal(expected, CombatShieldRose.SectorFor(new Vector3(attackerX, 0f, attackerZ), Vector3.Zero, facing));
		}

		// A strike in the back finds no guard, so it takes the zero-shield entry - the +20% the live game describes
		// as "there aren't any shields on the player's backside". Not a fourth column: the rose packs three digits.
		[Fact]
		public void DamageMultiplier_BehindTakesTheUnguardedBonusWhateverTheSpread()
		{
			foreach (var spread in new uint[] { 300, 111, 3, 30 })
				Assert.Equal(120u, CombatShieldRose.ApplyArcMitigation(100, spread, CombatShieldRose.StruckSector.Behind));
		}

		[Fact]
		public void DamageMultiplier_AFrontSectorUsesTheSameTableAsADirectedSwing()
		{
			// 300 = all three guards on Left: a hit from the defender's left is fully covered, one from the right is not.
			Assert.Equal(34u, CombatShieldRose.ApplyArcMitigation(100, 300, CombatShieldRose.StruckSector.Left));
			Assert.Equal(120u, CombatShieldRose.ApplyArcMitigation(100, 300, CombatShieldRose.StruckSector.Right));

			// ...and it agrees with the ability-driven overload arc for arc.
			foreach (var (sector, arc) in new[]
			         {
				         (CombatShieldRose.StruckSector.Left, ShieldArc.Left), (CombatShieldRose.StruckSector.Mid, ShieldArc.Mid),
				         (CombatShieldRose.StruckSector.Right, ShieldArc.Right)
			         })
				Assert.Equal(CombatShieldRose.DamageMultiplier(210, arc), CombatShieldRose.DamageMultiplier(210, sector));
		}

		[Fact]
		public void DamageMultiplier_AnUnrolledRoseIsUntouchedFromBehindToo()
		{
			// An NPC that never engaged has no rose at all, which is not the same as three bare arcs - so a back
			// attack on it earns nothing either.
			Assert.Equal(100u, CombatShieldRose.ApplyArcMitigation(100, 0, CombatShieldRose.StruckSector.Behind));
		}

		private static uint[] Arcs(uint value)
		{
			var (left, mid, right) = CombatShieldDistribution.Unpack(value);
			return [left, mid, right];
		}
	}
}
