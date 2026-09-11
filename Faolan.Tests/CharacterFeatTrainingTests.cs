using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// AddCharacterFeats keeps one row per feat base - the highest rank held.
	//
	// A feat id is composite (base*100 + rank) and retail stores only the current rank: no captured character carries
	// two ranks of one base. Deduping on the exact id instead would leave both rows behind on a rank-up, putting an
	// entry on the spawn's feat list that retail never sends and lengthening the pre-IIR frame with it.
	//
	// Runs against a real throwaway SQLite DB rather than a fake, because the rank-up is a delete + INSERT (FeatId is
	// part of the primary key) and that is exactly the part worth exercising for real.
	public class CharacterFeatTrainingTests
	{
		[Fact]
		public async Task RankingAFeatUp_ReplacesTheRow_AndKeepsItsPlaceInTheList()
		{
			var (repo, options, path) = NewDb();
			try
			{
				// Two feats trained in order, then the first is ranked up. It must not become a third entry, and it
				// must not jump to the end of the list - the wire order is train order.
				await repo.AddCharacterFeats(CharacterId, [80001u, 90000u]);
				await repo.AddCharacterFeats(CharacterId, [80004u]);

				await using var ctx = new DatabaseContext(options);
				var rows = await ctx.CharacterFeats.Where(f => f.CharacterId == CharacterId).OrderBy(f => f.Sequence).ToListAsync(TestContext.Current.CancellationToken);

				Assert.Equal([80004u, 90000u], rows.Select(f => f.FeatId));
				Assert.Equal([0, 1], rows.Select(f => f.Sequence));
			}
			finally
			{
				Cleanup(path);
			}
		}

		[Fact]
		public async Task TrainingALowerRank_IsIgnored()
		{
			var (repo, options, path) = NewDb();
			try
			{
				await repo.AddCharacterFeats(CharacterId, [80004u]);
				var added = await repo.AddCharacterFeats(CharacterId, [80002u, 80004u]);

				await using var ctx = new DatabaseContext(options);
				var rows = await ctx.CharacterFeats.Where(f => f.CharacterId == CharacterId).ToListAsync(TestContext.Current.CancellationToken);

				Assert.Equal(0, added);
				Assert.Equal([80004u], rows.Select(f => f.FeatId));
			}
			finally
			{
				Cleanup(path);
			}
		}

		// Distinct bases are unrelated - the replacement must key on the base, not on proximity of the ids.
		[Fact]
		public async Task DistinctBases_EachKeepTheirOwnRow()
		{
			var (repo, options, path) = NewDb();
			try
			{
				await repo.AddCharacterFeats(CharacterId, [80004u, 80104u, 80200u]);

				await using var ctx = new DatabaseContext(options);
				var rows = await ctx.CharacterFeats.Where(f => f.CharacterId == CharacterId).OrderBy(f => f.Sequence).ToListAsync(TestContext.Current.CancellationToken);

				Assert.Equal([80004u, 80104u, 80200u], rows.Select(f => f.FeatId));
			}
			finally
			{
				Cleanup(path);
			}
		}

		private const uint CharacterId = 0x01000001;

		private static void Cleanup(string path)
		{
			TestDb.Delete(path);
		}

		private static (DatabaseRepository Repo, DbContextOptions<DatabaseContext> Options, string Path) NewDb()
		{
			var (options, path) = TestDb.NewSeededDatabase(CharacterId);
			return (new DatabaseRepository(new Factory(options)), options, path);
		}

		private sealed class Factory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext()
			{
				return new DatabaseContext(options);
			}
		}
	}
}
