using System.Numerics;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// A melee swing's reach. The target handle a swing carries is the client's, and the server is the only
	// thing that can bound it - the client range-checks a spell itself and never sends an out-of-range cast, but it
	// always sends the swing. `CombatTargeting.ResolveSwingReach` is that bound.
	//
	// The case these tests exist for is the instanced one. The first cut of the gate resolved the target through
	// `TryGetZone(MapId)`, which only ever finds the default instance, so on a Solo map it found nothing and fell
	// open - and it was tested on Tortage Island (4010), which is exactly such a map, so it shipped doing nothing
	// while looking correct in review. The session log that "proved" the check ran and measured a small distance
	// shows a swing landing on a mob standing untouched at its own spawn 21.3u away, which is the signature of a
	// guard that never resolved its target at all.
	public class MeleeReachTests
	{
		private const uint SoloMap = 4010; // Tortage Island - MapInstanceMode.Solo, the map the first cut was tested on
		private const uint PrivateInstance = 0x51;

		[Fact]
		public void TortageIsland_IsSolo_SoTheDefaultInstanceIsTheWrongPlaceToLook()
		{
			// The premise the instance bug rests on: if this map were Shared, `TryGetZone(MapId)` would have worked and
			// the first cut would have bitten. Pinned so a content change cannot quietly invalidate the reasoning.
			using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var map = db.Maps.Find(SoloMap);
			Assert.NotNull(map);
			Assert.Equal(MapInstanceMode.Solo, map.InstanceMode);
		}

		[Fact]
		public void OutOfReach_InAPrivateInstance_IsRefused()
		{
			var world = new WorldState();
			var attacker = Place(world, 1, true, 0, 0);
			Place(world, 2, false, 0, 21.3f); // the log's mob: at its own spawn, 21.3u out, never moved

			Assert.Equal(MeleeReach.TooFar, CombatTargeting.ResolveSwingReach(world, attacker, 2, out var distance));
			Assert.Equal(21.3f, distance, 3);
		}

		[Fact]
		public void InReach_InAPrivateInstance_IsAllowed()
		{
			var world = new WorldState();
			var attacker = Place(world, 1, true, 0, 0);
			Place(world, 2, false, 0, CombatTargeting.SwingReach - 0.5f);

			Assert.Equal(MeleeReach.InReach, CombatTargeting.ResolveSwingReach(world, attacker, 2, out _));
		}

		[Fact]
		public void ExactlyAtReach_IsAllowed()
		{
			var world = new WorldState();
			var attacker = Place(world, 1, true, 0, 0);
			Place(world, 2, false, 0, CombatTargeting.SwingReach);

			Assert.Equal(MeleeReach.InReach, CombatTargeting.ResolveSwingReach(world, attacker, 2, out _));
		}

		// The reach is measured in three dimensions, so a mob on the ledge directly above you is as unreachable as one
		// the same distance away on the flat. (The cleave cone ignores height on purpose - that is a compass arc - but
		// the distance a blade travels is not a compass question.)
		[Fact]
		public void HeightCountsTowardTheDistance()
		{
			var world = new WorldState();
			var attacker = Place(world, 1, true, 0, 0);
			var target = Place(world, 2, false, 0, 0);
			target.Position = new Vector3(0, CombatTargeting.SwingReach + 5f, 0);

			Assert.Equal(MeleeReach.TooFar, CombatTargeting.ResolveSwingReach(world, attacker, 2, out _));
		}

		// A target in another copy of the same map is not reachable - the instancing split is what stops one player's
		// swing landing in someone else's private Tortage.
		[Fact]
		public void TargetInAnotherInstanceOfTheSameMap_IsNotInZone()
		{
			var world = new WorldState();
			var attacker = Place(world, 1, true, 0, 0);
			var other = WorldEntity.ForNpc(2, SoloMap, Vector3.Zero, Quaternion.Identity, "Mob", EntityVitals.FromMax(100, 0, 100));
			world.Place(other, SoloMap, PrivateInstance + 1, Vector3.Zero, Quaternion.Identity);

			Assert.Equal(MeleeReach.TargetNotInZone, CombatTargeting.ResolveSwingReach(world, attacker, 2, out _));
		}

		[Fact]
		public void UnknownHandle_IsNotInZone()
		{
			var world = new WorldState();
			var attacker = Place(world, 1, true, 0, 0);

			Assert.Equal(MeleeReach.TargetNotInZone, CombatTargeting.ResolveSwingReach(world, attacker, 0xDEAD, out _));
		}

		// The swing and its cleave splash are the same physical reach, so they must stay one number: two would let a
		// swing land on a primary the splash could not have touched.
		[Fact]
		public void SwingAndCleaveShareOneReach()
		{
			Assert.Equal(CombatTargeting.SwingReach, CombatTargeting.DefaultCleaveReach);
		}

		private static WorldEntity Place(WorldState world, uint handle, bool isPlayer, float x, float z)
		{
			var entity = isPlayer
				? new WorldEntity(handle, SoloMap, true, "Attacker", null!)
				: WorldEntity.ForNpc(handle, SoloMap, Vector3.Zero, Quaternion.Identity, "Mob", EntityVitals.FromMax(100, 0, 100));
			world.Place(entity, SoloMap, PrivateInstance, new Vector3(x, 0, z), Quaternion.Identity);
			return entity;
		}
	}
}
