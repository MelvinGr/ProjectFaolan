using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Spawn;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// SaveCharacterScaffold has to persist the character's feat specs, and has to do it without touching the feats
	// trained in them.
	//
	// TrySeedFreshRows builds a fresh character's single unnamed spec in memory, so a save that writes only Skills +
	// ScaffoldPairs discards it when the create session ends. The next connection re-loads the character with zero
	// spec rows, PlayerSpawnScaffoldRows.SpecsOf throws ("has no CharacterFeatSpec rows"), and the client sits on a
	// loading screen that never progresses. Every imported character carries exactly one spec, so a character with
	// none is a shape nothing downstream expects.
	public class SaveCharacterScaffoldSpecTests
	{
		private const uint CharacterId = 0x01000001;

		[Fact]
		public async Task AFreshCharactersSeededSpecIsPersisted()
		{
			var (repo, options, path) = NewDb();
			try
			{
				var character = new Character
				{
					Id = CharacterId,
					Name = "Fresh",
					Class = ConanClassOf(),
					Skills = []
				};

				Assert.True(PlayerSpawnScaffoldRows.TrySeedFreshRows(character));
				Assert.Single(character.FeatSpecs); // the seed builds it...

				await repo.SaveCharacterScaffold(character);

				// ...and the save is the only thing that can write it. Without this the character cannot be spawned.
				await using var ctx = new DatabaseContext(options);
				var specs = await ctx.CharacterFeatSpecs.Where(s => s.CharacterId == CharacterId).ToListAsync(TestContext.Current.CancellationToken);
				Assert.Single(specs);
				Assert.Equal(0, specs[0].Index);
				Assert.Equal("", specs[0].Name);

				// And the round-trip the crash came from now builds instead of throwing.
				var reloaded = await ctx.Characters.Include(c => c.FeatSpecs).Include(c => c.Feats).FirstAsync(c => c.Id == CharacterId, TestContext.Current.CancellationToken);
				Assert.Single(PlayerSpawnScaffoldRows.SpecsOf(reloaded));
			}
			finally
			{
				TestDb.Delete(path);
			}
		}

		[Fact]
		public async Task SavingTheScaffoldDoesNotDropTrainedFeats()
		{
			// CharacterFeat's FK is (CharacterId, SpecIndex), so delete+reinserting specs the way Skills and
			// ScaffoldPairs are handled would cascade every trained feat away. A scaffold save must never cost a
			// character its feats - hence ensure-exists rather than replace.
			var (repo, options, path) = NewDb();
			try
			{
				await repo.AddCharacterFeats(CharacterId, [123400u, 567800u]);

				await using (var seeded = new DatabaseContext(options))
					Assert.Equal(2, await seeded.CharacterFeats.CountAsync(f => f.CharacterId == CharacterId, TestContext.Current.CancellationToken));

				var character = new Character
				{
					Id = CharacterId,
					Name = "Trained",
					Class = ConanClassOf(),
					Skills = []
				};
				PlayerSpawnScaffoldRows.TrySeedFreshRows(character);
				await repo.SaveCharacterScaffold(character);

				await using var ctx = new DatabaseContext(options);
				Assert.Equal(2, await ctx.CharacterFeats.CountAsync(f => f.CharacterId == CharacterId, TestContext.Current.CancellationToken));
				Assert.Single(await ctx.CharacterFeatSpecs.Where(s => s.CharacterId == CharacterId).ToListAsync(TestContext.Current.CancellationToken)); // not duplicated either
			}
			finally
			{
				TestDb.Delete(path);
			}
		}

		private static Core.Enums.ConanClass ConanClassOf()
		{
			return Core.Enums.ConanClass.Assassin; // the class the live crash happened on
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
