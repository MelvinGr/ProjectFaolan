using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
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

		// --- #513/#679/#680: the AREA BANNERS -------------------------------------------------------------------
		// A banner is a COLUMN on the volume that fires it. It used to be a `MapArea` row carrying a hull over the
		// positions retail was captured firing it at, and the gates here tested that hull against the region it was
		// matched to. The hull is gone - it was an approximation the runtime had to widen with a slack radius, and
		// the client ships the real polygon for effectively every banner - so what is shippable is the ASSIGNMENT,
		// and that is what these hold. The evidence that produced it (hull inside volume, the two routes agreeing)
		// is checked where the evidence lives: in the importer, against the real corpus, and printed by
		// `--report area-banner-names`.

		// The FILTER both routes share. Every banner volume runs the client's crossing-notifier scry script; without
		// it Old Tarantia's banners returned up to four containing regions, the tightest being `City_sounds` at
		// 142,490 u² - an audio region. A region that never tells the server the player entered it cannot be what
		// fires a server-sent banner, which is as true of a name match as of a geometric one: "Outlaw Camp" also
		// names audio and collision volumes.
		[Fact]
		public async Task EveryBannerVolume_RunsTheCrossingNotifierScript()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var banners = await db.MapRegions.Where(r => r.AreaNameId != null).ToListAsync(TestContext.Current.CancellationToken);

			Assert.NotEmpty(banners);
			Assert.All(banners, r => Assert.Equal(BoundedAreaClassifier.CrossingNotifierScryId, r.ScryId));
		}

		// A banner id identifies ONE area of one playfield, so no two volumes may claim the same one - the server
		// would have no way to say which of them the player just walked into, and the client renders one string.
		// Provenance travels with it: a region either has both halves or neither.
		[Fact]
		public async Task ABannerIdBelongsToOneVolume_AndCarriesItsProvenance()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var regions = await db.MapRegions.ToListAsync(TestContext.Current.CancellationToken);

			Assert.All(regions, r => Assert.Equal(r.AreaNameId == null, r.AreaNameSource == null));
			Assert.All(regions.Where(r => r.AreaNameId != null).GroupBy(r => r.AreaNameId), g => Assert.Single(g));
		}

		// The coverage this subsystem exists to have, as a ratchet rather than a description: the recovery reaches
		// playfields no capture has ever visited, and a rule change that quietly stopped reaching them would leave
		// every other gate here passing.
		[Fact]
		public async Task TheBannerRecovery_ReachesFarMoreThanTheCapturesDid()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var banners = await db.MapRegions.Where(r => r.AreaNameId != null).ToListAsync(TestContext.Current.CancellationToken);

			// 22 banners over 7 playfields were all the captures ever gave, and 12 of those had a volume.
			Assert.True(banners.Count >= 140, $"{banners.Count} banner volumes; the recovery resolves 140+ (#679/#680)");
			Assert.True(banners.Select(r => r.MapId).Distinct().Count() >= 13, "the recovery reaches 13+ playfields, against the captures' 7");

			// The three routes all have to still be firing. ClientName is the bulk (playfields no capture visited),
			// Corroborated is the cross-check, and Captured is the geometric route - the only one that can reach a
			// banner whose region carries an internal name.
			Assert.All(new[] { AreaBannerSource.ClientName, AreaBannerSource.Corroborated, AreaBannerSource.Captured },
				source => Assert.True(banners.Any(r => r.AreaNameSource == source), $"no banner resolved by {source}"));
		}

		// Old Tarantia is the map where the answer can be read by eye, so it is worth pinning by name. Four of these
		// were matched GEOMETRICALLY, from a hull over captured walk positions, before the client's own names could
		// be recovered at all - and the two routes agree on `BA_TradingPost` and both gates, which is the check
		// neither could give itself.
		[Fact]
		public async Task OldTarantiasBanners_LandOnTheRegionsNamedAfterThem()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var byBanner = (await db
					.MapRegions.Where(r => r.MapId == 2000 && r.AreaNameId != null)
					.ToListAsync(TestContext.Current.CancellationToken))
				.ToDictionary(r => r.AreaNameId!.Value, r => r.Name);

			Assert.Equal("BA_Bridge", byBanner[3373519]); // "Bridge of Vilerus I" - geometric; the name join cannot reach it
			Assert.Equal("BA_LionMarket", byBanner[3373521]); // "The Lion's Market" - the possessive s
			Assert.Equal("BA_TradingPost", byBanner[3373522]); // both routes
			Assert.Equal("BA_AVRoses", byBanner[3373523]); // "Avenue of Roses" - the abbreviation pass
			Assert.Equal("BA_MarketWard", byBanner[3373525]);
			Assert.Equal("BA_AVLions", byBanner[3373530]); // "Avenue of Lions"
			Assert.Equal("BA_EastGate", byBanner[3385037]); // "The East Gate" - the article
			Assert.Equal("BA_WestGate", byBanner[3385038]);
			Assert.Equal("Outlaw Camp", byBanner[3906077]); // the generic name, pinned by the wire
		}
	}
}
