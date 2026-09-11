using System.Numerics;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// The leash measures against the entity's territory - its spawn plus its route - so a far wander anchor is a
	// destination, not a drag, while an NPC dragged off the whole route still leashes. Measured against the spawn
	// alone, an anchor beyond NpcLeashRange reads as "led too far from home" on the very next tick and walks the NPC
	// straight back off its own route, cycle after cycle (331 of 900 captured routes have such an anchor).
	public class PatrolLeashTests
	{
		[Fact]
		public void Patroller_DraggedOffItsWholeRoute_StillLeashes()
		{
			// The leash's real job: a player who drags a patroller away from spawn and every anchor is still refused.
			var npc = Patroller(new Vector3(0, 0, 0), [new Vector3(0, 0, 0), new Vector3(200, 0, 0)]);
			npc.Position = new Vector3(0, 0, 500); // far from spawn and from both anchors

			Assert.True(npc.Route.BeyondLeash(npc.Position, 500f, WorldState.NpcLeashRange));
		}

		[Fact]
		public void Patroller_OnAFarAnchor_IsNotLeashedHome()
		{
			// The Casilda case: standing on a route anchor 200u from spawn is exactly where the wander put it.
			var npc = Patroller(new Vector3(0, 0, 0), [new Vector3(0, 0, 0), new Vector3(200, 0, 0)]);
			npc.Position = new Vector3(200, 0, 0);

			Assert.False(npc.Route.BeyondLeash(npc.Position, 200f, WorldState.NpcLeashRange));
		}

		[Fact]
		public void ReturningNpc_WalksHomeAndLandsAtItsSpawnAltitude()
		{
			// A genuine stray (no route) walks home. Its Y is whatever height it was led to, so the walk must close the
			// vertical gap too - otherwise it arrives hovering 60u above its spawn.
			var world = new WorldState();
			var npc = WorldEntity.ForNpc(10, 2040, new Vector3(0, 100, 0), Quaternion.Identity, "Mob", EntityVitals.FromMax(100, 0, 100));
			world.Place(npc, 2040, npc.Position, Quaternion.Identity);
			npc.Position = new Vector3(60, 160, 0); // dragged 60u out and 60u up

			var previousY = npc.Position.Y;
			for (var i = 0; i < 300; i++) // 60u at NpcMoveSpeed (7 u/s) needs ~86 ticks; the rest idles at home
			{
				world.Tick(0.1);
				Assert.True(npc.Position.Y <= previousY, "the walk home descends - it never climbs away from the spawn altitude");
				previousY = npc.Position.Y;
			}

			Assert.False(npc.Route.Returning); // arrived
			Assert.Equal(100f, npc.Position.Y, 3); // exactly the spawn's altitude, not a residual float
			Assert.True(Vector3.Distance(npc.Position, npc.Route.SpawnPosition) <= WorldState.NpcMeleeRange + 0.5f);
		}

		[Fact]
		public void StationaryNpc_BeyondLeash_StillReturnsHome()
		{
			// No route: territory is the spawn alone, so the leash measures from the spawn.
			var world = new WorldState();
			var npc = WorldEntity.ForNpc(10, 2040, Vector3.Zero, Quaternion.Identity, "Mob", EntityVitals.FromMax(100, 0, 100));
			world.Place(npc, 2040, Vector3.Zero, Quaternion.Identity);
			npc.Position = new Vector3(100, 0, 0);

			world.Tick(0.1);

			Assert.True(npc.Route.Returning);
		}

		[Fact]
		public void WanderingPatroller_IsNeverSetReturning_ByItsOwnAnchor()
		{
			// End to end through the tick: park the NPC on its far anchor and run a stretch of ticks. A spawn-only leash
			// flips Returning on the first tick and starts walking it back off its route.
			var world = new WorldState();
			var npc = WorldEntity.ForNpc(10, 2040, new Vector3(0, 0, 0), Quaternion.Identity, "Wanderer", EntityVitals.FromMax(100, 0, 100));
			world.Place(npc, 2040, npc.Position, Quaternion.Identity);
			npc.Route.PatrolWaypoints = [new Vector3(0, 0, 0), new Vector3(150, 0, 0), new Vector3(300, 0, 0)];
			npc.Position = new Vector3(300, 0, 0);

			for (var i = 0; i < 50; i++)
			{
				world.Tick(0.1);
				Assert.False(npc.Route.Returning, "a patroller standing on its own route is not a stray");
			}
		}

		private static WorldEntity Patroller(Vector3 spawn, Vector3[] waypoints)
		{
			var npc = WorldEntity.ForNpc(1, 2040, spawn, Quaternion.Identity, "Patroller", EntityVitals.FromMax(100, 0, 100));
			npc.Route.PatrolWaypoints = waypoints;
			return npc;
		}
	}
}
