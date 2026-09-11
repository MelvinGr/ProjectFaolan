using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.GameServer.Combat;
using Xunit;

namespace Faolan.Tests
{
	// Directional combat - the defender's shield rose. Every expectation here is a measurement, so the class
	// doubles as the record of the decode:
	//   * the 10 packed distributions and the "one shield moves" transition shape come from the 110 CombatShieldIIR
	//     records / 39 transitions in the capture corpus;
	//   * the mitigation table comes from the client's own balance sheet (RDB 1000516 `Combat_PvE{0..3}ShieldBonus`
	//     0.2/0/-0.33/-0.66 and `Combat_PvP{0..3}ShieldBonus` 0.2/0/-0.15/-0.30), which the client registers and never
	//     reads - so they are the server's numbers;
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
		// the flat behaviour every non-directional path gets.
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

		// A player's rose is the only one that arrives from outside, so it is the only one that needs the client's
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

		// An NPC's swing carries no direction ability, so the server picks the zone it swings from. It is a
		// direction of the swing, not of the attacker's position - so the first thing to gate is that it reaches all
		// five zones retail's mobs use (a stuck selector would silently make one guard the only one that matters).
		[Fact]
		public void RollSwingDirection_ReachesAllFiveOfTheClientsZones()
		{
			var random = new Random(20260805);
			var seen = new HashSet<SwingDirection>();
			for (var i = 0; i < 500; i++)
				seen.Add(CombatShieldRose.RollSwingDirection(random));

			Assert.Equal(Enum.GetValues<SwingDirection>().ToHashSet(), seen);
		}

		// The measurement the five-zone model rests on: collapsed onto the three guard arcs, the corpus's 2,709
		// NPC-actor swings are uniform (918/896/895, chi-square 0.37 on 2 df). So the arc draw must stay uniform even
		// though the zone draw is not - a model fitted to the raw five-way counts would bias the mitigation, which is
		// the only thing the arc is spent on.
		[Fact]
		public void RollSwingDirection_IsUniformOverTheThreeGuardArcs()
		{
			var random = new Random(20260805);
			var counts = new Dictionary<ShieldArc, int> { [ShieldArc.Left] = 0, [ShieldArc.Mid] = 0, [ShieldArc.Right] = 0 };
			const int rolls = 60000;
			for (var i = 0; i < rolls; i++)
				counts[CombatShieldRose.ArcOf(CombatShieldRose.RollSwingDirection(random))]++;

			foreach (var (_, n) in counts)
				Assert.InRange(n / (double)rolls, 0.32, 0.347);
		}

		// The height axis is biased, and measured: within the two side arcs the corpus is 1,056 upper / 757 lower.
		// Uniform-over-five would put 40% of every side arc's swings in each of upper and lower, so a wide band still
		// separates the model that was fitted from the one that was not.
		[Fact]
		public void RollSwingDirection_LeansUpperWithinASideArc()
		{
			var random = new Random(20260805);
			int upper = 0, lower = 0;
			for (var i = 0; i < 60000; i++)
			{
				switch (CombatShieldRose.RollSwingDirection(random))
				{
					case SwingDirection.UpperLeft or SwingDirection.UpperRight:
					{
						upper++;
						break;
					}

					case SwingDirection.LowerLeft or SwingDirection.LowerRight:
					{
						lower++;
						break;
					}
				}
			}

			Assert.InRange(upper / (double)(upper + lower), 0.57, 0.595);
		}

		// The client's own collapse (`FUN_005edd90` folds Lower Left -> Upper Left and Lower Right -> Upper Right):
		// a lower zone is guarded by the arc on its own side, which is what lets three shields cover five zones.
		[Theory]
		[InlineData(SwingDirection.UpperLeft, ShieldArc.Left)]
		[InlineData(SwingDirection.LowerLeft, ShieldArc.Left)]
		[InlineData(SwingDirection.UpperRight, ShieldArc.Right)]
		[InlineData(SwingDirection.LowerRight, ShieldArc.Right)]
		[InlineData(SwingDirection.Overhead, ShieldArc.Mid)]
		public void ArcOf_FoldsEachLowerZoneOntoItsOwnSide(SwingDirection direction, ShieldArc expected)
		{
			Assert.Equal(expected, CombatShieldRose.ArcOf(direction));
		}

		// The ability id -> zone mapping, in the client's enum order. `ArcFor` is this composed with `ArcOf`, so this
		// is what pins the two lower abilities that no captured player swing ever carries.
		[Theory]
		[InlineData(769094u, SwingDirection.UpperLeft)]
		[InlineData(769095u, SwingDirection.UpperRight)]
		[InlineData(769099u, SwingDirection.Overhead)]
		[InlineData(769096u, SwingDirection.LowerLeft)]
		[InlineData(769097u, SwingDirection.LowerRight)]
		public void DirectionFor_MapsTheFiveDirectionAbilities(uint abilityId, SwingDirection expected)
		{
			Assert.Equal(expected, CombatShieldRose.DirectionFor(abilityId));
		}

		// The mob's swing goes through the same table as a player's directed swing - a mob hitting a guard the player
		// stacked is mitigated, one hitting a bare arc is not. The player defender takes the gentler PvP column.
		[Fact]
		public void ApplyArcMitigation_AMobsSwingIsMitigatedByTheArcThePlayerStacked()
		{
			// 300 = all three guards on Left.
			Assert.Equal(70u, CombatShieldRose.ApplyArcMitigation(100, 300, ShieldArc.Left, playerVersusPlayer: true));
			Assert.Equal(120u, CombatShieldRose.ApplyArcMitigation(100, 300, ShieldArc.Right, playerVersusPlayer: true));

			// The default 111 spread is one guard everywhere, which is the table's neutral entry - "leave them alone
			// in PvE" costs the player nothing.
			foreach (var arc in new[] { ShieldArc.Left, ShieldArc.Mid, ShieldArc.Right })
				Assert.Equal(100u, CombatShieldRose.ApplyArcMitigation(100, 111, arc, playerVersusPlayer: true));
		}

		// The zone the mitigation charges for is the zone the ack states, and the mapping is the client's own -
		// `FUN_005e3b30` is the same switch over the same 1..5 order. The first three are pinned to the `Abilities`
		// rows `Upper Left`/`Upper Right`/`Overhead Attack` by the corpus with zero exceptions, and the two lower ones
		// by the combo parser's `0xCC -> 0xC9` / `0xCD -> 0xCA` fold. So this asserts a decoded correspondence, not a
		// convention.
		[Theory]
		[InlineData(SwingDirection.UpperLeft, (byte)0xC9)]
		[InlineData(SwingDirection.UpperRight, (byte)0xCA)]
		[InlineData(SwingDirection.Overhead, (byte)0xCB)]
		[InlineData(SwingDirection.LowerLeft, (byte)0xCC)]
		[InlineData(SwingDirection.LowerRight, (byte)0xCD)]
		public void SwingDirectionCode_IsTheClientsOwnDirectionCode(SwingDirection direction, byte expected)
		{
			Assert.Equal(expected, BehaviorAcks.SwingDirectionCode(direction));
		}

		// Every zone maps, and to a distinct code - a mapping that collapsed two zones onto one would state the wrong
		// direction on the wire for one of them and never fail an equality test written zone-by-zone.
		[Fact]
		public void SwingDirectionCode_IsOneToOneOverTheWholeEnum()
		{
			var codes = Enum.GetValues<SwingDirection>().Select(BehaviorAcks.SwingDirectionCode).ToList();

			Assert.Equal(codes.Count, codes.Distinct().Count());
		}

		// The wire alphabet has exactly the five zones the client ships and nothing more - a sixth member would be a
		// code retail never sends.
		[Fact]
		public void SwingDirectionCode_CoversTheFiveCodeAlphabetAndNoOther()
		{
			Assert.Equal<byte>([0xC9, 0xCA, 0xCB, 0xCC, 0xCD], Enum.GetValues<SwingDirection>().Select(BehaviorAcks.SwingDirectionCode).Order().ToArray());
		}

		private static uint[] Arcs(uint value)
		{
			var (left, mid, right) = CombatShieldDistribution.Unpack(value);
			return [left, mid, right];
		}
	}
}
