using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.World;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The imported bounded-area regions (MapRegion, #519/#521) tested through the SAME routine the runtime uses,
	// against the SHIPPED rows rather than a synthetic polygon. The parser's own grammar gates live in
	// BoundedAreaCollectionTests; this is about the geometry surviving the trip through the importer and the DB.
	public class MapRegionGeometryTests
	{
		private static async Task<MapRegion> Region(DatabaseContext db, uint mapId, string name)
		{
			var region = await db.MapRegions.Include(r => r.Vertices).FirstOrDefaultAsync(r => r.MapId == mapId && r.Name == name, TestContext.Current.CancellationToken);
			Assert.NotNull(region);
			return region!;
		}

		// The #521 case end to end: quest 10238's goal 1448 resolves (via LDB category 51200 id 180) to `Trappers
		// Haven` on Tortage Island, and the goal's own map MARKER lands inside that region. That the client's region
		// and the client's marker agree is the evidence the resolution picked the right polygon - a wrong region
		// would almost certainly not contain the pin.
		[Fact]
		public async Task BeachStarterReachGoal_ResolvesToARegionContainingItsOwnMarker()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var goal = await db
				.QuestGoalDefinitions.Include(g => g.Region!.Vertices)
				.FirstOrDefaultAsync(g => g.GoalId == 1448, ct);

			Assert.NotNull(goal);
			Assert.NotNull(goal!.Region);
			Assert.Equal("Trappers Haven", goal.Region!.Name);
			Assert.Equal(4010u, goal.Region.MapId);

			// The marker, lifted into the region's own height band - the wire marker is (x, z) only.
			var marker = new Vector3(goal.MarkerX!.Value, (goal.Region.MinY + goal.Region.MaxY) / 2f, goal.MarkerY!.Value);
			Assert.True(EnvironmentService.Contains(goal.Region, marker), "goal 1448's marker is outside the region it resolved to");
		}

		// The prism is three-dimensional, and that is the point of carrying <heights> at all: a discovery volume is
		// ~10 units tall, so a player on a floor above must not trip it. Same lesson the lava volumes taught (#504).
		[Fact]
		public async Task Region_RejectsAPointOutsideItsHeightBand()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var region = await Region(db, 4010, "Trappers Haven");

			var centreX = (region.MinX + region.MaxX) / 2f;
			var centreZ = (region.MinZ + region.MaxZ) / 2f;

			Assert.True(EnvironmentService.Contains(region, new Vector3(centreX, (region.MinY + region.MaxY) / 2f, centreZ)));
			Assert.False(EnvironmentService.Contains(region, new Vector3(centreX, region.MaxY + 50f, centreZ)));
			Assert.False(EnvironmentService.Contains(region, new Vector3(centreX, region.MinY - 50f, centreZ)));
		}

		// A point well outside the footprint is rejected - the AABB fast path.
		[Fact]
		public async Task Region_RejectsAPointOutsideItsFootprint()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var region = await Region(db, 4010, "Trappers Haven");
			var y = (region.MinY + region.MaxY) / 2f;

			Assert.False(EnvironmentService.Contains(region, new Vector3(region.MinX - 100f, y, region.MinZ - 100f)));
		}

		// ⚠️ The rotated ones are why this table stores a RING and not four floats. `BA_MountainGlade_RezDiscovery`
		// is a rotated quad: its axis-aligned box is nearly twice its real area, so a bbox test would fire from
		// well outside the volume - on a 16 x 20 pad whose whole purpose is to be tighter than the 30-unit radius
		// it replaces. This pins a corner of that box as OUTSIDE the polygon.
		[Fact]
		public async Task RotatedDiscoveryVolume_IsNarrowerThanItsBoundingBox()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var region = await Region(db, 1010, "BA_MountainGlade_RezDiscovery");
			var y = (region.MinY + region.MaxY) / 2f;

			var ring = region.Vertices.OrderBy(v => v.Sequence).ToArray();
			var polygon = System.Math.Abs(ring.Select((v, i) =>
			{
				var n = ring[(i + 1) % ring.Length];
				return v.X * n.Z - n.X * v.Z;
			}).Sum()) / 2f;
			var box = (region.MaxX - region.MinX) * (region.MaxZ - region.MinZ);

			Assert.True(polygon < box * 0.75f, $"expected a rotated quad; polygon {polygon:F0} u² vs box {box:F0} u²");

			// Every corner of the bounding box lies outside the polygon - which is exactly what a bbox test would get wrong.
			Assert.False(EnvironmentService.Contains(region, new Vector3(region.MinX, y, region.MinZ)));
			Assert.False(EnvironmentService.Contains(region, new Vector3(region.MaxX, y, region.MaxZ)));
		}
	}
}
