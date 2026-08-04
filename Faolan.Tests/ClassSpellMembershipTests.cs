using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #296: which class may cast which spell comes off the client's own catalog - a `Spell.GuiStackingLine` encoded as
	// `ConanClass * 1000 + lineIndex` names the class the line belongs to. This replaced an AoC-fandom-wiki scrape
	// whose importer asserted the opposite ("the client RDB spell record has no class field... so the community wiki
	// is the source").
	//
	// The evidence for the swap was that the two AGREED on 342 of the 343 rows where both spoke. That comparison
	// cannot be re-run - the wiki TSV is retired - so what is gated here is the property that made it meaningful:
	// the shipped map and the stacking line do not contradict each other.
	public class ClassSpellMembershipTests
	{
		// The one known exception, kept as data rather than as a tolerance: `Draw Forth The Heart` sits on
		// Necromancer line 41016 but the retired wiki listed it under Demonologist, and both rows are shipped. It is
		// exactly the trap the catalog keeps setting - `Spells` holds several rows per display name - so it is named
		// here instead of being rounded away, and a SECOND such row should fail this test rather than join it.
		private const uint KnownDisagreement = 146281;

		[Fact]
		public async Task NoClassSpellRow_ContradictsItsSpellsStackingLine()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var contradictions = await db.ClassSpells.AsNoTracking()
				.Join(db.Spells.AsNoTracking(), c => c.SpellId, s => s.Id, (c, s) => new
				{
					c.SpellId,
					c.Class,
					s.Name,
					s.GuiStackingLine
				})
				.Where(x => x.GuiStackingLine > 0 && (uint)x.Class != x.GuiStackingLine / 1000)
				.ToListAsync(ct);

			// A line whose high part is not a class says nothing about membership (50000 and 440061 are real lines
			// with no class in them - see Spell.GuiStackingLine), so only the class-encoded ones can contradict.
			var real = contradictions
				.Where(x => System.Enum.IsDefined((ConanClass)(x.GuiStackingLine / 1000)) && x.SpellId != KnownDisagreement)
				.ToList();

			Assert.True(real.Count == 0,
				"class<->spell rows contradicting their spell's own stacking line: " +
				string.Join(", ", real.Select(x => $"{x.SpellId} '{x.Name}' mapped to {x.Class} but on line {x.GuiStackingLine}")));
		}

		// The point of moving off the scrape: the wiki covered nine classes and the client covers all twelve.
		[Fact]
		public async Task EveryPlayableClass_HasSpells()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var perClass = await db.ClassSpells.AsNoTracking()
				.GroupBy(c => c.Class)
				.Select(g => new
				{
					Class = g.Key,
					Count = g.Count()
				})
				.ToListAsync(TestContext.Current.CancellationToken);

			var missing = ClassSpellClasses.Where(c => perClass.All(p => p.Class != c)).ToList();
			Assert.True(missing.Count == 0, "classes with no spells at all: " + string.Join(", ", missing));
			Assert.All(perClass, p => Assert.True(p.Count >= 25, $"{p.Class} has only {p.Count} spells"));
		}

		// The twelve playable classes - the same set `ClassSpell`'s own CHECK constraint enumerates.
		private static readonly ConanClass[] ClassSpellClasses =
		[
			ConanClass.Barbarian, ConanClass.Guardian, ConanClass.Conqueror, ConanClass.PriestOfMitra,
			ConanClass.TempestOfSet, ConanClass.BearShaman, ConanClass.DarkTemplar, ConanClass.Assassin,
			ConanClass.Ranger, ConanClass.Necromancer, ConanClass.HeraldOfXotli, ConanClass.Demonologist
		];
	}
}
