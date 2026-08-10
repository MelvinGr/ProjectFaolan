using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Interfaces;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #718/#765: which near-identical STATIONARY spawns the shipped data says are one NPC harvested twice.
	//
	// The decision is made at import time from the corpus (`StationaryPlacementResolver` - the test is a HANDLE sighted
	// at two of a cluster's positions, not a distance), so what a test without the corpus can gate is the RESULT: that
	// the pooling happened, that it stayed narrow, and that it did not touch the tight placements retail really has.
	// That last one is the assertion with teeth. 181 of the 191 candidate clusters are genuinely concurrent, so a
	// regression to any distance-based rule would collapse the world's guard pairs and animal packs, and would show up
	// here as a pooled row among them.
	public class StationaryPlacementTests
	{
		[Fact]
		public async Task TheThreeScruffys_AreOnePlacement()
		{
			// The reported case: at least three "Scruffy" dogs stood on one spot on map 2000, and there should be one.
			// He MOVES (per #741 he chases Giner the cat), so each capture caught him somewhere slightly different and
			// the (map, XYZ) spawn key minted a placement per sighting. Two captures show one handle standing at two of
			// the three positions, which is what proves it rather than the 0.2u gap between two of them.
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var scruffy = await db
				.NpcSpawns.Include(s => s.NpcTemplate)
				.AsNoTracking()
				.Where(s => s.NpcTemplate!.Name == "Scruffy")
				.ToListAsync(ct);

			Assert.Equal(3, scruffy.Count); // the rows stay - they are the placement's variant pool, not duplicates to delete
			Assert.Single(scruffy.Select(s => s.PlacementId).Distinct());
			Assert.All(scruffy, s => Assert.NotNull(s.PlacementId));
		}

		[Fact]
		public async Task PooledStationaryRows_StayNarrow_AndSpareTheTightRealPLACEMENTS()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var routed = await db.PatrolNodes.Select(p => p.NpcSpawnId).Distinct().ToHashSetAsync(ct);
			var stationary = await db
				.NpcSpawns.Include(s => s.NpcTemplate)
				.AsNoTracking()
				.Where(s => s.NpcTemplate != null)
				.ToListAsync(ct);

			var pooled = stationary.Where(s => !routed.Contains(s.Id) && s.PlacementId != null).ToList();
			var pools = pooled.GroupBy(s => s.PlacementId!.Value).ToList();

			// 9 clusters / 23 pooled rows as measured. A jump means the criterion loosened - most likely back into a
			// distance rule - and the numbers are here so that shows up as a failure rather than as a tidier world.
			Assert.InRange(pools.Count, 1, 20);
			Assert.InRange(pooled.Count, 2, 60);

			// Every pooled group is ONE creature on ONE map: the resolver groups by (map, template, class), so a pool
			// spanning two templates would mean the key collided.
			Assert.All(pools, pool =>
			{
				Assert.Single(pool.Select(s => s.NpcTemplateId).Distinct());
				Assert.Single(pool.Select(s => s.MapId).Distinct());
				Assert.Single(pool.Select(s => s.ClassId).Distinct());
			});

			// The creatures retail really places in tight groups must NOT be pooled - these are the ones a distance rule
			// eats first. Black Dragon Guards flank Tarantia doorways 1.3u apart; Enraged Boars come in packs; the
			// class-specific armory rows sit at literally the same point, one per class.
			foreach (var name in new[] { "Black Dragon Guard", "Enraged Boar", "Quartermaster Haji", "Wolf Pict Envoy" })
			{
				var rows = stationary.Where(s => s.NpcTemplate!.Name == name && !routed.Contains(s.Id)).ToList();
				Assert.NotEmpty(rows);
				Assert.DoesNotContain(rows, s => s.PlacementId != null);
			}
		}
	}
}
