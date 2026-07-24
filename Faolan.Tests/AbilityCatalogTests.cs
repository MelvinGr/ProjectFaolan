using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The ability catalog (#303). It has two tiers and the distinction is load-bearing:
	//
	//  - **grantable** rows, harvested from captured 0x2052 grants, which carry the ability's 128-bit TEA key;
	//  - **identity-only** rows, catalogued from the client's own data (an RDB 1000020 template named in LDB cat 50210
	//    and not item-named), which have a name and nothing else.
	//
	// The keys are random, so an ability nobody was ever seen being granted CANNOT be granted - the 0x2052 body is
	// built around that key. `Ability.IsGrantable` is what the grant path checks; these tests pin that a key-less row
	// reads as not-grantable and that the shipped data still carries every captured ability's key.
	public class AbilityCatalogTests
	{
		[Fact]
		public async Task EveryAbility_HasAName()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));

			var nameless = await db.Abilities.CountAsync(a => a.Name == null || a.Name == "", TestContext.Current.CancellationToken);
			Assert.Equal(0, nameless);
		}

		[Fact]
		public void IdentityOnlyRow_IsNotGrantable()
		{
			// What RdbLdbAbilityImporter writes: an id and a name, no key.
			Assert.False(new Ability { Id = 117809, Name = "Defensive Stance" }.IsGrantable);
		}

		[Fact]
		public async Task TheCatalog_IsTheWholeClientSet_AndOnlyCapturedOnesAreGrantable()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));

			var all = await db.Abilities.ToListAsync(TestContext.Current.CancellationToken);
			Assert.Equal(265, all.Count); // the client's full set: 1000020 templates named in LDB cat 50210, not item-named

			// Only the captured ones carry a key - and the grantable set must be exactly those, never a name-only row.
			var grantable = all.Where(a => a.IsGrantable).ToList();
			Assert.Equal(27, grantable.Count);
			Assert.All(grantable, a => Assert.NotEqual(0u, a.Slot));
		}

		[Fact]
		public void KeyedRow_IsGrantable()
		{
			var ability = new Ability { Id = 769094, Name = "Upper Left Attack", Slot = 108 };
			ability.Key.Key1 = 0xC0EBA341;

			Assert.True(ability.IsGrantable);
		}
	}
}
