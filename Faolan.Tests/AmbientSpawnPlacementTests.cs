using System.Linq;
using System.Numerics;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// Ambient creatures were standing inside each other - nine pairs at exactly 0.000u in one map-4020 dump, all
	// in the ambient handle range. `FreePoint` walked its points for one with 2u of clearance and, when none had it,
	// ended in `pool.Points[Random.Shared.Next(...)]` - dropping the spacing rule entirely and able to hand back the
	// very point a live member was standing on.
	//
	// The obvious suspect is innocent, and it is worth keeping that here: it is not over-subscription. Measured over
	// the shipped data, `MaxCount` equals the point count exactly on all 3,970 areas. The fallback fires because the
	// client's own points can sit closer together than our 2u radius - 91 areas (2.3%) hold 244 such pairs, and in
	// exactly those the greedy walk runs out of clear points 195 spawns before MaxCount.
	public class AmbientSpawnPlacementTests
	{
		[Fact]
		// The reported failure, reduced: two points closer together than the spacing radius, one already occupied.
		// The old fallback could return the occupied point itself.
		public void WithNoClearPoint_ItNeverReturnsAPointSomebodyIsStandingOn()
		{
			var points = new[] { new Vector3(0, 0, 0), new Vector3(1.5f, 0, 0) };
			var live = new[] { new Vector3(0, 0, 0) };

			var chosen = AmbientPoolService.FreePoint(points, live);

			Assert.NotEqual(live[0], chosen);
			Assert.Equal(points[1], chosen); // the least-crowded of the two
		}

		[Fact]
		// The general guarantee, stated as the property rather than a case: while the pool is under strength - the only
		// time anything is spawned - some point has nobody exactly on it, and the least-crowded rule always finds it.
		public void WhileThePoolIsUnderStrength_TheChosenPointIsNeverExactlyOccupied()
		{
			// Every point inside the spacing radius of every other, so the clearance path never fires.
			var points = Enumerable.Range(0, 6).Select(i => new Vector3(i * 0.4f, 0, 0)).ToArray();

			for (var occupiedCount = 1; occupiedCount < points.Length; occupiedCount++)
			{
				var live = points.Take(occupiedCount).ToArray();
				var chosen = AmbientPoolService.FreePoint(points, live);

				Assert.DoesNotContain(live, p => p == chosen);
			}
		}

		[Fact]
		// The common path is untouched: a point with full clearance still wins, and the first such point is taken, so
		// a pool fills its points in order rather than scattering.
		public void AClearPointIsStillTakenInOrder()
		{
			var points = new[] { new Vector3(0, 0, 0), new Vector3(50, 0, 0), new Vector3(100, 0, 0) };

			Assert.Equal(points[0], AmbientPoolService.FreePoint(points, []));
			Assert.Equal(points[1], AmbientPoolService.FreePoint(points, [points[0]]));
			Assert.Equal(points[2], AmbientPoolService.FreePoint(points, [points[0], points[1]]));
		}

		[Fact]
		// Crowding is a fallback, not a preference: given a genuinely free point it is never passed over for a nearer
		// crowded one, whatever the order.
		public void AFreePointWins_EvenWhenACrowdedOneComesFirst()
		{
			var points = new[] { new Vector3(0, 0, 0), new Vector3(0.5f, 0, 0), new Vector3(80, 0, 0) };
			var live = new[] { new Vector3(0, 0, 0) };

			Assert.Equal(points[2], AmbientPoolService.FreePoint(points, live));
		}

		[Fact]
		// The shipped data's real shape: points the client placed closer together than our radius. Retail put them
		// there deliberately, so the answer is to crowd rather than to refuse - both members are placed, on different
		// points.
		public void TwoClientPointsInsideTheRadius_StillHoldTwoMembers()
		{
			var points = new[] { new Vector3(0, 0, 0), new Vector3(1.2f, 0, 0) };

			var first = AmbientPoolService.FreePoint(points, []);
			var second = AmbientPoolService.FreePoint(points, [first]);

			Assert.NotEqual(first, second);
		}
	}
}
