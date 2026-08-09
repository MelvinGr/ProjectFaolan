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
	// MoveTask at a 0.38/s median (p90 0.89, MAX 3.09) and the BCC heartbeat at 0.93/s (p90 2.35, max 40.78 - and
	// that maximum is an 89 s resurrect burst, not a floor).
	//
	// Both over-sends were per-something bugs, not tuning: the heartbeat fired for EVERY live NPC on a shared 1 s
	// accumulator (so the rate scaled with the zone's population), and an idle beat was streamed as one 2-point
	// chase segment every ~6 u forever. These gates hold the two mechanisms that replaced them, because a rate
	// regression is invisible in every other test - the effects are individually correct, there are just tens of
	// thousands of them.
	public class WorldTrafficBudgetTests
	{
		// Retail's p90 over the 91 captures. The cap is an AGGREGATE per zone (WorldState.HeartbeatsPerSecond = 2),
		// so a hundred NPCs cost what two do - they take turns.
		private const double RetailHeartbeatsPerSecondP90 = 2.35;

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
		public void IdleZone_HeartbeatRate_DoesNotScaleWithPopulation()
		{
			// The BCC half, and the actual regression: the rate must be flat in the zone's population. Old Tarantia
			// carries ~120 visible NPCs, which at one pulse each per second was the 119.6/s measured.
			var few = HeartbeatRate(10);
			var many = HeartbeatRate(200);

			Assert.True(many <= RetailHeartbeatsPerSecondP90,
				$"{many:F2} BCC/s in a 200-NPC zone, over retail's p90 of {RetailHeartbeatsPerSecondP90}");
			Assert.True(many <= few * 1.5, $"the heartbeat rate scaled with the population: {few:F2}/s at 10 NPCs, {many:F2}/s at 200");
		}

		[Fact]
		public void SparseZone_StillHeartbeatsEveryNpc()
		{
			// ...and the cap must not silence anyone: the pulse ROTATES through the population, so every NPC is still
			// asserted, just on a period that grows with how many there are. A cap that dropped the tail would leave
			// those NPCs frozen mid-idle, which is the thing the heartbeat exists to prevent (#225).
			var world = new WorldState();
			var zone = world.ZoneFor(2000);
			var handles = new List<uint>();
			for (var i = 0; i < 20; i++)
			{
				var npc = Idle((uint)(i + 1));
				zone.Add(npc);
				handles.Add(npc.Handle);
			}

			var pulsed = new HashSet<uint>();
			for (var i = 0; i < 300; i++) // 30 s: 20 NPCs at 2 pulses/s is a full sweep every 10 s
			{
				foreach (var e in world.Tick(0.1))
				{
					if (e.Type == WorldTickEffectType.NpcHeartbeat)
						pulsed.Add(e.Entity.Handle);
				}
			}

			Assert.Equal(handles.OrderBy(h => h), pulsed.OrderBy(h => h));
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

				reached = MathF.Max(reached, Vector3.Distance(walker.SpawnPosition, walker.Position));
			}

			return (moveTasks, seconds, reached);
		}

		// Heartbeat pulses per second in a zone holding `population` idle NPCs.
		private static double HeartbeatRate(int population)
		{
			var world = new WorldState();
			var zone = world.ZoneFor(2000);
			for (var i = 0; i < population; i++)
				zone.Add(Idle((uint)(i + 1)));

			var pulses = 0;
			for (var i = 0; i < 600; i++) // 60 s
			{
				foreach (var e in world.Tick(0.1))
				{
					if (e.Type == WorldTickEffectType.NpcHeartbeat)
						pulses++;
				}
			}

			return pulses / 60.0;
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
			npc.PatrolWaypoints = route;
			npc.WalksRoute = true;
			npc.RouteLoops = route.Length > 2 && Vector3.Distance(route[0], route[^1]) <= 5f;
			npc.RejoinRouteAtNearest();
			return (world, npc);
		}
	}
}
