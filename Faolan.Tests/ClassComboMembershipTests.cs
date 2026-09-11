using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Importer.Captures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// Which class gets which combo comes from the stacking line of the spell the combo drives - a combo's
	// `GameActionId` is a spell id, and a class-encoded `GuiStackingLine` names the class.
	//
	// What is worth gating is not that the rule works but that it is applied narrowly, because the obvious reading of
	// it is wrong. The archetype-shared combos sit in a low block that reads as class 20 and is not one, and taking
	// it at face value files every Rogue and Priest archetype combo under Guardian.
	public class ClassComboMembershipTests
	{
		// The archetype-shared block: three different archetypes under the same leading "20", which is what proves
		// the number is a block rather than a class, and that each of them goes to its archetype's three classes rather
		// than to the class that number would name.
		//
		// `Priest_` is not in this theory, and its absence is the point. Its three combos sit on 20008 like
		// the others, but they are the only archetype combos whose gating feat names a single class rather than the
		// archetype - see ArchetypeCombos_YieldToTheGatingFeatWhenItNamesOneClass below. The block says which
		// archetype's line a combo is on; the feat says who is taught it, and where they differ the feat wins.
		[Theory]
		[InlineData("Soldier_", 20002u, 20005u, ConanArchetype.Soldier)]
		[InlineData("Rogue_", 20006u, 20007u, ConanArchetype.Rogue)]
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

			// And the conclusion, in both directions: every one of them is mapped, to exactly the archetype's three
			// classes - never to the class its stacking line would name unless that class is one of the three.
			var ids = combos.Select(c => c.Id).ToList();
			var mapped = (await db.ClassCombos.AsNoTracking().Where(cc => ids.Contains(cc.ComboId)).ToListAsync(ct)).GroupBy(cc => cc.ComboId).ToList();
			var expected = ConanArchetypes.ClassesOf(archetype).Order().ToList();

			Assert.Equal(combos.Count, mapped.Count);
			Assert.All(mapped, g => Assert.Equal(expected, g.Select(x => x.Class).Order().ToList()));
		}

		// The one archetype-lined family the feat overrides. `Priest_..._2hb` is on the Priest archetype's
		// stacking line 20008, so the name rule fanned it out to all three priest classes - but the feat that unlocks
		// it is `Bear Shaman Class Feat 001`/`011`, the same feat that unlocks `BearShaman_05_dot_Internal_Bleed_I`
		// and its siblings. A combo is granted by the feat that teaches it, so these are Bear Shaman's.
		//
		// Assumption, recorded rather than hidden: this reads the missing Priest of Mitra / Tempest of Set variants
		// as absent from the catalog rather than as classes that share the 2HB one. The two-handed blunt is the Bear
		// Shaman's weapon, and Soldier_/Rogue_ each ship three weapon variants where Priest_ ships only this one, so
		// the shape of the gap is a missing variant, not a shared combo. If a Priest of Mitra variant ever lands, it
		// will arrive with its own class feat and this test is where the expectation changes.
		[Fact]
		public async Task ArchetypeCombos_YieldToTheGatingFeatWhenItNamesOneClass()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var combos = await db.Combos.AsNoTracking().Where(c => c.Name.StartsWith("Priest_")).Select(c => new
			{
				c.Id,
				c.Name
			}).ToListAsync(ct);

			Assert.NotEmpty(combos);
			var ids = combos.Select(c => c.Id).ToList();
			var mapped = await db.ClassCombos.AsNoTracking().Where(cc => ids.Contains(cc.ComboId)).ToListAsync(ct);

			Assert.Equal(combos.Count, mapped.Select(x => x.ComboId).Distinct().Count());
			Assert.All(mapped, row => Assert.Equal(ConanClass.BearShaman, row.Class));
		}

		// The same rule on the spell side. Reading `line / 1000` as a class with no guard files all 22 of the Rogue and
		// Priest archetype spells - Cunning Strike I-VI, Vicious Strike I-II, Censuring Strike I-III - under Guardian,
		// and leaves no other class holding a single archetype spell.
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

				Assert.NotEqual(ConanArchetype.Commoner, archetype); // an unattributable line must have no rows at all
				var expected = ConanArchetypes.ClassesOf(archetype).Order().ToList();
				foreach (var spell in line.GroupBy(x => x.SpellId))
					Assert.Equal(expected, spell.Select(x => x.Class).Order().ToList());
			}
		}

		// The gating-feat rule, both halves:
		//
		//   1. every shipped `ClassCombo` for a combo whose gating feat names its classes is exactly that set - which
		//      is what makes the feat authoritative rather than one more opinion;
		//   2. it is not vacuous: it speaks for a substantial slice of the map.
		//
		// The feat's authority rests on the agreement measured where the feat and the catalog name prefix both speak:
		// 134 agreed and none conflicted. That agreement is not observable from the shipped rows once the feat leads, so
		// what this gates is the invariant it produces.
		[Fact]
		public async Task EveryClassComboRow_AgreesWithItsCombosGatingFeat()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var gating = await FeatGating.LoadAsync(db);
			var combos = await db.Combos.AsNoTracking().Where(c => c.FeatId != null).Select(c => new
			{
				c.Id,
				c.Name,
				c.FeatId
			}).ToListAsync(ct);

			var mapped = (await db.ClassCombos.AsNoTracking().ToListAsync(ct)).GroupBy(cc => cc.ComboId)
				.ToDictionary(g => g.Key, g => g.Select(x => x.Class).Order().ToList());

			var spoken = 0;
			var bad = new List<string>();
			foreach (var combo in combos)
			{
				if (gating.ClassesOf(combo.FeatId) is not { Count: > 0 } expected)
					continue;

				spoken++;
				var actual = mapped.GetValueOrDefault(combo.Id, []);
				if (!actual.SequenceEqual(expected.Order()))
					bad.Add($"'{combo.Name}' is mapped to [{string.Join(",", actual)}] but its gating feat names [{string.Join(",", expected.Order())}]");
			}

			Assert.True(bad.Count == 0, "class<->combo rows contradicting the combo's gating feat: " + string.Join("; ", bad));
			Assert.True(spoken >= 140, $"the gating feat names classes for only {spoken} combos - it used to speak for 142, so the join has come apart");
		}

		// A combo's feat tree is the leading segment of its name, and a tree belongs to one class. Three trees
		// have said so themselves, through their own members' gating feats, and the tree-inheritance pass then applies
		// that answer to the members whose feat names a weapon line (`Pole Arm Combos 20-40 1`) or an ability
		// (`Combo - Skewer`) instead of a class.
		//
		// What is gated is the property the rule rests on: each of these trees is complete and unanimous. A tree that
		// splits across two classes would mean the inheritance had attributed something wrongly.
		[Theory]
		[InlineData("Reaver_", ConanClass.Barbarian, 30)]
		[InlineData("Halberdier_", ConanClass.Guardian, 30)]
		[InlineData("Liberator_", ConanClass.Conqueror, 27)]
		public async Task AFeatTree_IsOneClass_AndEveryMemberIsMapped(string tree, ConanClass expected, int members)
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var combos = await db.Combos.AsNoTracking().Where(c => c.Name != null && c.Name.StartsWith(tree)).Select(c => new
			{
				c.Id,
				c.Name
			}).ToListAsync(ct);

			Assert.Equal(members, combos.Count);

			var ids = combos.Select(c => c.Id).ToList();
			var mapped = (await db.ClassCombos.AsNoTracking().Where(cc => ids.Contains(cc.ComboId)).ToListAsync(ct))
				.GroupBy(cc => cc.ComboId)
				.ToDictionary(g => g.Key, g => g.Select(x => x.Class).Distinct().ToList());

			var unmapped = combos.Where(c => !mapped.ContainsKey(c.Id)).Select(c => c.Name!).ToList();
			Assert.True(unmapped.Count == 0, $"{tree} members with no class: {string.Join(", ", unmapped)}");

			var split = mapped.Where(kv => kv.Value.Count != 1 || kv.Value[0] != expected)
				.Select(kv => $"{combos.First(c => c.Id == kv.Key).Name} -> [{string.Join(",", kv.Value)}]")
				.ToList();
			Assert.True(split.Count == 0, $"{tree} is not one class: {string.Join("; ", split)}");
		}

		// And the guard that keeps the rule off the trees it must not fire on. An archetype tree is correctly
		// multi-class and a racial one is universal, so a unanimity rule that ignored them would collapse each to a
		// single class - which is the exact mistake the stacking-line block above already documents.
		[Theory]
		[InlineData("Soldier_", 3)]
		[InlineData("Rogue_", 3)]
		[InlineData("Cimmerian_", 12)]
		public async Task AMultiClassTree_KeepsAllOfItsClasses(string tree, int classes)
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var rows = await db.ClassCombos.AsNoTracking()
				.Join(db.Combos.AsNoTracking(), cc => cc.ComboId, c => c.Id, (cc, c) => new { cc.Class, c.Name })
				.Where(x => x.Name!.StartsWith(tree))
				.ToListAsync(ct);

			Assert.NotEmpty(rows);
			Assert.Equal(classes, rows.Select(x => x.Class).Distinct().Count());
		}

		// Where the rule is applied - lineIndex 400 and up - it must never contradict the map built independently
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
