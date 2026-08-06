using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.World;
using Faolan.Importer.Rdb;
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

		// --- #513: the AREA BANNER volumes ---------------------------------------------------------------------
		// A banner is an LDB string id and a region is a place name, so the link between them is GEOMETRIC and has
		// no name to check it against. These gate the property that makes the match trustworthy instead.

		// Every linked banner's captured firing hull has to sit in the region it was matched to. This is the whole
		// basis of the match, and it is the thing a rule change would break silently - the link would still be there,
		// just pointing at the wrong polygon.
		[Fact]
		public async Task EveryLinkedBanner_SitsInsideItsOwnClientRegion()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var linked = await db
				.MapAreas.Include(a => a.Region!.Vertices)
				.Where(a => a.RegionId != null)
				.ToListAsync(TestContext.Current.CancellationToken);

			Assert.NotEmpty(linked);
			foreach (var area in linked)
			{
				var centre = new Vector3((area.MinX + area.MaxX) / 2f, (area.Region!.MinY + area.Region.MaxY) / 2f, (area.MinZ + area.MaxZ) / 2f);
				Assert.True(EnvironmentService.Contains(area.Region, centre),
					$"banner {area.AreaNameId} on map {area.MapId} is linked to '{area.Region.Name}' but its captured hull centre is outside it");
			}
		}

		// The SEPARATION guard: a region holding two banners cannot be what distinguishes them, so no region may be
		// linked to more than one banner, and no banner's region may swallow a sibling banner on the same map.
		// Without this the test above passes happily with every banner on a map pointing at one big region.
		[Fact]
		public async Task ALinkedRegion_DoesNotContainAnotherBannerOnTheSameMap()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var areas = await db.MapAreas.Include(a => a.Region!.Vertices).ToListAsync(TestContext.Current.CancellationToken);

			Assert.All(areas.Where(a => a.RegionId != null).GroupBy(a => a.RegionId), g => Assert.Single(g));

			foreach (var area in areas.Where(a => a.RegionId != null))
			{
				var y = (area.Region!.MinY + area.Region.MaxY) / 2f;
				foreach (var other in areas.Where(o => o.MapId == area.MapId && o.AreaNameId != area.AreaNameId))
				{
					var centre = new Vector3((other.MinX + other.MaxX) / 2f, y, (other.MinZ + other.MaxZ) / 2f);
					Assert.False(EnvironmentService.Contains(area.Region, centre),
						$"'{area.Region.Name}' (banner {area.AreaNameId}) also contains banner {other.AreaNameId} - it cannot be what tells them apart");
				}
			}
		}

		// The FILTER that made the match unambiguous. Every banner volume runs the client's crossing-notifier scry
		// script; without it Old Tarantia's banners returned up to four containing regions, the tightest being
		// `City_sounds` at 142,490 u² - an audio region. A region that never tells the server the player entered it
		// cannot be what fires a server-sent banner.
		[Fact]
		public async Task EveryBannerVolume_RunsTheCrossingNotifierScript()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var linked = await db.MapAreas.Include(a => a.Region).Where(a => a.RegionId != null).ToListAsync(TestContext.Current.CancellationToken);

			Assert.All(linked, a => Assert.Equal(BoundedAreaClassifier.CrossingNotifierScryId, a.Region!.ScryId));
		}

		// Old Tarantia is the map where the answer can be read by eye, so it is worth pinning by name: the client's
		// own region names for the four banners we hold several samples of say bridge, trading post, market ward and
		// the two gates - which is what `MapArea`'s comment lists as Old Tarantia's banner texts, arrived at from a
		// completely different direction (a hull over captured walk positions).
		[Fact]
		public async Task OldTarantiasBanners_LandOnTheRegionsNamedAfterThem()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var byBanner = (await db
					.MapAreas.Include(a => a.Region)
					.Where(a => a.MapId == 2000 && a.RegionId != null)
					.ToListAsync(TestContext.Current.CancellationToken))
				.ToDictionary(a => a.AreaNameId, a => a.Region!.Name);

			Assert.Equal("BA_Bridge", byBanner[3373519]); // "Bridge of Vilerus I"
			Assert.Equal("BA_MarketWard", byBanner[3373525]);
			Assert.Equal("BA_EastGate", byBanner[3385037]); // "The East Gate"
			Assert.Equal("BA_WestGate", byBanner[3385038]); // "The West Gate"
		}
	}
}
