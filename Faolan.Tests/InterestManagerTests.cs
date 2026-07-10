using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// Interest management (docs/server-architecture-roadmap.md): as a player moves, compute the spawn/move/despawn
	// events so each player's view stays correct - the logic that makes two players see each other.
	public class InterestManagerTests
	{
		private const float Range = 50f;

		[Fact]
		public void KnownBurstNpc_WithIncludeNonPlayers_NotReSpawned()
		{
			var zone = new Zone(2040);
			var a = Player(1, 0, 0);
			var npc = WorldEntity.ForNpc(99, 2040, new Vector3(5, 0, 0), Quaternion.Identity, "Kalanthes"); // right next to A
			zone.Add(a);
			zone.Add(npc);
			a.Known.Add(99); // entry seeded this: the burst already rendered it

			a.Position = new Vector3(1, 0, 0);
			var events = new WorldEvents(InterestManager.ResolveMove(zone, a, Range, true));

			Assert.False(events.Has(1, 99, InterestEventType.Spawn)); // already known -> not doubled
		}

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
			var npc = WorldEntity.ForNpc(99, 2040, new Vector3(5, 0, 0), Quaternion.Identity, "Mob"); // right next to A
			zone.Add(a);
			zone.Add(npc);

			var (events, _) = MoveTo(zone, a, 1, 0);

			Assert.False(events.Has(1, 99, InterestEventType.Spawn)); // NPC comes from the burst, not interest (yet)
			Assert.DoesNotContain(99u, a.Known);
		}

		[Fact]
		public void MovingNearNpc_WithIncludeNonPlayers_StreamsTheNpc()
		{
			var zone = new Zone(2040);
			var a = Player(1, 0, 0);
			var npc = WorldEntity.ForNpc(99, 2040, new Vector3(1000, 0, 0), Quaternion.Identity, "Casilda"); // far
			zone.Add(a);
			zone.Add(npc);

			a.Position = new Vector3(990, 0, 0); // A walks up to the NPC
			var events = new WorldEvents(InterestManager.ResolveMove(zone, a, Range, true));

			Assert.True(events.Has(1, 99, InterestEventType.Spawn)); // NPC now streamed from the model
			Assert.Contains(99u, a.Known);
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
			a.Confirmed.Add(2);
			b.Confirmed.Add(1); // both clients polled (0x2020) -> built each other; a later despawn is now safe (#245)

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
			b.Confirmed.Add(1); // B polled (0x2020) -> built A, so a later despawn is safe (#245/#322 gate)

			var events = new WorldEvents(InterestManager.ResolveRemove(zone, a));

			Assert.True(events.Has(2, 1, InterestEventType.Despawn)); // B told A is gone
			Assert.DoesNotContain(1u, b.Known);
		}

		[Fact]
		public void Remove_UnconfirmedViewer_DoesNotDespawn()
		{
			// #322: A leaver disconnecting must NOT send an n3ToClientQuit to a viewer that has it in Known but never
			// CONFIRMED building it (still loading) - the same crash the #245 gate prevents on the range-loss path.
			// The leaver is dropped from that viewer's Known silently.
			var zone = new Zone(2040);
			var a = Player(1, 0, 0);
			var b = Player(2, 10, 0);
			zone.Add(a);
			zone.Add(b);
			InterestManager.ResolveMove(zone, a, Range); // a + b mutually Known, but B never polled (Confirmed empty)

			var events = new WorldEvents(InterestManager.ResolveRemove(zone, a));

			Assert.False(events.Has(2, 1, InterestEventType.Despawn)); // no despawn for the unconfirmed handle
			Assert.DoesNotContain(1u, b.Known); // but A is dropped from B's Known
		}

		[Fact]
		public void UnconfirmedHandleLeavingRange_DoesNotDespawn()
		{
			// #245: an entity spawned to a still-loading client (Known) but not yet CONFIRMED (no 0x2020 poll) must NOT
			// get an n3ToClientQuit when it briefly leaves range during the load-settle churn - that despawn crashes the
			// client (pcDynel == null). It is dropped from Known silently instead.
			var zone = new Zone(2040);
			var a = Player(1, 0, 0);
			var b = Player(2, 10, 0);
			zone.Add(a);
			zone.Add(b);
			InterestManager.ResolveMove(zone, a, Range); // a + b mutually Known, but neither has polled (Confirmed empty)

			var (events, _) = MoveTo(zone, a, 5000, 0); // a runs far away

			Assert.False(events.Has(1, 2, InterestEventType.Despawn)); // no despawn for the unconfirmed handle
			Assert.DoesNotContain(2u, a.Known); // but it's dropped from Known
		}

		[Fact]
		public void Entry_ViewerWithStaleKnown_SpawnsNotMove()
		{
			// #323: a re-entering player whose handle is still in a viewer's Known (stale - a prior map-change/re-enter
			// that never clean-removed it) must be SPAWNED to that viewer at entry, not forwarded as a Move (entry
			// dispatches move=null, which drops Moves -> the re-entrant would stay invisible). isEntry forces the Spawn.
			var zone = new Zone(2040);
			var a = Player(1, 0, 0);
			var b = Player(2, 10, 0);
			zone.Add(a);
			zone.Add(b);
			b.Known.Add(1); // stale: B still "knows" A from a prior session
			b.Confirmed.Add(1);

			var events = new WorldEvents(InterestManager.ResolveMove(zone, a, Range, true, isEntry: true));

			Assert.True(events.Has(2, 1, InterestEventType.Spawn)); // A re-spawned to B
			Assert.False(events.Has(2, 1, InterestEventType.Move)); // NOT a (dropped) Move
			Assert.DoesNotContain(1u, b.Confirmed); // stale build forgotten -> B must re-poll the fresh spawn
		}

		private static (WorldEvents events, Zone zone) MoveTo(Zone zone, WorldEntity mover, float x, float z)
		{
			mover.Position = new Vector3(x, 0, z);
			return (new WorldEvents(InterestManager.ResolveMove(zone, mover, Range)), zone);
		}

		private static WorldEntity Player(uint handle, float x, float z)
		{
			return new WorldEntity(handle, 2040, true, $"P{handle}", null)
			{
				Position = new Vector3(x, 0, z)
			};
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
