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
		// the number is a block rather than a class - and, since #581, that each of them goes to its ARCHETYPE's three
		// classes rather than to the class that number would name.
		[Theory]
		[InlineData("Soldier_", 20002u, 20005u, ConanArchetype.Soldier)]
		[InlineData("Rogue_", 20006u, 20007u, ConanArchetype.Rogue)]
		[InlineData("Priest_", 20008u, 20008u, ConanArchetype.Priest)]
		public async Task ArchetypeCombos_GoToTheirArchetypesThreeClasses(string prefix, uint lo, uint hi, ConanArchetype archetype)
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

			// And the conclusion, in both directions: every one of them is mapped, to EXACTLY the archetype's three
			// classes - never to the class its stacking line would name unless that class is one of the three.
			var ids = combos.Select(c => c.Id).ToList();
			var mapped = (await db.ClassCombos.AsNoTracking().Where(cc => ids.Contains(cc.ComboId)).ToListAsync(ct)).GroupBy(cc => cc.ComboId).ToList();
			var expected = ConanArchetypes.ClassesOf(archetype).Order().ToList();

			Assert.Equal(combos.Count, mapped.Count);
			Assert.All(mapped, g => Assert.Equal(expected, g.Select(x => x.Class).Order().ToList()));
		}

		// The same correction on the SPELL side, which is where it was actually wrong in the shipped data (#581):
		// `RdbSpellImporter` read `line / 1000` as a class with no guard, so GUARDIAN held all 22 of the Rogue and
		// Priest archetype spells - Cunning Strike I-VI, Vicious Strike I-II, Censuring Strike I-III - and no other
		// class held a single archetype spell.
		[Fact]
		public async Task ArchetypeSpells_GoToTheirArchetypesThreeClassesOrNowhere()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var rows = await db.ClassSpells.AsNoTracking()
				.Join(db.Spells.AsNoTracking(), cs => cs.SpellId, s => s.Id, (cs, s) => new
				{
					cs.Class,
					cs.SpellId,
					s.GuiStackingLine
				})
				.Where(x => x.GuiStackingLine >= 20000 && x.GuiStackingLine <= 20008)
				.ToListAsync(ct);

			Assert.NotEmpty(rows);
			foreach (var line in rows.GroupBy(x => x.GuiStackingLine))
			{
				var archetype = line.Key switch
				{
					>= 20002 and <= 20005 => ConanArchetype.Soldier,
					>= 20006 and <= 20007 => ConanArchetype.Rogue,
					20008 => ConanArchetype.Priest,
					_ => ConanArchetype.Commoner // 20000/20001 carry no combo, so the block says nothing about them
				};

				Assert.NotEqual(ConanArchetype.Commoner, archetype); // an unattributable line must have NO rows at all
				var expected = ConanArchetypes.ClassesOf(archetype).Order().ToList();
				foreach (var spell in line.GroupBy(x => x.SpellId))
					Assert.Equal(expected, spell.Select(x => x.Class).Order().ToList());
			}
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
