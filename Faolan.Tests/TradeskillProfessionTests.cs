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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// The tradeskill profession and rank model, and the recipe book it gates.
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

			// Eleven, which is the client's own count: five crafting and six gathering.
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

		// The load-bearing one: the feat template's authoring name for all six of Gemcutter's trained ranks is
		// "Gemcutter Novice", so a rank read off that name gives the profession seven Novices and no ladder. The ladder
		// is read off the chain depth instead, and stays a seven-rung one whatever the names say. The names themselves
		// are the client's own LDB 50220 labels, which name each crafting tier - the one place the two sources
		// genuinely disagree, and the displayed one wins - so the ladder reads as a ladder by name too.
		[Fact]
		public async Task GemcuttersRanks_ReadOffTheChainDepth_AndCarryTheClientsTierNames()
		{
			var ladders = await Ladders();
			var gemcutter = ladders.Single(l => l.Name == "Gemcutter");
			Assert.Equal(7, gemcutter.RankFeatIds.Length);

			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var wireIds = gemcutter.RankFeatIds.ToList();
			var nameByWire = await (from map in db.FeatIdMaps.AsNoTracking()
				join feat in db.Feats.AsNoTracking() on map.TemplateId equals feat.Id
				where wireIds.Contains(map.WireFeatId)
				select new { map.WireFeatId, feat.Name }).ToDictionaryAsync(x => x.WireFeatId, x => x.Name, TestContext.Current.CancellationToken);

			Assert.Equal(
				[
					"Gemcutter Novice", "Gemcutter Apprentice", "Gemcutter Adept", "Gemcutter Artisan",
					"Gemcutter Expert", "Gemcutter Master", "Gemcutter Grandmaster"
				],
				gemcutter.RankFeatIds.Select(id => nameByWire[id]));
		}

		// Every shipped recipe's requirement sits on one of the ladders. If a recipe requirement fell off them the
		// gate would silently allow it (HasProfessionFor's deliberate default), so this is what keeps that default
		// from becoming a hole.
		[Fact]
		public async Task EveryShippedRecipesRequirement_SitsOnALadder()
		{
			var ladders = await Ladders();

			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var required = await db.Recipes.AsNoTracking().Select(r => r.RequiredFeatId).Distinct().ToListAsync(TestContext.Current.CancellationToken);

			Assert.NotEmpty(required);
			Assert.All(required, feat => Assert.Contains(ladders, l => l.RankOfFeat(feat) != null));

			// ...and the professions represented are exactly the ones the captured books belong to, which is the
			// finding a shipped recipe set carries: rows are harvested per character, so the catalog is the union of
			// the professions the captured characters had trained: NecroL80's 603-row book contributes Armorsmith +
			// Weaponsmith, and DemoL80's brings its own two.
			var professions = required.Select(f => ladders.Single(l => l.RankOfFeat(f) != null).Name).Distinct().Order().ToList();
			Assert.Equal(["Alchemy", "Armorsmith", "Gemcutter", "Weaponsmith"], professions);
		}

		// A character holds several rungs of one ladder, not one: AddCharacterFeats keeps the highest rank per
		// `id / 100` base and a ladder spans three bases. So a Weaponsmith Grandmaster holds 321001 / 321101 / 321202
		// and holds neither 321000 (Novice) nor 321201 (Master) - a set-membership test against the recipe's own
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

		// Retail's own record of a learned tradeskill is the spawn scaffold's skill list, and it carries the rank:
		// `CharacterSkills.SkillId` is a wire feat id. The two imported L80s resolve to a coherent set of professions
		// at plausibly different ranks - which is what says the id, and not the slot, is the thing to read.
		//
		// Two of the slots carry ids that are not professions on both characters (`328908` Expert Bandaging,
		// `326000` Destiny Quest). Resolving against the ladders drops them; reading the slot positionally files them
		// under a profession and makes the rank look identical across characters - which is how the tier came to be
		// recorded as absent from this table.
		//
		// The two id sets below are the `CharacterSkills.SkillId` lists of the two imported L80s, verbatim. They are
		// literals because `faolan.sql` excludes characters (per-developer play state), so the rows are not in the
		// test fixture - the ids are the evidence and carry their own provenance.
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

			// `328908` and `326000` are on both characters and are not professions - they resolve to `Expert
			// Bandaging` and `Destiny Quest`. Resolving against the ladders drops them; reading the slot they sit in
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

		// A character who has trained nothing gets an empty book - retail, per the ticket: the book is what a
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
			Assert.Equal(708, catalog.Count); // the rest is the other three professions, which they have not trained
		}

		// The gathering gate. Retail refused a Stonecutting apprentice a Marble node and named `Stonecutting
		// Adept` (feat template id 894305) as what was missing, so the rank one below the requirement must refuse and
		// the requirement itself must pass. The template -> wire hop is the point: 894305 is not a wire feat id and a
		// gate that compared it against the character's own feat list would pass everybody.
		[Theory]
		[InlineData("Apprentice", false)] // exactly the capture: the rank the refused player held
		[InlineData("Novice", false)]
		[InlineData("Adept", true)]
		[InlineData("Grandmaster", true)]
		public async Task TheGatheringGate_RefusesBelowTheNamedRank(string rank, bool allowed)
		{
			var (service, characterId) = await Trained("Stonecutting", rank);
			var marble = HarvestCatalog.Nodes.Values.Single(n => n.Name == "Marble");

			Assert.Equal(894305u, marble.RequiredFeatId);
			Assert.Equal(allowed, await service.HasReachedFeat(characterId, marble.RequiredFeatId!.Value));
		}

		// A character who has trained no gathering profession at all is refused, which is the whole point of the gate -
		// and every other node stays ungated, because the corpus states one requirement and only one.
		[Fact]
		public async Task AnUntrainedCharacterIsRefusedTheOneGatedNode_AndNothingElseIsGated()
		{
			var (service, characterId) = await Trained(null, null);

			Assert.False(await service.HasReachedFeat(characterId, 894305));
			Assert.Single(HarvestCatalog.Nodes.Values, n => n.RequiredFeatId != null);
		}

		// The benefit-of-the-doubt default, stated so it cannot be lost: a requirement naming a feat that is on no
		// ladder we walked is allowed. That is a gap in the recovered graph, not a statement about the character.
		[Fact]
		public async Task ARequirementOffEveryLadder_IsAllowed()
		{
			var (service, characterId) = await Trained(null, null);

			Assert.True(await service.HasReachedFeat(characterId, 4242424)); // no such feat template
			Assert.True(await service.HasReachedFeat(characterId, 328908)); // Expert Bandaging - a real feat, no ladder
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
			// A writable copy of the shipped database, not `NewSeededDatabase` - that builds a bare schema with no
			// game content, so the prerequisite graph would be empty and every ladder one rung deep. The ladders are
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
			var service = new TradeskillService(database, database, database, null!, null!, null!, NullLogger.Instance);

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
