using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Progression;
using Faolan.GameServer.Economy;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// #735: the tradeskill PROFESSION and RANK model, and the recipe book it gates.
	//
	// The model is the client's own and nothing here is editorial except which chains are professions: a profession is
	// a linear chain in `FeatPrereqs` (each rank requires the one below), and every shipped recipe names the rung it
	// needs in `RequiredFeatId`. These gates assert the chains reproduce from the shipped feat table, that the recipe
	// set is fully covered by them, and that the book a rank buys is the one the requirements state.
	public class TradeskillProfessionTests : IDisposable
	{
		// The eleven chains walk out of the shipped prerequisite edges, seven ranks each.
		[Fact]
		public async Task TheElevenProfessionLadders_WalkOutOfTheShippedPrereqEdges()
		{
			var ladders = await Ladders();

			// ELEVEN, which is the client's own count: five crafting and six gathering.
			Assert.Equal(11, ladders.Length);
			Assert.All(ladders, l => Assert.Equal(TradeskillLadders.RankNames.Length, l.RankFeatIds.Length));
			Assert.Equal(
				[
					"Weaponsmith", "Armorsmith", "Alchemy", "Architect", "Gemcutter",
					"Woodcutting", "Mining", "Prospecting", "Stonecutting", "Weaver", "Skinner"
				],
				ladders.Select(l => l.Name));

			// The Weaponsmith chain, spelled out - it is the one the recipe set leans on hardest.
			var weaponsmith = ladders.Single(l => l.Name == "Weaponsmith");
			Assert.Equal([321000u, 321001u, 321100u, 321101u, 321200u, 321201u, 321202u], weaponsmith.RankFeatIds);
		}

		// ⚠️ The load-bearing one: ALL SIX of Gemcutter's trained ranks are NAMED "Gemcutter Novice" in the shipped
		// feat table. A rank read off the name would give that profession seven Novices and no ladder at all; read off
		// the chain DEPTH it is a seven-rung ladder like every other.
		[Fact]
		public async Task GemcuttersRanksAreAllNamedNovice_AndTheLadderIsIntactAnyway()
		{
			var ladders = await Ladders();
			var gemcutter = ladders.Single(l => l.Name == "Gemcutter");

			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var names = await (from wire in gemcutter.RankFeatIds.ToAsyncEnumerable()
				select wire).ToListAsync(TestContext.Current.CancellationToken);

			var featNames = await (from map in db.FeatIdMaps.AsNoTracking()
				join feat in db.Feats.AsNoTracking() on map.TemplateId equals feat.Id
				where names.Contains(map.WireFeatId)
				select feat.Name).ToListAsync(TestContext.Current.CancellationToken);

			Assert.Equal(7, gemcutter.RankFeatIds.Length);
			Assert.Equal(7, featNames.Count);
			Assert.All(featNames.Take(6), n => Assert.Equal("Gemcutter Novice", n)); // ...and so is the Grandmaster row
		}

		// Every shipped recipe's requirement sits on one of the ladders. If a recipe requirement fell off them the
		// gate would silently ALLOW it (HasProfessionFor's deliberate default), so this is what keeps that default
		// from becoming a hole.
		[Fact]
		public async Task EveryShippedRecipesRequirement_SitsOnALadder()
		{
			var ladders = await Ladders();

			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var required = await db.Recipes.AsNoTracking().Select(r => r.RequiredFeatId).Distinct().ToListAsync(TestContext.Current.CancellationToken);

			Assert.NotEmpty(required);
			Assert.All(required, feat => Assert.Contains(ladders, l => l.RankOfFeat(feat) != null));

			// ...and only two professions are represented, which is the finding the book itself carries: the 603 rows
			// were harvested from ONE character's TradeskillReplyIIR, so they are that character's professions.
			var professions = required.Select(f => ladders.Single(l => l.RankOfFeat(f) != null).Name).Distinct().Order().ToList();
			Assert.Equal(["Armorsmith", "Weaponsmith"], professions);
		}

		// ⚠️ A character holds SEVERAL rungs of one ladder, not one: AddCharacterFeats keeps the highest rank per
		// `id / 100` base and a ladder spans three bases. So a Weaponsmith Grandmaster holds 321001 / 321101 / 321202
		// and holds NEITHER 321000 (Novice) NOR 321201 (Master) - a set-membership test against the recipe's own
		// RequiredFeatId would hide most of their book from them.
		[Fact]
		public async Task AGrandmastersStoredFeats_ResolveToTheTopRank_ThoughTheyHoldNeitherEnd()
		{
			var weaponsmith = (await Ladders()).Single(l => l.Name == "Weaponsmith");
			var stored = new HashSet<uint> { 321001, 321101, 321202 }; // what AddCharacterFeats collapses the chain to

			Assert.DoesNotContain(321000u, stored); // Novice - the rung 9 recipes name
			Assert.DoesNotContain(321201u, stored); // Master
			Assert.Equal(6, TradeskillLadders.RankOf(weaponsmith, stored));
		}

		// ⭐ Retail's own record of a learned tradeskill is the spawn scaffold's skill list, and it carries the RANK:
		// `CharacterSkills.SkillId` IS a wire feat id. The two imported L80s resolve to a coherent set of professions
		// at plausibly different ranks - which is what says the id, and not the slot, is the thing to read.
		//
		// ⚠️ Two of the slots carry ids that are NOT professions on BOTH characters (`328908` Expert Bandaging,
		// `326000` Destiny Quest). Resolving against the ladders drops them; reading the slot positionally files them
		// under a profession and makes the rank look identical across characters - which is how the tier came to be
		// recorded as absent from this table.
		//
		// The two id sets below are the `CharacterSkills.SkillId` lists of the two imported L80s, verbatim. They are
		// literals because `faolan.sql` excludes characters (per-developer play state), so the rows are not in the
		// test fixture - the ids ARE the evidence and carry their own provenance.
		[Theory]
		// NecroL80's scaffold: everything at Master, and Armorsmith at Grandmaster.
		[InlineData(new uint[] { 321502, 323205, 323505, 327505, 328908, 326000, 323305, 323405 }, 6, 5)]
		// DemoL80's: a mixed set, and Skinner/Mining only just started.
		[InlineData(new uint[] { 323201, 322402, 323501, 327500, 328908, 326000, 323300, 323402 }, 6, 0)]
		public async Task AnImportedCharactersScaffold_CarriesTheirProfessionsAndRanks(uint[] scaffold, int professions, int miningRank)
		{
			var ladders = await Ladders();
			var held = scaffold.ToHashSet();

			Assert.Equal(professions, ladders.Count(l => TradeskillLadders.RankOf(l, held) != null));
			Assert.Equal(miningRank, TradeskillLadders.RankOf(ladders.Single(l => l.Name == "Mining"), held));

			// ⚠️ `328908` and `326000` are on BOTH characters and are NOT professions - they resolve to `Expert
			// Bandaging` and `Destiny Quest`. Resolving against the ladders drops them; reading the SLOT they sit in
			// positionally files them under a profession, and because the pair is identical across characters that is
			// what made the tier look absent from this table.
			Assert.All((uint[]) [328908, 326000], id => Assert.All(ladders, l => Assert.Null(l.RankOfFeat(id))));
		}

		[Fact]
		public async Task AnUntrainedCharacter_ResolvesToNoProfessionAtAll()
		{
			var weaponsmith = (await Ladders()).Single(l => l.Name == "Weaponsmith");

			Assert.Null(TradeskillLadders.RankOf(weaponsmith, new HashSet<uint>()));
			Assert.Null(TradeskillLadders.RankOf(weaponsmith, new HashSet<uint> { 80004, 1440500 })); // combat feats
		}

		// The book a rank buys, against the shipped requirements. A Weaponsmith Adept sees the Novice, Apprentice and
		// Adept rows (9 + 18 + 18) and nothing of Armorsmith at all.
		[Fact]
		public async Task TheBookIsExactlyWhatTheRankBuys()
		{
			var (service, characterId) = await Trained("Weaponsmith", "Adept");

			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var catalog = await db.Recipes.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);

			var book = await service.BookFor(characterId, catalog);

			Assert.Equal(45, book.Count);
			Assert.All(book, r => Assert.Contains(r.RequiredFeatId, (uint[]) [321000, 321001, 321100]));
		}

		// A character who has trained nothing gets an EMPTY book - retail, per the ticket: the book is what a
		// profession buys, so no profession means no book rather than a book that cannot be used.
		[Fact]
		public async Task AnUntrainedCharacterGetsNoBookAndCannotCraft()
		{
			var (service, characterId) = await Trained(null, null);

			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var catalog = await db.Recipes.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);

			Assert.Empty(await service.BookFor(characterId, catalog));
			Assert.False(await service.HasProfessionFor(characterId, catalog[0]));
		}

		// Training the whole way up opens the whole of that profession, and still none of the other one.
		[Fact]
		public async Task AGrandmasterSeesTheirWholeProfessionAndNoneOfTheOther()
		{
			var (service, characterId) = await Trained("Armorsmith", "Grandmaster");

			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var catalog = await db.Recipes.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);

			var book = await service.BookFor(characterId, catalog);

			Assert.Equal(504, book.Count);
			Assert.Equal(603, catalog.Count); // the rest is Weaponsmith, which they have not trained
		}

		private static async Task<System.Collections.Immutable.ImmutableArray<TradeskillLadder>> Ladders()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var edges = await db.FeatPrereqs.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);

			return TradeskillLadders.Build(edges.GroupBy(p => p.WireFeatId).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First().RequiresWireFeatId));
		}

		// A TradeskillService over a throwaway copy of the shipped database, with the character trained to the named
		// rank through the same AddCharacterFeats the GM command uses - so the stored rows are the collapsed set, not
		// an idealised one.
		private async Task<(TradeskillService Service, uint CharacterId)> Trained(string? profession, string? rank)
		{
			// ⚠️ A WRITABLE COPY of the shipped database, not `NewSeededDatabase` - that builds a bare schema with no
			// game content, so the prerequisite graph would be EMPTY and every ladder one rung deep. The ladders are
			// the thing under test; they have to come from the real feat table.
			var path = Path.Combine(Path.GetTempPath(), $"faolan-tradeskill-{Path.GetRandomFileName()}.db");
			File.Copy(TestDb.GameDatabase, path);
			_databases.Add(path);

			var options = TestDb.Options(path);
			// The character is added straight in rather than through TestDb.SeedCharacter: the shipped database already
			// carries the default account, realm and maps that helper inserts, and re-inserting them collides. Its own
			// account and realm are read back rather than assumed - the seeded account id is a generated instance, not 1.
			await using (var seed = new DatabaseContext(options))
			{
				seed.Characters.Add(new Character
				{
					Id = Character,
					AccountId = await seed.Accounts.Select(a => a.Id).FirstAsync(TestContext.Current.CancellationToken),
					RealmId = await seed.Realms.Select(r => r.Id).FirstAsync(TestContext.Current.CancellationToken),
					MapId = Map.TortageBeachId,
					Name = "Crafter", Class = ConanClass.Barbarian, Race = Race.Cimmerian, Sex = Sex.Male
				});
				await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
			}

			var database = new DatabaseRepository(new Factory(options));
			var service = new TradeskillService(database, null!, NullLogger.Instance);

			if (profession != null && rank != null)
			{
				var ladder = (await service.Ladders()).Single(l => l.Name == profession);
				await database.AddCharacterFeats(Character, ladder.RankFeatIds.Take(TradeskillLadders.RankNames.IndexOf(rank) + 1));
			}

			return (service, Character);
		}

		private const uint Character = 0x0802E5D4;

		// The throwaway copies this test made; deleted when it finishes.
		private readonly List<string> _databases = [];

		public void Dispose()
		{
			foreach (var path in _databases)
			{
				try
				{
					File.Delete(path);
				}
				catch (IOException)
				{
					// A copy left behind in the temp dir is harmless; failing the test over it would not be.
				}
			}
		}

		private sealed class Factory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext() => new DatabaseContext(options);
			public Task<DatabaseContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new DatabaseContext(options));
		}
	}
}
