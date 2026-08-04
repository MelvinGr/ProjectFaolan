using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #581/#296: which class gets which combo comes from the stacking line of the SPELL the combo drives - a combo's
	// `GameActionId` is a spell id, and a class-encoded `GuiStackingLine` names the class.
	//
	// What is worth gating is not that the rule works but that it is APPLIED NARROWLY, because the obvious reading of
	// it is wrong. The archetype-shared combos sit in a low block that reads as class 20 and is not one, and taking
	// it at face value files every Rogue and Priest archetype combo under Guardian.
	public class ClassComboMembershipTests
	{
		// The archetype-shared block: three different archetypes under the same leading "20", which is what proves
		// the number is a block rather than a class.
		[Theory]
		[InlineData("Soldier_", 20002u, 20005u)]
		[InlineData("Rogue_", 20006u, 20007u)]
		[InlineData("Priest_", 20008u, 20008u)]
		public async Task ArchetypeCombos_AreNotAttributedFromTheirLowStackingLine(string prefix, uint lo, uint hi)
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var combos = await db.Combos.AsNoTracking()
				.Where(c => c.Name.StartsWith(prefix))
				.Join(db.Spells.AsNoTracking(), c => c.GameActionId, s => s.Id, (c, s) => new
				{
					c.Id,
					c.Name,
					s.GuiStackingLine
				})
				.ToListAsync(ct);

			Assert.NotEmpty(combos);
			// The premise: they really are in that low block, and it really does read as a class.
			Assert.All(combos, c => Assert.InRange(c.GuiStackingLine, lo, hi));
			Assert.All(combos, c => Assert.True(System.Enum.IsDefined((ConanClass)(c.GuiStackingLine / 1000)),
				$"{c.Name} sits on {c.GuiStackingLine}, whose /1000 is not a class - the trap this test guards no longer applies"));

			// And the conclusion: none of them was mapped from it. Their real class membership needs the captured
			// action bars (#581), not this field.
			var ids = combos.Select(c => c.Id).ToList();
			var mapped = await db.ClassCombos.AsNoTracking().Where(cc => ids.Contains(cc.ComboId)).CountAsync(ct);
			Assert.Equal(0, mapped);
		}

		// Where the rule IS applied - lineIndex 400 and up - it must never contradict the map built independently
		// from name prefixes and captured action bars. That agreement (257 of 257 at the time it landed) is the whole
		// reason the field is trusted for the feat-tree prefixes it resolves.
		[Fact]
		public async Task NoClassComboRow_ContradictsTheDrivenSpellsStackingLine()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));

			var rows = await db.ClassCombos.AsNoTracking()
				.Join(db.Combos.AsNoTracking(), cc => cc.ComboId, c => c.Id, (cc, c) => new
				{
					cc.Class,
					c.Name,
					c.GameActionId
				})
				.Join(db.Spells.AsNoTracking(), x => x.GameActionId, s => s.Id, (x, s) => new
				{
					x.Class,
					x.Name,
					s.GuiStackingLine
				})
				.Where(x => x.GuiStackingLine % 1000 >= 400)
				.ToListAsync(TestContext.Current.CancellationToken);

			Assert.NotEmpty(rows);
			var bad = rows.Where(x => (uint)x.Class != x.GuiStackingLine / 1000).ToList();
			Assert.True(bad.Count == 0,
				"class<->combo rows contradicting the stacking line of the spell they drive: " +
				string.Join(", ", bad.Select(x => $"'{x.Name}' mapped to {x.Class} but its spell is on line {x.GuiStackingLine}")));
		}
	}
}
