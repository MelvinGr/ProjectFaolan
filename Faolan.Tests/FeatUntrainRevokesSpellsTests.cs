using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.GameServer.Feats;
using Faolan.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Faolan.Tests
{
	// #920: untraining a feat takes back the spell that feat TAUGHT.
	//
	// `GrantSpellsTaughtBy` hands a spell over when a feat that teaches one is trained (#847), and nothing did the
	// reverse - `ResetFeats` cleared the rows and world entry re-granted the whole `CharacterSpells` set, so a
	// tree-bought spell survived the untrain of the feat that bought it for ever.
	//
	// ⚠️ The ticket's stated blocker was that revoking would also strip the character's LEVEL-earned spells,
	// because `ClearCharacterFeats` wipes the automatic feats too. Both halves of that are checked below against
	// the shipped data rather than argued: the automatic grants are excluded by the query itself, and the level
	// path's own set is the guard on what is left.
	public class FeatUntrainRevokesSpellsTests
	{
		private const uint AutomaticUnlockCategory = 7063;

		// ⛔ The first half of the blocker's answer, over the shipped catalog: `GetSpellsTaughtByFeats` filters
		// `TemplateCategory != AutomaticUnlockCategory` in its own `where`, so the half of the grant table that
		// carries what a DING gives can never reach the revoke - whatever ends up in `CharacterFeat`.
		//
		// The population matters: 798 of the 1,548 grants ARE automatic, so a query that had forgotten the filter
		// would be visibly wrong here rather than vacuously right.
		[Fact]
		public async Task SpellsTaughtByFeats_NeverIncludesAnAutomaticGrant()
		{
			Assert.True(Scalar($"SELECT COUNT(*) FROM FeatSpellGrants g JOIN Feats f ON f.Id = g.FeatId WHERE f.TemplateCategory = {AutomaticUnlockCategory}") > 700,
				"the automatic half of the grant table is missing - this test is vacuous without it");

			var repo = new DatabaseRepository(new TestDb.ContextFactory(TestDb.ReadOnly(TestPaths.FaolanDb)));

			// Every wire feat id that maps to an AUTOMATIC grant. None of them may contribute a spell.
			var automaticWireIds = Query($@"
				SELECT DISTINCT m.WireFeatId FROM FeatIdMaps m
				JOIN FeatSpellGrants g ON g.FeatId = m.TemplateId
				JOIN Feats f ON f.Id = g.FeatId
				WHERE f.TemplateCategory = {AutomaticUnlockCategory} LIMIT 200");

			Assert.NotEmpty(automaticWireIds);
			Assert.Empty(await repo.GetSpellsTaughtByFeats(automaticWireIds));
		}

		// …and the TREE half does come back, so the revoke has something to act on. Same call, the other category.
		[Fact]
		public async Task SpellsTaughtByFeats_ReturnsTheTreeGrants()
		{
			var repo = new DatabaseRepository(new TestDb.ContextFactory(TestDb.ReadOnly(TestPaths.FaolanDb)));

			var treeWireIds = Query($@"
				SELECT DISTINCT m.WireFeatId FROM FeatIdMaps m
				JOIN FeatSpellGrants g ON g.FeatId = m.TemplateId
				JOIN Feats f ON f.Id = g.FeatId
				WHERE f.TemplateCategory <> {AutomaticUnlockCategory} LIMIT 200");

			Assert.NotEmpty(treeWireIds);
			Assert.NotEmpty(await repo.GetSpellsTaughtByFeats(treeWireIds));
		}

		// ⭐ The rule itself: a spell the character's LEVEL also grants is KEPT. Retail's untrain-all refunds spent
		// points; it does not un-grant what levelling handed over, and the two paths can name the same spell - a
		// tree feat teaching something the class ladder hands out anyway.
		[Fact]
		public void ASpellTheLevelPathAlsoGrants_IsNotRevoked()
		{
			var taught = new uint[] { 100, 200, 300 };

			Assert.Equal([100u, 300u], FeatService.SpellsToRevoke(taught, new[] { 200u }.ToHashSet()));
		}

		[Fact]
		public void WithNothingGrantedByLevel_EverythingTaughtComesBack()
		{
			var taught = new uint[] { 100, 200 };

			Assert.Equal(taught, FeatService.SpellsToRevoke(taught, new System.Collections.Generic.HashSet<uint>()));
		}

		[Fact]
		public void WhenTheLevelPathGrantsThemAll_NothingIsRevoked()
		{
			Assert.Empty(FeatService.SpellsToRevoke([100, 200], new[] { 100u, 200u, 300u }.ToHashSet()));
		}

		private static uint[] Query(string sql)
		{
			using var connection = new SqliteConnection($"Data Source={TestPaths.FaolanDb};Mode=ReadOnly");
			connection.Open();
			using var command = connection.CreateCommand();
			command.CommandText = sql;
			using var reader = command.ExecuteReader();

			var ids = new System.Collections.Generic.List<uint>();
			while (reader.Read())
				ids.Add((uint)reader.GetInt64(0));

			return [.. ids];
		}

		private static uint Scalar(string sql)
		{
			using var connection = new SqliteConnection($"Data Source={TestPaths.FaolanDb};Mode=ReadOnly");
			connection.Open();
			using var command = connection.CreateCommand();
			command.CommandText = sql;

			return (uint)(long)command.ExecuteScalar()!;
		}
	}
}
