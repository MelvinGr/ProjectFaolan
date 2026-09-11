using System;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The universal combo kit in the shipped data: the 4 playable races' dance emotes, granted to every class.
	//
	// They are named by race (Cimmerian_/Khitan_/Stygian_/Aquilonian_Dance), which is exactly why the class-name-prefix
	// derivation could never produce them and they reached nobody: `Combos` had all 12 but `ClassCombos` had none, so
	// `GetClassCombos` returned none and `GrantCharacterCombos` emitted none. The corpus settles the grant rule -
	// 19 characters spanning every class each receive the same 12, i.e. all four races' dances - so this asserts the
	// shipped ClassCombo rows say the same thing, and that nothing class-specific leaked in with them (which is what
	// a union across create dumps instead of an intersection would do: the Ranger's own combos handed to everyone).
	public class UniversalComboKitTests
	{
		private static readonly string[] ExpectedUniversal =
		[
			"Aquilonian_Dance_combo1", "Aquilonian_Dance_combo2", "Aquilonian_Dance_combo3",
			"Cimmerian_Dance_combo1", "Cimmerian_Dance_combo2", "Cimmerian_Dance_combo3",
			"Khitan_Dance_combo1", "Khitan_Dance_combo2", "Khitan_Dance_combo3",
			"Stygian_Dance_combo1", "Stygian_Dance_combo2", "Stygian_Dance_combo3"
		];

		[Fact]
		public async Task EveryClass_GrantsTheDanceEmotes_AtLevelOne()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));

			var dances = await db.Combos.Where(c => c.Name.Contains("_Dance_combo")).Select(c => c.Id).ToListAsync(TestContext.Current.CancellationToken);
			Assert.Equal(12, dances.Count);

			foreach (var cls in Enum.GetValues<ConanClass>())
			{
				// Level 0, so a freshly created character qualifies for all of them.
				var granted = await db.ClassCombos.Where(cc => cc.Class == cls && cc.Level <= 1 && dances.Contains(cc.ComboId)).Select(cc => cc.ComboId).ToListAsync(TestContext.Current.CancellationToken);
				Assert.Equal(12, granted.Count);
			}
		}

		[Fact]
		public async Task TheKitGrantedToEveryClass_IsExactlyTheDanceEmotes()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));

			var classCount = Enum.GetValues<ConanClass>().Length;
			var universal = await db
				.ClassCombos.GroupBy(cc => cc.ComboId)
				.Where(g => g.Count() == classCount)
				.Select(g => g.Key)
				.ToListAsync(TestContext.Current.CancellationToken);

			var names = await db.Combos.Where(c => universal.Contains(c.Id)).Select(c => c.Name).OrderBy(n => n).ToListAsync(TestContext.Current.CancellationToken);

			Assert.Equal(ExpectedUniversal, names);
		}
	}
}
