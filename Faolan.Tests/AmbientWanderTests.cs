using System.Numerics;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// Ambient anti-stacking: a pool member's wander prefers an anchor no other entity occupies (isOccupied
	// predicate), so a pool spreads across its area's points instead of piling up - while still always moving.
	public class AmbientWanderTests
	{
		private static readonly Vector3 A = new Vector3(0, 0, 0);
		private static readonly Vector3 B = new Vector3(10, 0, 0);
		private static readonly Vector3 C = new Vector3(20, 0, 0);

		[Fact]
		public void FallsBackToAnchor_WhenEveryFreeSpotIsTaken()
		{
			var e = Croc();
			// Every non-current anchor occupied -> still returns a real anchor (B or C), never stalls at A.
			var p = e.Route.NextWanderPoint(e.Position, _ => true);
			Assert.True(p == B || p == C);
			Assert.NotEqual(A, p);
		}

		[Fact]
		public void NoPredicate_ReturnsNonCurrentAnchor()
		{
			var e = Croc();
			for (var i = 0; i < 100; i++)
				Assert.NotEqual(A, e.Route.NextWanderPoint(e.Position));
		}

		[Fact]
		public void PrefersFreeAnchor_NeverPicksOccupiedOrCurrent()
		{
			var e = Croc();
			// B occupied, C free, A is current -> must always pick C, regardless of the random start offset.
			for (var i = 0; i < 100; i++)
				Assert.Equal(C, e.Route.NextWanderPoint(e.Position, pt => pt == B));
		}

		private static WorldEntity Croc()
		{
			var e = WorldEntity.ForNpc(1, 4010, A, Quaternion.Identity, "Croc", EntityVitals.FromMax(10, 0, 10));
			e.Route.PatrolWaypoints = [A, B, C];
			e.Position = A;
			return e;
		}
	}
}
