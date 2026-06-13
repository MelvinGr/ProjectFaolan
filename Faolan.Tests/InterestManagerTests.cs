using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// Interest management (docs/archive/server-architecture-roadmap.md): as a player moves, compute the spawn/move/despawn
	// events so each player's view stays correct - the logic that makes two players see each other.
	public class InterestManagerTests
	{
		private const float Range = 50f;

		[Fact]
		public void MovingIntoRange_SpawnsBothWays()
		{
			var zone = new Zone(2040);
			var a = Player(1, 0, 0);
			var b = Player(2, 1000, 0); // far
			zone.Add(a);
			zone.Add(b);

			// A walks up next to B.
			var (events, _) = MoveTo(zone, a, 990, 0);

			Assert.True(events.Has(1, 2, InterestEventType.Spawn)); // A now sees B
			Assert.True(events.Has(2, 1, InterestEventType.Spawn)); // B now sees A
			Assert.True(a.Known.Contains(2) && b.Known.Contains(1));
		}

		[Fact]
		public void MovingNearNpc_DoesNotSpawnTheNpc_StillBurstRendered()
		{
			var zone = new Zone(2040);
			var a = Player(1, 0, 0);
			var npc = WorldEntity.ForNpc(99, 2040, new Vector3(5, 0, 0), Vector3.Zero, "Mob"); // right next to A
			zone.Add(a);
			zone.Add(npc);

			var (events, _) = MoveTo(zone, a, 1, 0);

			Assert.False(events.Has(1, 99, InterestEventType.Spawn)); // NPC comes from the burst, not interest (yet)
			Assert.False(a.Known.Contains(99));
		}

		[Fact]
		public void MovingNearNpc_WithIncludeNonPlayers_StreamsTheNpc()
		{
			var zone = new Zone(2040);
			var a = Player(1, 0, 0);
			var npc = WorldEntity.ForNpc(99, 2040, new Vector3(1000, 0, 0), Vector3.Zero, "Casilda"); // far
			zone.Add(a);
			zone.Add(npc);

			a.Position = new Vector3(990, 0, 0); // A walks up to the NPC
			var events = new WorldEvents(InterestManager.ResolveMove(zone, a, Range, includeNonPlayers: true));

			Assert.True(events.Has(1, 99, InterestEventType.Spawn)); // NPC now streamed from the model
			Assert.True(a.Known.Contains(99));
		}

		[Fact]
		public void KnownBurstNpc_WithIncludeNonPlayers_NotReSpawned()
		{
			var zone = new Zone(2040);
			var a = Player(1, 0, 0);
			var npc = WorldEntity.ForNpc(99, 2040, new Vector3(5, 0, 0), Vector3.Zero, "Kalanthes"); // right next to A
			zone.Add(a);
			zone.Add(npc);
			a.Known.Add(99); // entry seeded this: the burst already rendered it

			a.Position = new Vector3(1, 0, 0);
			var events = new WorldEvents(InterestManager.ResolveMove(zone, a, Range, includeNonPlayers: true));

			Assert.False(events.Has(1, 99, InterestEventType.Spawn)); // already known -> not doubled
		}

		[Fact]
		public void MovingOutOfRange_Despawns()
		{
			var zone = new Zone(2040);
			var a = Player(1, 0, 0);
			var b = Player(2, 10, 0);
			zone.Add(a);
			zone.Add(b);
			InterestManager.ResolveMove(zone, a, Range); // mutually visible

			// A runs far away.
			var (events, _) = MoveTo(zone, a, 5000, 0);

			Assert.True(events.Has(1, 2, InterestEventType.Despawn)); // A no longer sees B
			Assert.True(events.Has(2, 1, InterestEventType.Despawn)); // B no longer sees A
			Assert.False(a.Known.Contains(2) || b.Known.Contains(1));
		}

		[Fact]
		public void MovingWhileKnown_ForwardsMove_NotSpawn()
		{
			var zone = new Zone(2040);
			var a = Player(1, 0, 0);
			var b = Player(2, 10, 0);
			zone.Add(a);
			zone.Add(b);
			InterestManager.ResolveMove(zone, a, Range); // establish mutual visibility

			// A takes a step (still near B).
			var (events, _) = MoveTo(zone, a, 12, 0);

			Assert.True(events.Has(2, 1, InterestEventType.Move)); // B sees A move (already known -> Move, not Spawn)
			Assert.False(events.Has(2, 1, InterestEventType.Spawn));
		}

		[Fact]
		public void Remove_DespawnsForEveryoneWhoKnew()
		{
			var zone = new Zone(2040);
			var a = Player(1, 0, 0);
			var b = Player(2, 10, 0);
			zone.Add(a);
			zone.Add(b);
			InterestManager.ResolveMove(zone, a, Range);

			var events = new WorldEvents(InterestManager.ResolveRemove(zone, a));

			Assert.True(events.Has(2, 1, InterestEventType.Despawn)); // B told A is gone
			Assert.False(b.Known.Contains(1));
		}

		private static (WorldEvents events, Zone zone) MoveTo(Zone zone, WorldEntity mover, float x, float z)
		{
			mover.Position = new Vector3(x, 0, z);
			return (new WorldEvents(InterestManager.ResolveMove(zone, mover, Range)), zone);
		}

		private static WorldEntity Player(uint handle, float x, float z)
		{
			return new WorldEntity(handle, 2040, true, $"P{handle}", null) { Position = new Vector3(x, 0, z) };
		}

		// tiny helper to query the returned event list
		private sealed class WorldEvents
		{
			private readonly List<InterestEvent> _e;

			public int Count => _e.Count;

			public WorldEvents(List<InterestEvent> e)
			{
				_e = e;
			}

			public bool Has(uint recipient, uint subject, InterestEventType t)
			{
				return _e.Any(ev => ev.Recipient.Handle == recipient && ev.Subject.Handle == subject && ev.Type == t);
			}
		}
	}
}
