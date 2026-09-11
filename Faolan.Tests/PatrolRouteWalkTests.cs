using System;
using System.Numerics;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// A harvested patrol route is an ordered, densely sampled polyline - the path the retail NPC actually walked,
	// which in a city means down the street and round the corner. A wander that picks a random waypoint off the list
	// and moves the NPC there in one straight line throws that order away, so an Old Tarantia guard walks through the
	// block instead of round it. These gates hold the walk on the polyline.
	public class PatrolRouteWalkTests
	{
		[Fact]
		public void ClosedRoute_KeepsGoingRoundInTheSameDirection()
		{
			// Ends that meet: the wrap from last back to first is a route segment, so there is nothing to turn round for.
			var square = new[]
			{
				new Vector3(0, 0, 0), new Vector3(0, 0, 30), new Vector3(30, 0, 30), new Vector3(30, 0, 0),
				new Vector3(0, 0, 0)
			};
			var (world, npc) = Walker(square);

			for (var i = 0; i < 600; i++)
				world.Tick(0.1);

			Assert.True(npc.Route.RouteLoops);
			Assert.False(npc.Route.RouteBackwards); // a closed route never reverses - it wraps
			Assert.True(OnPolyline(square, npc.Position) <= 1f);
		}

		[Fact]
		public void OpenRoute_TurnsRoundAtTheEnd_RatherThanCuttingBackToTheStart()
		{
			// An Old Tarantia beat does not close: its two ends are ~77u and a row of houses apart. Jumping from the
			// last waypoint back to the first would draw exactly the through-the-building line this change removes, so
			// an open route is walked back down its own polyline instead.
			var street = new[] { new Vector3(0, 0, 0), new Vector3(0, 0, 40), new Vector3(40, 0, 40) };
			var (world, npc) = Walker(street);

			var reachedFarEnd = false;
			var cameBack = false;
			var turnedRound = false;
			for (var i = 0; i < 1200; i++) // 120s: 160u of beat at RouteWalkSpeed, plus the turnaround dwell
			{
				world.Tick(0.1);
				Assert.True(OnPolyline(street, npc.Position) <= 1f, $"left the route at {npc.Position}");
				turnedRound |= npc.Route.RouteBackwards;
				if (Vector3.Distance(npc.Position, street[^1]) < 1f)
					reachedFarEnd = true;
				else if (reachedFarEnd && Vector3.Distance(npc.Position, street[0]) < 1f)
					cameBack = true;
			}

			Assert.True(reachedFarEnd, "the walk never got to the far end of the beat");
			Assert.True(turnedRound, "an open route turns round rather than wrapping");
			Assert.True(cameBack, "it never walked back down the polyline to the start");
		}

		[Fact]
		public void LongLeg_IsStillTheNpcsOwnTerritory_SoItIsNotLeashedOffItsBeat()
		{
			// The leash territory is the route's polyline, not its vertices. A beat sampled sparsely - the 150
			// single-leg animal wanders on map 2070 run up to 228u between their two waypoints - puts the whole middle
			// of the leg further than NpcLeashRange from both ends, so the walker set off, was read as "dragged too far
			// from home" ~40u out, walked back streaming chase segments, rejoined at the nearest waypoint and did it
			// again. Forever, and it never once saw the far end of its own beat.
			var leg = new[] { new Vector3(0, 0, 0), new Vector3(0, 0, 220) };
			var (world, npc) = Walker(leg);

			var reached = 0f;
			for (var i = 0; i < 1100; i++) // 110s: 220u at RouteWalkSpeed is 100s of walking
			{
				world.Tick(0.1);
				Assert.False(npc.Route.Returning, $"leashed off its own route at {npc.Position}");
				reached = MathF.Max(reached, npc.Position.Z);
			}

			Assert.True(reached > 215f, $"only covered {reached:F1}u of a 220u leg");
		}

		[Fact]
		public void RouteWalker_AfterAChase_RejoinsAtTheNEARESTWaypoint()
		{
			// Dragged off its beat, an NPC must pick the route up where it now stands. Resuming the stale index would
			// send it back across the map to a leg it left behind - a long straight line through everything between.
			var street = new[] { new Vector3(0, 0, 0), new Vector3(0, 0, 40), new Vector3(0, 0, 80) };
			var (_, npc) = Walker(street);

			npc.Position = new Vector3(3, 0, 78); // where a chase ended: right beside the last waypoint
			npc.Route.RouteInterrupted = true;
			npc.Route.RejoinRouteAtNearest(npc.Position);

			Assert.Equal(2, npc.Route.RouteIndex);
			Assert.False(npc.Route.RouteInterrupted);
		}

		[Fact]
		public void RouteWalker_StaysOnTheStreet_InsteadOfCuttingTheCorner()
		{
			// The reported bug, in miniature: an L-shaped street. The old wander's random-anchor pick draws the diagonal
			// - straight through whatever building fills the corner. Walking the waypoints in order cannot.
			var corner = new[] { new Vector3(0, 0, 0), new Vector3(0, 0, 60), new Vector3(60, 0, 60) };
			var (world, npc) = Walker(corner);

			var reachedCorner = false;
			for (var i = 0; i < 600; i++) // 60s: the corner is 60u along at RouteWalkSpeed
			{
				world.Tick(0.1);
				Assert.True(OnPolyline(corner, npc.Position) <= 1f, $"cut the corner at {npc.Position}");
				if (Vector3.Distance(npc.Position, corner[1]) < 1.5f)
					reachedCorner = true;
			}

			Assert.True(reachedCorner, "the walk never rounded the corner - it must pass through the waypoint");
		}

		[Fact]
		public void RouteWalker_TakesTheWaypointAltitude_SoAStaircaseIsClimbed()
		{
			// A captured beat carries real altitudes (an Old Tarantia route climbs 99 -> 112 up a staircase). Keeping
			// the current Y - right for a chase across flat ground - would walk it through the steps at ground level.
			var stairs = new[] { new Vector3(0, 100, 0), new Vector3(0, 112, 30) };
			var (world, npc) = Walker(stairs);

			var climbed = false;
			for (var i = 0; i < 250; i++) // 25s: 30u of staircase at RouteWalkSpeed
			{
				world.Tick(0.1);
				// The height gap closes in step with the horizontal one, so the NPC is on the stairs the whole way up.
				var expected = 100f + 12f * Math.Clamp(npc.Position.Z / 30f, 0f, 1f);
				Assert.Equal(expected, npc.Position.Y, 1);
				climbed |= npc.Position.Y > 111f;
			}

			Assert.True(climbed, "never reached the top of the stairs");
		}

		[Fact]
		public void RouteWalker_WalksContinuously_RatherThanDriftingOnTheWanderClock()
		{
			// A wander moves once every 25-60s. A patrol walks, so ground is covered steadily: ten seconds in, a
			// wanderer would still be standing on its spawn (its first move is not even due yet), while a walker is
			// 22u down the street at RouteWalkSpeed. That gap is what these ticks measure.
			var street = new[] { new Vector3(0, 0, 0), new Vector3(0, 0, 200) };
			var (world, npc) = Walker(street);

			for (var i = 0; i < 100; i++) // 10 seconds
				world.Tick(0.1);

			Assert.True(npc.Position.Z > 15f, $"barely moved in 10s: {npc.Position}");
		}

		// The distance from `point` to the nearest segment of the polyline - how far off its own route the NPC is.
		private static float OnPolyline(Vector3[] route, Vector3 point)
		{
			var nearest = float.MaxValue;
			for (var i = 1; i < route.Length; i++)
				nearest = MathF.Min(nearest, ToSegment(route[i - 1], route[i], point));
			return nearest;
		}

		private static float ToSegment(Vector3 a, Vector3 b, Vector3 p)
		{
			var ab = b - a;
			var lengthSquared = ab.LengthSquared();
			if (lengthSquared < 1e-6f)
				return Vector3.Distance(a, p);

			var t = Math.Clamp(Vector3.Dot(p - a, ab) / lengthSquared, 0f, 1f);
			return Vector3.Distance(a + ab * t, p);
		}

		// A live NPC in a zone, standing on the first waypoint of `route` and walking it - the runtime wiring
		// WorldBroadcaster does when it attaches a harvested route to a spawn.
		private static (WorldState World, WorldEntity Npc) Walker(Vector3[] route)
		{
			var world = new WorldState();
			var npc = WorldEntity.ForNpc(10, 2000, route[0], Quaternion.Identity, "Tarantia Guard", EntityVitals.FromMax(100, 0, 100));
			world.Place(npc, 2000, route[0], Quaternion.Identity);
			npc.Route.PatrolWaypoints = route;
			npc.Route.WalksRoute = true;
			npc.Route.RouteLoops = route.Length > 2 && Vector3.Distance(route[0], route[^1]) <= 5f;
			npc.Route.RejoinRouteAtNearest(npc.Position);
			return (world, npc);
		}
	}
}
