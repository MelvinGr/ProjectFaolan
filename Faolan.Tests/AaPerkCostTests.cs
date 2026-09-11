using System;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The alternate-advancement perk prices - RDB 1000099, imported as `FeatPerkCost`.
	//
	// The point of these gates is that the cost table and the feat-tree table have to agree about what an AA perk
	// is. A price that lands outside the twelve AA trees, or an AA placement with no price, would each mean the
	// join key (`FeatClassEntry.Id`, the client's own featclass row id) had drifted - and that key is the whole
	// reason the table joins to anything at all, since it is not a feat id.
	public class AaPerkCostTests
	{
		// The twelve level-20/80 trees, one per playable class. 1000185 and 1000187 are absent from the client's
		// data - the range is not dense, which is why this is a bound rather than a count.
		private const uint FirstAaTree = 1000177;
		private const uint LastAaTree = 1000190;

		[Fact]
		public async Task EveryCost_PricesARowInAnAaTree()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var total = await db.FeatPerkCosts.AsNoTracking().CountAsync(ct);
			Assert.NotEqual(0, total); // the table is the whole point

			var outside = await (from cost in db.FeatPerkCosts.AsNoTracking()
					join entry in db.FeatClassEntries.AsNoTracking() on cost.FeatClassEntryId equals entry.Id
					where entry.TreeId < FirstAaTree || entry.TreeId > LastAaTree
					select entry.TreeId)
				.Distinct()
				.ToListAsync(ct);

			Assert.True(outside.Count == 0, $"feat point costs outside the AA trees: {string.Join(", ", outside)}");
		}

		// The other direction, and the one that would catch a partial read: every AA placement is priced. An ordinary
		// feat has no row here at all because it costs one point per rank (FeatRules.PointCost).
		[Fact]
		public async Task EveryAaPlacement_HasAPrice()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var unpriced = await db.FeatClassEntries.AsNoTracking()
				.Where(e => e.TreeId >= FirstAaTree && e.TreeId <= LastAaTree)
				.Where(e => !db.FeatPerkCosts.Any(c => c.FeatClassEntryId == e.Id))
				.CountAsync(ct);

			Assert.Equal(0, unpriced);
		}

		// The pool column is the client's own `AAFeatPlannerPointType` (e_PvP = 2, e_PvE = 3, e_Generic = 4). The
		// shipped table uses only the two specific pools - if a Generic row ever appeared it would be new data about
		// the economy, not noise, so it is asserted rather than assumed away.
		[Fact]
		public async Task PointType_IsOnlyTheTwoSpecificAaPools()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var pools = await db.FeatPerkCosts.AsNoTracking().Select(c => c.PointType).Distinct().OrderBy(p => p).ToListAsync(ct);

			Assert.Equal([TokenPool.AltAdvancementPvPToken, TokenPool.AltAdvancementPvEToken], pools);
		}

		// A perk priced in both pools costs the same either way - so "which currency" is a restriction on who may buy
		// it, never a discount. 216 of the 2,880 placements are dual-priced.
		[Fact]
		public async Task ADualPricedPlacement_CostsTheSameInBothPools()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var mismatched = await db.FeatPerkCosts.AsNoTracking()
				.GroupBy(c => c.FeatClassEntryId)
				.Where(g => g.Count() > 1 && g.Min(c => c.Cost) != g.Max(c => c.Cost))
				.CountAsync(ct);

			Assert.Equal(0, mismatched);
		}

		// The ladder: a later rank of the same perk placement always costs more. `Electric Wards` runs 1 / 4 / 8 /
		// 14 / 23 over its five ranks, and nothing in the table goes flat or backwards - which is what makes a rising
		// per-rank price a rule rather than an observation about one perk.
		[Fact]
		public async Task Cost_RisesWithEveryRankOfAPlacement()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var priced = await (from cost in db.FeatPerkCosts.AsNoTracking()
					join entry in db.FeatClassEntries.AsNoTracking() on cost.FeatClassEntryId equals entry.Id
					select new { entry.TreeId, entry.Tier, entry.TreeColumn, entry.FeatId, cost.PointType, cost.Cost })
				.ToListAsync(ct);

			foreach (var placement in priced.GroupBy(p => (p.TreeId, p.Tier, p.TreeColumn, p.PointType)))
			{
				// The wire feat id's low two digits are the rank (FeatIdMap.MaxRank's 0..9 bound).
				var ladder = placement.OrderBy(p => p.FeatId % 100).ToList();
				for (var i = 1; i < ladder.Count; i++)
					Assert.True(ladder[i].Cost > ladder[i - 1].Cost,
						$"tree {placement.Key.TreeId} tier {placement.Key.Tier} col {placement.Key.TreeColumn} "
						+ $"({placement.Key.PointType}): rank {ladder[i].FeatId % 100} costs {ladder[i].Cost}, "
						+ $"no more than rank {ladder[i - 1].FeatId % 100}'s {ladder[i - 1].Cost}");
			}
		}

		// The parser, against bytes rather than the install - the same split ParseClassEntries has. Note the header:
		// this table's count is the fifth word, unlike its two 20-byte-header siblings whose count is the fourth.
		[Fact]
		public void ParsePerkCosts_ReadsTheFifthWordAsTheCount()
		{
			var body = new byte[20 + (3 * 12)];
			Write(body, 0, 1000099);
			Write(body, 4, 1);
			Write(body, 8, 1);        // version
			Write(body, 12, 0xDEAD);  // the word the count is not in
			Write(body, 16, 3);       // count

			Write(body, 20, 736354); Write(body, 24, 2); Write(body, 28, 1);
			Write(body, 32, 736354); Write(body, 36, 3); Write(body, 40, 1);
			Write(body, 44, 735116); Write(body, 48, 3); Write(body, 52, 6);

			var costs = RdbFeatImporter.ParsePerkCosts(body);

			Assert.Equal(3, costs.Count);
			Assert.Equal(736354u, costs[0].FeatClassEntryId);
			Assert.Equal(TokenPool.AltAdvancementPvPToken, costs[0].PointType);
			Assert.Equal(1u, costs[0].Cost);
			Assert.Equal(TokenPool.AltAdvancementPvEToken, costs[1].PointType);
			Assert.Equal(6u, costs[2].Cost);
		}

		// A pool the client does not switch on is a mis-read of the table, not an unnamed currency - so the row is
		// dropped rather than stored under a value nothing can mean.
		[Fact]
		public void ParsePerkCosts_DropsARowWithAnUnknownPool()
		{
			var body = new byte[20 + (2 * 12)];
			Write(body, 0, 1000099);
			Write(body, 16, 2);
			Write(body, 20, 736354); Write(body, 24, 9); Write(body, 28, 1); // 9 = WorldPvPToken, not an AA pool
			Write(body, 32, 736333); Write(body, 36, 2); Write(body, 40, 10);

			var costs = RdbFeatImporter.ParsePerkCosts(body);

			Assert.Equal(736333u, Assert.Single(costs).FeatClassEntryId);
		}

		private static void Write(byte[] body, int at, uint value)
		{
			BitConverter.GetBytes(value).CopyTo(body, at);
		}
	}
}
