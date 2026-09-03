using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// #716: what the world tick is allowed to put on the wire, measured against the retail corpus.
	//
	// A 17-minute Old Tarantia session pushed 20,204 MoveTaskIIR + 125,492 BehaviorControlCenterIIR at one client
	// (19.3 and 119.6 per second). Over the 91 corpus captures of at least 60 s that carry either IIR, retail runs
	// MoveTask at a 0.38/s median (p90 0.89, MAX 3.09).
	//
	// Both over-sends were per-something bugs, not tuning: an idle beat was streamed as one 2-point chase segment
	// every ~6 u forever, and the "heartbeat" fired for EVERY live NPC on a shared 1 s accumulator - a record retail
	// only ever sends ONCE per entity (see WorldBroadcaster.SendNpcIdleAssert). These gates hold the mechanisms that
	// replaced them, because a rate regression is invisible in every other test: the effects are individually
	// correct, there are just tens of thousands of them.
	public class WorldTrafficBudgetTests
	{
		// Retail's corpus MAXIMUM, i.e. the most generous reading available.
		private const double RetailMoveTasksPerSecondMax = 3.09;

		[Fact]
		public void IdleBeat_IsIssuedPerLap_NotPerSegment()
		{
			// The MoveTask half. A beat is handed to the client once and walked THERE; the server re-issues it only
			// where the client's copy has run out (a turnaround, or a lap closing). Streaming the walk instead - one
			// segment per ~6 u - is what measured 19.3/s.
			var street = new[] { new Vector3(0, 0, 0), new Vector3(0, 0, 220) };
			var (world, npc) = Walker(1, street);

			var (moveTasks, seconds, reached) = Run(world, npc, 120.0);

			// 220 u at RouteWalkSpeed is 100 s a leg, so 120 s is one leg and a turnaround: a handful of re-issues,
			// not one per waypoint reached and certainly not one per tick.
			Assert.InRange(moveTasks, 1, 4);
			Assert.True(moveTasks / seconds <= RetailMoveTasksPerSecondMax,
				$"{moveTasks / seconds:F2} MoveTask/s from ONE idle patroller, over retail's corpus maximum of {RetailMoveTasksPerSecondMax}");
			// ...and the low rate must be a WALKED beat, not a stalled one: it got to the far end of the street.
			Assert.True(reached > 215f, $"the walk only covered {reached:F1}u of a 220u beat");
		}

		[Fact]
		public void IdleZone_EmitsNoPerTickBccPulseAtAll()
		{
			// The BCC half, and the actual regression. Old Tarantia carries ~120 visible NPCs, which at one heartbeat
			// pulse each per second was the 119.6/s measured at one client.
			//
			// It is not a rate problem any more, it is gone: retail's `BccAction` is a one-shot ASSERT sent as an entity
			// comes into view (p50 1 record per actor over 7,969 captured actors), so it belongs to the client's spawn
			// confirm - `WorldBroadcaster.SendNpcIdleAssert` - and the TICK emits nothing. A zone of NPCs carrying no
			// ambient animations therefore produces no behaviour traffic whatsoever, however many of them there are.
			var world = new WorldState();
			var zone = world.ZoneFor(2000);
			for (var i = 0; i < 200; i++)
				zone.Add(Idle((uint)(i + 1)));

			for (var i = 0; i < 600; i++) // 60 s
				Assert.Empty(world.Tick(0.1));
		}

		[Fact]
		public void AZoneOfIdlers_StaysWithinRetailsAmbientAnimationRate()
		{
			// What DOES recur is the ambient animation (#766), and it is per-entity - so it is the one that has to be
			// checked against a population. Retail runs the Emote channel at 25,778 records over 2,909 actors, p50 9.4s
			// between one actor's, i.e. a per-actor rate around 0.1/s. 200 NPCs all idling at once is a worst case no
			// real zone reaches (only 222 of the shipped creature types have animations at all), and even that must stay
			// inside what retail's own busiest capture put on the wire.
			var world = new WorldState();
			var zone = world.ZoneFor(2000);
			for (var i = 0; i < 200; i++)
			{
				var npc = Idle((uint)(i + 1));
				npc.Presentation.AmbientAnimations = [new AmbientAnimation(62171, 419, 10)]; // the corpus' median duration, 4.19s
				zone.Add(npc);
			}

			var animations = 0;
			for (var i = 0; i < 600; i++) // 60 s
			{
				foreach (var e in world.Tick(0.1))
				{
					if (e.Type == WorldTickEffectType.NpcAmbientAnimation)
						animations++;
				}
			}

			// 4.19s of animation plus a ~2.2s median pause is one per NPC per ~6.4s, so 200 NPCs is ~31/s across the
			// zone - and each record only reaches the viewers who have that NPC confirmed, which is what makes a
			// per-entity rate acceptable where a per-entity heartbeat was not. Held to twice the arithmetic so a
			// scheduling bug that fired every tick (2,000/s) fails loudly.
			Assert.InRange(animations / 60.0, 20, 62);
		}

		[Fact]
		public void EveryIdler_PerformsAnAnimation_NoneIsStarved()
		{
			// ...and the per-entity clock must not starve anyone. The heartbeat this replaced had to ROTATE through the
			// population to cap its aggregate, which is exactly the kind of scheme that can silently drop the tail.
			var world = new WorldState();
			var zone = world.ZoneFor(2000);
			var handles = new List<uint>();
			for (var i = 0; i < 20; i++)
			{
				var npc = Idle((uint)(i + 1));
				npc.Presentation.AmbientAnimations = [new AmbientAnimation(62171, 200, 10)];
				zone.Add(npc);
				handles.Add(npc.Handle);
			}

			var performed = new HashSet<uint>();
			for (var i = 0; i < 600; i++) // 60 s
			{
				foreach (var e in world.Tick(0.1))
				{
					if (e.Type == WorldTickEffectType.NpcAmbientAnimation)
						performed.Add(e.Entity.Handle);
				}
			}

			Assert.Equal(handles.OrderBy(h => h), performed.OrderBy(h => h));
		}

		// Ticks `seconds` of world at 10 Hz (the server's rate) and returns how many MoveTask-bearing effects came out,
		// plus how far along its beat `walker` ever got - a rate is only good news if the ground was still covered.
		private static (int MoveTasks, double Seconds, float Reached) Run(WorldState world, WorldEntity walker, double seconds)
		{
			var moveTasks = 0;
			var reached = 0f;
			for (var i = 0; i < (int)(seconds * 10); i++)
			{
				foreach (var e in world.Tick(0.1))
				{
					if (e.Type is WorldTickEffectType.NpcPatrol or WorldTickEffectType.NpcMove or WorldTickEffectType.NpcFollow)
						moveTasks++;
				}

				reached = MathF.Max(reached, Vector3.Distance(walker.Route.SpawnPosition, walker.Position));
			}

			return (moveTasks, seconds, reached);
		}

		// A live, non-hostile, stationary NPC - a Citizen standing in the street. Spread out so nobody aggros, follows
		// or leashes: this measures the IDLE floor.
		private static WorldEntity Idle(uint handle)
		{
			var at = new Vector3(handle * 40f, 0, 0);
			var npc = WorldEntity.ForNpc(handle, 2000, at, Quaternion.Identity, "Citizen", EntityVitals.FromMax(100, 0, 100));
			npc.Hostile = false;
			return npc;
		}

		// A route walker on `route`, wired the way WorldBroadcaster wires a captured beat.
		private static (WorldState World, WorldEntity Npc) Walker(uint handle, Vector3[] route)
		{
			var world = new WorldState();
			var npc = WorldEntity.ForNpc(handle, 2000, route[0], Quaternion.Identity, "Tarantia Guard", EntityVitals.FromMax(100, 0, 100));
			npc.Hostile = false;
			world.Place(npc, 2000, route[0], Quaternion.Identity);
			npc.Route.PatrolWaypoints = route;
			npc.Route.WalksRoute = true;
			npc.Route.RouteLoops = route.Length > 2 && Vector3.Distance(route[0], route[^1]) <= 5f;
			npc.Route.RejoinRouteAtNearest(npc.Position);
			return (world, npc);
		}
	}
}
