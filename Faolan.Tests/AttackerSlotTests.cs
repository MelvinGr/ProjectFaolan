using System;
using System.Linq;
using System.Numerics;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// Five Tortage Pirates on one player settled 0.03u-0.46u apart - the combat chase walked every attacker to
	// the target's exact point, so N attackers solved for one sphere and stacked inside it.
	//
	// The ring geometry is retail's, measured by `--report melee-crowd` over 25,638 NPC-NPC pairs in 75 captures:
	// divide each crowd's closest walking pair by `2*R*sin(pi/N)` and the implied radius is flat across crowd sizes
	// (1.05, 1.03, 1.06, 0.96, 1.08 for four to eight mobs), which is what says "ring" rather than "packed in".
	public class AttackerSlotTests
	{
		private const uint TargetHandle = 0x01316e68;

		[Theory]
		[InlineData(2)]
		[InlineData(3)]
		[InlineData(4)]
		[InlineData(5)]
		[InlineData(6)]
		[InlineData(7)]
		[InlineData(8)]
		// The property the bug violated, over every crowd size retail's own data covers. Handles are allocated
		// sequentially, so consecutive spawns are the case that has to work - the five pirates were exactly that, and
		// it is the input an ordinary scrambling hash handles worst (the first attempt put two of five 0.42u apart).
		//
		// 0.5u is retail's own line: only 0.9% of its walking crowd pairs are inside it, against 100% of the reported
		// pile-on (0.03u-0.46u across all ten pairs).
		public void AttackersOnOnePlayer_NeverStackInsideRetailsOverlapThreshold(int count)
		{
			var slots = Attackers(count).Select(a => Slot(a)).ToList();

			var gaps = (from i in Enumerable.Range(0, slots.Count)
				from j in Enumerable.Range(i + 1, slots.Count - i - 1)
				select Vector2.Distance(slots[i], slots[j])).ToList();

			Assert.DoesNotContain(gaps, g => g < 0.5f);
		}

		[Theory]
		// ...and it does not merely clear the floor, it reproduces retail's spacing. Left: the closest pair the golden
		// -angle ring produces. Right: the median closest walking pair `--report melee-crowd` measured at that crowd
		// size. Tolerance is loose on purpose - the retail figure is a median over a noisy population, so this gates
		// the right order, not a coincidence of decimals.
		[InlineData(3, 2.11f)]
		[InlineData(4, 1.49f)]
		[InlineData(5, 1.21f)]
		[InlineData(6, 1.06f)]
		[InlineData(7, 0.83f)]
		public void TheClosestPair_MatchesWhatRetailDoesAtThatCrowdSize(int count, float retailMedian)
		{
			var slots = Attackers(count).Select(a => Slot(a)).ToList();
			var closest = (from i in Enumerable.Range(0, slots.Count)
				from j in Enumerable.Range(i + 1, slots.Count - i - 1)
				select Vector2.Distance(slots[i], slots[j])).Min();

			Assert.InRange(closest, retailMedian - 0.5f, retailMedian + 0.9f);
		}

		[Fact]
		// Every slot is on the ring - that is what makes the arrangement retail's shape rather than a random scatter.
		public void EverySlotSitsOnTheMeasuredRing()
		{
			foreach (var slot in Attackers(24).Select(a => Slot(a)))
				Assert.Equal(WorldState.AttackerSlotRadius, slot.Length(), 2);
		}

		[Fact]
		// Stable across ticks: the slot is a pure function of the two handles, so an attacker does not re-solve for a
		// new angle every tick. That is the jitter `FollowFormationOffset` was written to avoid.
		public void ASlotIsStableForTheSamePairing()
		{
			var attacker = Npc(0x00000101);
			var target = Player(TargetHandle);

			Assert.Equal(WorldState.AttackerSlotOffset(attacker, target), WorldState.AttackerSlotOffset(attacker, target));
		}

		[Fact]
		// ...and it depends on the target too, so one creature does not take the same angle against everything it ever
		// fights - which a handle-only hash would do.
		public void TheSameAttackerTakesADifferentSlotAgainstADifferentTarget()
		{
			var attacker = Npc(0x00000101);

			Assert.NotEqual(WorldState.AttackerSlotOffset(attacker, Player(TargetHandle)),
				WorldState.AttackerSlotOffset(attacker, Player(TargetHandle + 1)));
		}

		[Fact]
		// A slot is a ring position, not extra stop distance: an attacker standing on it is still inside melee range
		// of the target, so the spread cannot push anyone out of the fight.
		public void StandingOnASlot_LeavesTheAttackerInsideMeleeRange()
		{
			Assert.True(WorldState.AttackerSlotRadius < WorldState.NpcMeleeRange);
		}

		private static Vector2 Slot(WorldEntity attacker)
		{
			var (x, z) = WorldState.AttackerSlotOffset(attacker, Player(TargetHandle));
			return new Vector2(x, z);
		}

		// Consecutive handles - the allocation pattern a wave of spawns actually produces.
		private static WorldEntity[] Attackers(int count)
		{
			return Enumerable.Range(0, count).Select(i => Npc((uint)(0x00000101 + i))).ToArray();
		}

		private static WorldEntity Npc(uint handle)
		{
			return new WorldEntity(handle, 4010, false, "Tortage Pirate", null!);
		}

		private static WorldEntity Player(uint handle)
		{
			return new WorldEntity(handle, 4010, true, "Player", null!);
		}
	}
}
