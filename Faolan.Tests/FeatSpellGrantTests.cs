using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// Which feats may be put in a character's list at all. The list rides the player spawn's pre-IIR frame, so
	// an extra id is a wire divergence rather than a harmless surplus - and the client's own featclass table
	// (`FeatClassEntry`, RDB 1000043) says which side of the line each feat is on.
	//
	// The gate that proves this from the captured feat lists is `--report feat-autotrain-check`, not a test
	//: `faolan.sql` excludes every `Character*` table as per-developer play state, so the suite's
	// fixture holds no `CharacterFeats` at all and an assertion over them would pass on an empty left side.
	public class FeatSpellGrantTests
	{
		// The claim `FeatClassEntry.Autotrain`'s remark rests on: auto-training is a property of the feat, not of one
		// (class, level) row. If a feat were auto-trained for one class and trainable for another, "may this id be in
		// a feat list" would stop being answerable from the id alone - which is exactly how the level path asks it.
		[Fact]
		public async Task NoFeat_IsBothAutotrainedAndTrainable()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			Assert.NotEqual(0, await db.FeatClassEntries.AsNoTracking().CountAsync(ct)); // the table is the whole point
			var split = await db.FeatClassEntries.AsNoTracking()
				.GroupBy(e => e.FeatId)
				.Where(g => g.Min(e => e.Autotrain) != g.Max(e => e.Autotrain))
				.Select(g => g.Key)
				.ToListAsync(ct);

			Assert.True(split.Count == 0, "feats with both an auto-trained and a trainable row: " + string.Join(", ", split));
		}

		// Everything a server-side level-up grant would hand out - the `Spell Unlocking <lo>-<hi>` bands, the
		// `<Class> Class Feat NNN` tracks and `Pet Mastery` - is auto-trained in the client's own table, so the client
		// hands it over itself. Asserted by name, because the names are how that population is identified without the
		// flag.
		[Fact]
		public async Task EveryFeatTheLevelPathUsedToTrain_IsAutotrainedByTheClient()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var granted = await (from entry in db.FeatClassEntries.AsNoTracking()
				join map in db.FeatIdMaps.AsNoTracking() on entry.FeatId equals map.WireFeatId
				join feat in db.Feats.AsNoTracking() on map.TemplateId equals feat.Id
				where feat.Name.StartsWith("Spell Unlocking") || feat.Name.Contains("Class Feat") || feat.Name == "Pet Mastery"
				select new { feat.Name, entry.Autotrain }).ToListAsync(ct);

			Assert.NotEmpty(granted);
			var trainable = granted.Where(g => !g.Autotrain).Select(g => g.Name).Distinct().ToList();
			Assert.True(trainable.Count == 0, "expected auto-trained, but the client marks them trainable: " + string.Join(", ", trainable));
		}

		// The free set the budget check uses is the client's flag, not the 7063 category. It must still resolve to
		// something - an empty set would silently let a character overspend its budget.
		[Fact]
		public async Task TheAutotrainedSet_IsNotEmpty()
		{
			var repo = new DatabaseRepository(new Factory(TestDb.ReadOnly(TestDb.GameDatabase)));
			var autotrained = await repo.GetAutotrainedFeatWireIds();

			Assert.NotEmpty(autotrained);

			// ...and it is a strict subset: the trainable half is what the point budget exists to bind on.
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var all = await db.FeatClassEntries.AsNoTracking().Select(e => e.FeatId).Distinct().CountAsync(TestContext.Current.CancellationToken);
			Assert.True(autotrained.Count < all, $"every feat is auto-trained ({autotrained.Count} of {all}) - the flag cannot be selecting anything");
		}

		// The class progression tracks - bases whose low digits run past the client's own rank ceiling
		// (FeatIdMap.MaxRank, from FUN_007850c0's `base*100 + 10`), so the digits index a family instead of naming a
		// rank. The client's flag reaches them too, which is what makes an exclusion by name unnecessary rather than
		// merely agreed with on the easy cases.
		[Fact]
		public async Task EveryProgressionTrack_IsAutotrained()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var tracks = await TrackBases(db, ct);
			Assert.NotEmpty(tracks);
			Assert.NotEqual(0, await db.FeatClassEntries.AsNoTracking().CountAsync(ct)); // ...and the side being scanned

			var trainable = await (from entry in db.FeatClassEntries.AsNoTracking()
				join map in db.FeatIdMaps.AsNoTracking() on entry.FeatId equals map.WireFeatId
				where tracks.Contains(map.BaseIndex) && !entry.Autotrain
				select entry.FeatId).Distinct().ToListAsync(ct);

			Assert.True(trainable.Count == 0, "progression-track feats the client does NOT auto-train: " + string.Join(", ", trainable));
		}

		private static async Task<uint[]> TrackBases(DatabaseContext db, CancellationToken ct)
		{
			return await db.FeatIdMaps.AsNoTracking()
				.GroupBy(m => m.BaseIndex)
				.Where(g => g.Max(m => m.Rank) >= FeatIdMap.MaxRank)
				.Select(g => g.Key)
				.ToArrayAsync(ct);
		}

		private sealed class Factory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext()
			{
				return new DatabaseContext(options);
			}

			public Task<DatabaseContext> CreateDbContextAsync(CancellationToken ct = default)
			{
				return Task.FromResult(new DatabaseContext(options));
			}
		}
	}
}
