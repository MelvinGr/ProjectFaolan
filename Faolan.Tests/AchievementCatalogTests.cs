using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// A tag (`GameCharInterface.DispatchAddTag`, one u32) is an achievement, and the client's own LDB names it -
	// category 55701 the title, 55700 what earns it. Without the catalog a granted achievement is a number.
	//
	// The gate that matters is the crib: every tag id the corpus was captured awarding must resolve, and resolve to
	// something that matches what that capture was doing. An id-keyed import is trivially self-consistent, so the only
	// way to know the keying is right is to check it against wire evidence the import never saw.
	public class AchievementCatalogTests
	{
		// The tags retail awarded in the corpus (23 awards over 14 captures), with what the capture was doing when it
		// landed. Hand-carried from `--report method-census --report-arg Tag` + `--report ldb-search --report-arg id:<n>`.
		[Theory]
		[InlineData(7917u, "Seeing Red", "Achieve a kill streak.")] // 6 captures - the most-awarded tag in the corpus
		[InlineData(716u, "Old Tarantia Docks", null)] // annotated_2 t+285.5, the instant the vista fly-through of #740 ended
		[InlineData(3509u, "Pet Collector", "Collect and summon your first pet, companion or mini-pet.")] // ..._login_summon_pet
		[InlineData(3546u, "Uncommon Pet", "Collect and summon a pet, companion or mini-pet of uncommon quality.")] // same instant
		[InlineData(2562u, "Spider Caverns", "Defeat the Lurking Horror in the Spider Caverns")] // awarded beside 2563
		[InlineData(2563u, "Lurking Horror", null)] // ...the boss half of the same pair
		[InlineData(3305u, "Rare Mount", "Collect and summon a rare quality mount.")]
		[InlineData(3286u, "Siege Mount", "Collect and summon a rhino or mammoth mount.")]
		public async Task ACapturedTagAward_ResolvesToTheAchievementItsCaptureWasEarning(uint tagId, string name, string? description)
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var achievement = await db.Achievements.AsNoTracking().FirstOrDefaultAsync(a => a.Id == tagId, TestContext.Current.CancellationToken);

			Assert.NotNull(achievement);
			Assert.Equal(name, achievement.Name);
			Assert.Equal(description, achievement.Description);
		}

		[Fact]
		public async Task TagsThatAreNotAchievements_AreAbsent_NotInvented()
		{
			// 6617 is the one captured tag id of fourteen that resolves to nothing in the achievement catalog - and this
			// codebase had already recovered it from the client's shop Lua as one of the item-shop "bundle shown"
			// `HasTag` gates (6617-6620, 7316, 7416-7421), so it is a tag that is not an achievement at all.
			//
			// So its absence is the correct state and worth pinning: an import that started inventing rows for
			// unresolvable ids would make the catalog useless as the thing that says which tags are achievements.
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			Assert.Null(await db.Achievements.AsNoTracking().FirstOrDefaultAsync(a => a.Id == 6617, ct));
			foreach (var shopFlag in new uint[] { 6618, 6619, 6620, 7316, 7416, 7421 })
				Assert.Null(await db.Achievements.AsNoTracking().FirstOrDefaultAsync(a => a.Id == shopFlag, ct));
		}

		[Fact]
		public async Task TheCatalogIsWholeAndDescriptionsAreSPARSE()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var rows = await db.Achievements.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);

			// 4,095 as imported. A big drop means the language selection broke (English pages only, by the client's own
			// language records) or a category id moved.
			Assert.InRange(rows.Count, 4000, 6000);
			Assert.All(rows, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));

			// About a quarter carry a description, and that is the client's data, not a partial import - so it is pinned
			// rather than asserted away. A run that suddenly filled them all would mean 55700 was being read for
			// something else.
			var described = rows.Count(a => a.Description != null);
			Assert.InRange(described, 900, 2000);
		}
	}
}
