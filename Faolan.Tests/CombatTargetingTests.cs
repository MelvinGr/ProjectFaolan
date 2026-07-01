using System.Linq;
using System.Numerics;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// Melee cleave target selection (#18): a swing on the selected target also hits the hostile, living NPCs clustered
	// around it (target-centered cleave). CombatTargeting.CleaveTargets is the pure selection over the world model.
	public class CombatTargetingTests
	{
		[Fact]
		public void CapsToMax()
		{
			var zone = new Zone(4010);
			var primary = Npc(1, 0, 0);
			zone.Add(primary);
			for (uint h = 2; h <= 9; h++)
				zone.Add(Npc(h, 1, 0));

			Assert.Equal(3, CombatTargeting.CleaveTargets(zone, primary, 5f, 3).Count);
		}

		[Fact]
		public void ExcludesPlayersNonHostileAndDeadNpcs()
		{
			var zone = new Zone(4010);
			var primary = Npc(1, 0, 0);
			var player = new WorldEntity(2, 4010, true, "P", null) { Position = new Vector3(2, 0, 0) };
			var questGiver = Npc(3, 2, 0, false);
			var corpse = Npc(4, 2, 0);
			corpse.Vitals.ApplyHealthDelta(-100); // dead
			Assert.True(corpse.IsDead);
			foreach (var e in new[]
			{
				primary,
				player,
				questGiver,
				corpse
			})
				zone.Add(e);

			Assert.Empty(CombatTargeting.CleaveTargets(zone, primary, 5f, 4));
		}

		[Fact]
		public void HitsHostileNpcsInRange_NearestFirst_ExcludesPrimary()
		{
			var zone = new Zone(4010);
			var primary = Npc(1, 0, 0);
			var near = Npc(2, 2, 0);
			var farInRange = Npc(3, 4, 0);
			var outOfRange = Npc(4, 50, 0);
			foreach (var e in new[]
			{
				primary,
				near,
				farInRange,
				outOfRange
			})
				zone.Add(e);

			var targets = CombatTargeting.CleaveTargets(zone, primary, 5f, 4);

			Assert.Equal(new uint[]
				{
					2,
					3
				},
				targets.Select(t => t.Handle).ToArray()); // nearest-first, no primary, no #4
		}

		private static WorldEntity Npc(uint handle, float x, float z, bool hostile = true)
		{
			var e = WorldEntity.ForNpc(handle, 4010, new Vector3(x, 0, z), Vector3.Zero, "Mob", EntityVitals.FromMax(100, 0, 100));
			e.Hostile = hostile;
			return e;
		}
	}
}
