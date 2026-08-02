using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #585: two identical rings are TWO occupied slots, and the stat they grant has to count twice.
	//
	// `GetEquippedStatItems` built a HashSet of the worn item ids and returned one Item per DISTINCT id - contradicting
	// its own comment ("Preserve one entry per occupied slot (duplicates included) so a stat granted by two worn copies
	// stacks") and the interface doc. Everything downstream reads the aggregate: max health/stamina/mana, the five
	// attribute scores, and - since #580 put the attributes INTO the pool curve - the attribute-scaled part of every
	// pool as well. So a character wearing two of the same ring was short on all of it, silently.
	//
	// The distinct set is still what the query takes: `IN (...)` cannot use a duplicate. The fix is that the RESULT is
	// rehydrated from the original per-slot sequence, not from the set.
	public class EquippedStatItemsDuplicateTests
	{
		[Fact]
		public async Task TwoIdenticalRings_AreReturnedTwice_SoTheirStatStacks()
		{
			// A WRITABLE copy of the shipped fixture rather than a bare EnsureCreated: Character hangs off Account,
			// Realm and Map, and the fixture already carries the seeded realm/account and the map rows.
			var path = Path.Combine(Path.GetTempPath(), $"faolan-equip-dupes-{System.Guid.NewGuid():N}.db");
			File.Copy(TestDb.GameDatabase, path);
			try
			{
				var options = TestDb.Options(path);
				await using (var ctx = new DatabaseContext(options))
				{
					// CharacterEquipment.CharacterId FKs Character, so the owner has to exist.
					var realm = await ctx.Realms.Select(r => r.Id).FirstAsync(TestContext.Current.CancellationToken);
					var account = await ctx.Accounts.Select(a => a.Id).FirstAsync(TestContext.Current.CancellationToken);
					ctx.Characters.Add(new Character
					{
						Id = 7, Name = "Dupetest", Class = ConanClass.Assassin, Level = 20, Race = Race.Stygian,
						Sex = Sex.Female, RealmId = realm, AccountId = account
					});
					ctx.Items.Add(new Item { Id = 4242, Name = "Ring of the Same" });
					ctx.Items.Add(new Item { Id = 4243, Name = "A Different Ring" });
					ctx.CharacterEquipment.Add(new CharacterEquipment
					{
						CharacterId = 7,
						RingLeftId = 4242,
						RingRightId = 4242, // the same template in both ring slots
						NeckId = 4243
					});
					await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
				}

				var repo = new DatabaseRepository(new TestContextFactory(options));
				var worn = await repo.GetEquippedStatItems(7, WeaponSet.Primary);

				Assert.Equal(3, worn.Length);
				Assert.Equal(2, worn.Count(i => i.Id == 4242)); // both slots, not one
				Assert.Single(worn, i => i.Id == 4243);
			}
			finally
			{
				TestDb.Delete(path);
			}
		}

		private sealed class TestContextFactory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext()
			{
				return new DatabaseContext(options);
			}

			public Task<DatabaseContext> CreateDbContextAsync(System.Threading.CancellationToken ct = default)
			{
				return Task.FromResult(new DatabaseContext(options));
			}
		}
	}
}
