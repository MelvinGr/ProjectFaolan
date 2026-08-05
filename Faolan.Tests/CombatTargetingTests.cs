using System;
using System.Linq;
using System.Numerics;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// Melee cleave target selection (#18): a swing damages "a small area in front of you" (Funcom's quickstart), so
	// the extras are the hostile, living NPCs inside the ATTACKER's frontal cone - not a ball around the selected
	// target, which is what this used to model. CombatTargeting.CleaveTargets is the pure selection over the world
	// model. The attacker below faces +Z (identity rotation) unless a test turns them.
	public class CombatTargetingTests
	{
		private const float Reach = 8f;
		private const float HalfAngle = 60f;

		[Fact]
		public void CapsToMax()
		{
			var zone = new Zone(4010);
			var attacker = Player(100, 0, 0);
			var primary = NpcTemplate(1, 0, 4);
			zone.Add(attacker);
			zone.Add(primary);
			for (uint h = 2; h <= 9; h++)
				zone.Add(NpcTemplate(h, 1, 5));

			Assert.Equal(3, CombatTargeting.CleaveTargets(zone, attacker, primary, Reach, HalfAngle, 3).Count);
		}

		[Fact]
		public void ExcludesPlayersNonHostileAndDeadNpcs()
		{
			var zone = new Zone(4010);
			var attacker = Player(100, 0, 0);
			var primary = NpcTemplate(1, 0, 4);
			var otherPlayer = new WorldEntity(2, 4010, true, "P", null!)
			{
				Position = new Vector3(0, 0, 2)
			};
			var questGiver = NpcTemplate(3, 0, 2, false);
			var corpse = NpcTemplate(4, 1, 2);
			corpse.Vitals.ApplyHealthDelta(-100); // dead
			Assert.True(corpse.IsDead);
			foreach (var e in new[] { attacker, primary, otherPlayer, questGiver, corpse })
				zone.Add(e);

			Assert.Empty(CombatTargeting.CleaveTargets(zone, attacker, primary, Reach, HalfAngle, 4));
		}

		[Fact]
		public void HitsHostileNpcsInFront_NearestFirst_ExcludesPrimary()
		{
			var zone = new Zone(4010);
			var attacker = Player(100, 0, 0);
			var primary = NpcTemplate(1, 0, 4);
			var near = NpcTemplate(2, 1, 2);
			var farInReach = NpcTemplate(3, 2, 6);
			var outOfReach = NpcTemplate(4, 0, 50);
			foreach (var e in new[] { attacker, primary, near, farInReach, outOfReach })
				zone.Add(e);

			var targets = CombatTargeting.CleaveTargets(zone, attacker, primary, Reach, HalfAngle, 4);

			Assert.Equal(new uint[] { 2, 3 }, targets.Select(t => t.Handle).ToArray()); // nearest-first, no primary, no #4
		}

		// The whole point of the change: a mob BEHIND the attacker is no longer cleaved just for standing near the
		// selected target, and a mob beside the attacker but well away from the target now is.
		[Fact]
		public void TheConeIsAroundTheATTACKER_NotAroundTheTarget()
		{
			var zone = new Zone(4010);
			var attacker = Player(100, 0, 0); // facing +Z
			var primary = NpcTemplate(1, 0, 5);
			var behindTheAttacker = NpcTemplate(2, 0, -3); // 8u from the primary, but at the attacker's back
			var besideTheAttacker = NpcTemplate(3, 3, 3); // 45 degrees off the swing, far from the primary
			foreach (var e in new[] { attacker, primary, behindTheAttacker, besideTheAttacker })
				zone.Add(e);

			var targets = CombatTargeting.CleaveTargets(zone, attacker, primary, Reach, HalfAngle, 4);

			Assert.Equal(new uint[] { 3 }, targets.Select(t => t.Handle).ToArray());
		}

		// Turning the attacker turns the swing with them - the facing is read off the entity's rotation, not assumed.
		[Fact]
		public void TurningTheAttackerTurnsTheCone()
		{
			var zone = new Zone(4010);
			var attacker = Player(100, 0, 0);
			var eastOfTheAttacker = NpcTemplate(2, 4, 0);
			zone.Add(attacker);
			zone.Add(eastOfTheAttacker);

			Assert.Empty(CombatTargeting.CleaveTargets(zone, attacker, null, Reach, HalfAngle, 4));

			attacker.Rotation = Yaw(90f); // now facing +X, straight at it
			Assert.Equal(new uint[] { 2 }, CombatTargeting.CleaveTargets(zone, attacker, null, Reach, HalfAngle, 4).Select(t => t.Handle).ToArray());
		}

		[Theory]
		// Facing +Z: dead ahead and the cone edges are in, past them is out.
		[InlineData(0f, 0f, 5f, true)]
		[InlineData(0f, 5f, 5f, true)] // 45 degrees
		[InlineData(0f, 5f, 1f, false)] // ~79 degrees
		[InlineData(0f, 5f, 0f, false)] // straight out to the side
		[InlineData(0f, 0f, -5f, false)] // behind
		// ...and it all rotates with the facing.
		[InlineData(180f, 0f, -5f, true)]
		[InlineData(180f, 0f, 5f, false)]
		public void IsInFrontOf_IsTheConeAroundTheFacing(float facingDegrees, float x, float z, bool expected)
		{
			var rotation = Yaw(facingDegrees);
			var facing = 2.0 * Math.Atan2(rotation.Y, rotation.W);
			Assert.Equal(expected, CombatTargeting.IsInFrontOf(Vector3.Zero, facing, new Vector3(x, 0, z), HalfAngle));
		}

		private static Quaternion Yaw(float degrees)
		{
			var yaw = float.DegreesToRadians(degrees);
			return new Quaternion(0f, MathF.Sin(yaw / 2f), 0f, MathF.Cos(yaw / 2f));
		}

		private static WorldEntity Player(uint handle, float x, float z)
		{
			return new WorldEntity(handle, 4010, true, "Attacker", null!)
			{
				Position = new Vector3(x, 0, z),
				Rotation = Quaternion.Identity // facing +Z
			};
		}

		private static WorldEntity NpcTemplate(uint handle, float x, float z, bool hostile = true)
		{
			var e = WorldEntity.ForNpc(handle, 4010, new Vector3(x, 0, z), Quaternion.Identity, "Mob", EntityVitals.FromMax(100, 0, 100));
			e.Hostile = hostile;
			return e;
		}
	}
}
