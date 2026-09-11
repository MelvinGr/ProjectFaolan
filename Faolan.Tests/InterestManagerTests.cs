using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// Interest management: as a player moves, compute the spawn/move/despawn
	// events so each player's view stays correct - the logic that makes two players see each other.
	public class InterestManagerTests
	{
		private const float Range = 50f;

		[Fact]
		public void Entry_ViewerWithStaleKnown_SpawnsNotMove()
		{
			// A re-entering player whose handle is still in a viewer's Known (stale - a prior map-change/re-enter
			// that never clean-removed it) must be spawned to that viewer at entry, not forwarded as a Move (entry
			// dispatches move=null, which drops Moves -> the re-entrant would stay invisible). isEntry forces the Spawn.
			var zone = new Zone(2040);
			var a = Player(1, 0, 0);
			var b = Player(2, 10, 0);
			zone.Add(a);
			zone.Add(b);
			b.Knowledge.Known.Add(1); // stale: B still "knows" A from a prior session
			b.Knowledge.Confirmed.Add(1);

			var events = new WorldEvents(InterestManager.ResolveMove(zone, a, Range, true, true));

			Assert.True(events.Has(2, 1, InterestEventType.Spawn)); // A re-spawned to B
			Assert.False(events.Has(2, 1, InterestEventType.Move)); // Not a (dropped) Move
			Assert.DoesNotContain(1u, b.Knowledge.Confirmed); // stale build forgotten -> B must re-poll the fresh spawn
		}

		[Fact]
		public void KnownBurstNpc_WithIncludeNonPlayers_NotReSpawned()
		{
			var zone = new Zone(2040);
			var a = Player(1, 0, 0);
			var npc = WorldEntity.ForNpc(99, 2040, new Vector3(5, 0, 0), Quaternion.Identity, "Kalanthes"); // right next to A
			zone.Add(a);
			zone.Add(npc);
			a.Knowledge.Known.Add(99); // entry seeded this: the burst already rendered it

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
			Assert.True(a.Knowledge.Known.Contains(2) && b.Knowledge.Known.Contains(1));
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
			Assert.DoesNotContain(99u, a.Knowledge.Known);
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
			Assert.Contains(99u, a.Knowledge.Known);
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
			a.Knowledge.Confirmed.Add(2);
			b.Knowledge.Confirmed.Add(1); // both clients polled (0x2020) -> built each other; a later despawn is now safe

			// A runs far away.
			var (events, _) = MoveTo(zone, a, 5000, 0);

			Assert.True(events.Has(1, 2, InterestEventType.Despawn)); // A no longer sees B
			Assert.True(events.Has(2, 1, InterestEventType.Despawn)); // B no longer sees A
			Assert.False(a.Knowledge.Known.Contains(2) || b.Knowledge.Known.Contains(1));
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
			b.Knowledge.Confirmed.Add(1); // B polled (0x2020) -> built A, so a later despawn is safe (#245/#322 gate)

			var events = new WorldEvents(InterestManager.ResolveRemove(zone, a));

			Assert.True(events.Has(2, 1, InterestEventType.Despawn)); // B told A is gone
			Assert.DoesNotContain(1u, b.Knowledge.Known);
		}

		[Fact]
		public void Remove_UnconfirmedViewer_DoesNotDespawn()
		{
			// A leaver disconnecting must not send an n3ToClientQuit to a viewer that has it in Known but never
			// confirmed building it (still loading) - the same crash the confirmed-build gate prevents on range loss.
			// The leaver is dropped from that viewer's Known silently.
			var zone = new Zone(2040);
			var a = Player(1, 0, 0);
			var b = Player(2, 10, 0);
			zone.Add(a);
			zone.Add(b);
			InterestManager.ResolveMove(zone, a, Range); // a + b mutually Known, but B never polled (Confirmed empty)

			var events = new WorldEvents(InterestManager.ResolveRemove(zone, a));

			Assert.False(events.Has(2, 1, InterestEventType.Despawn)); // no despawn for the unconfirmed handle
			Assert.DoesNotContain(1u, b.Knowledge.Known); // but A is dropped from B's Known
		}

		[Fact]
		public void Remove_PlayerLeaver_DespawnsForALoadedViewer_EvenUnconfirmed()
		{
			// The client renders another player's spawn without ever polling 0x2020 (measured live 2026-08-26:
			// zero polls for the other member's handle across a whole PvP match, model visibly rendered), so
			// Confirmed never holds a player handle, so a poll gate would leave a phantom player standing after its
			// owner teleported out. For a player leaver the gate is the viewer's own load state instead: a
			// WorldEntered viewer that knew the handle gets the despawn.
			var zone = new Zone(2040);
			var a = Player(1, 0, 0);
			var b = new WorldEntity(2, 2040, true, "P2", new Support.RecordingNetworkClient(2))
			{
				Position = new Vector3(10, 0, 0)
			};
			b.Client!.Session.WorldEntered = true;
			zone.Add(a);
			zone.Add(b);
			InterestManager.ResolveMove(zone, a, Range); // mutually Known; B never polls (players are not polled)

			var events = new WorldEvents(InterestManager.ResolveRemove(zone, a));

			Assert.True(events.Has(2, 1, InterestEventType.Despawn)); // the phantom is removed
			Assert.DoesNotContain(1u, b.Knowledge.Known);
		}

		[Fact]
		public void UnconfirmedHandleLeavingRange_DoesNotDespawn()
		{
			// An entity spawned to a still-loading client (Known) but not yet confirmed (no 0x2020 poll) must not
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
			Assert.DoesNotContain(2u, a.Knowledge.Known); // but it's dropped from Known
		}

		private static (WorldEvents events, Zone zone) MoveTo(Zone zone, WorldEntity mover, float x, float z)
		{
			mover.Position = new Vector3(x, 0, z);
			return (new WorldEvents(InterestManager.ResolveMove(zone, mover, Range)), zone);
		}

		// ---- static world objects -------------------------------------------------------------------------
		//
		// Objects are interest-streamed like NPCs but with the opposite known-set rule, which is the whole point of
		// these tests: retail despawns an object when the player leaves its range and re-sends the full spawn record
		// when they come back (measured across the login captures - 565 of 648 repeated sends byte-identical, 212
		// n3ToClientQuit records carrying world-object handles). NPC interest is deliberately sticky; if the two
		// ever get merged onto one set, these break.

		[Fact]
		public void WorldObjects_OnlyThoseInRangeSpawn()
		{
			var zone = new Zone(2040);
			zone.WorldObjects = [Obj(1, 0, 0), Obj(2, 10, 0), Obj(3, 500, 0)];
			var p = Player(9, 0, 0);

			var (spawn, despawn) = InterestManager.ResolveWorldObjects(zone, p, Range, NoQuests);

			Assert.Equal([1u, 2u], spawn.Select(o => o.Handle).Order().ToArray());
			Assert.Empty(despawn);
			Assert.Equal([1u, 2u], p.Knowledge.KnownObjects.Order().ToArray());
		}

		[Fact]
		public void WorldObjects_AlreadyKnown_AreNotResent()
		{
			var zone = new Zone(2040);
			zone.WorldObjects = [Obj(1, 0, 0)];
			var p = Player(9, 0, 0);

			InterestManager.ResolveWorldObjects(zone, p, Range, NoQuests);
			var (spawn, despawn) = InterestManager.ResolveWorldObjects(zone, p, Range, NoQuests);

			Assert.Empty(spawn); // standing still must not re-spawn what the client already has
			Assert.Empty(despawn);
		}

		// The rule that separates objects from NPCs: walk away -> despawn, walk back -> the whole record again.
		[Fact]
		public void WorldObjects_LeavingRangeDespawns_AndReturningResends()
		{
			var zone = new Zone(2040);
			zone.WorldObjects = [Obj(7, 0, 0)];
			var p = Player(9, 0, 0);

			InterestManager.ResolveWorldObjects(zone, p, Range, NoQuests);
			Assert.Contains(7u, p.Knowledge.KnownObjects);

			p.Position = new Vector3(500, 0, 0);
			var (spawn, despawn) = InterestManager.ResolveWorldObjects(zone, p, Range, NoQuests);
			Assert.Empty(spawn);
			Assert.Equal([7u], despawn);
			Assert.DoesNotContain(7u, p.Knowledge.KnownObjects); // Not sticky - the opposite of NPC Known

			p.Position = new Vector3(0, 0, 0);
			var (back, _) = InterestManager.ResolveWorldObjects(zone, p, Range, NoQuests);
			Assert.Equal([7u], back.Select(o => o.Handle).ToArray()); // full re-send, not a diff
		}

		// A gated object is absent entirely for a player without the quest, and appears once they hold it.
		[Fact]
		public void WorldObjects_QuestGated_SpawnOnlyForTheHolder()
		{
			var zone = new Zone(2040);
			var gated = Obj(5, 0, 0);
			gated.Row.GateQuestId = 13479;
			zone.WorldObjects = [gated];

			var without = Player(9, 0, 0);
			Assert.Empty(InterestManager.ResolveWorldObjects(zone, without, Range, NoQuests).Spawn);

			var with = Player(10, 0, 0);
			Assert.Equal([5u], InterestManager.ResolveWorldObjects(zone, with, Range, new HashSet<uint> { 13479 }).Spawn.Select(o => o.Handle).ToArray());
		}

		private static readonly IReadOnlySet<uint> NoQuests = new HashSet<uint>();

		// The handle is the zone's, not a column - so it is passed in beside the row, the way EnsureZoneWorldObjects
		// mints it (see LiveWorldObject).
		private static LiveWorldObject Obj(uint handle, float x, float z)
		{
			return new LiveWorldObject(handle, new WorldObject
			{
				MapId = 2040,
				PositionX = x,
				PositionY = 0,
				PositionZ = z
			});
		}

		private static WorldEntity Player(uint handle, float x, float z)
		{
			return new WorldEntity(handle, 2040, true, $"P{handle}", null!)
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
