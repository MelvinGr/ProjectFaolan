using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// Which class may cast which spell comes off the client's own catalog - a `Spell.GuiStackingLine` encoded as
	// `ConanClass * 1000 + lineIndex` names the class the line belongs to. What is gated here is that the shipped
	// map and the stacking line do not contradict each other.
	public class ClassSpellMembershipTests
	{
		// The one known exception, kept as data rather than as a tolerance: `Draw Forth The Heart` sits on
		// Necromancer line 41016 while a Demonologist row of the same display name is also shipped. It is exactly the
		// trap the catalog keeps setting - `Spells` holds several rows per display name - so it is named here instead
		// of being rounded away, and a second such row should fail this test rather than join it.
		private const uint KnownDisagreement = 146281;

		// How far into a leading number the shared archetype block runs - the same 8 `RdbSpellImporter` uses.
		private const uint ArchetypeBlockWidth = 8;

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
			//
			// ...and neither can the archetype-shared block, 20000-20008, where the leading number is a block rather
			// than Guardian: three archetypes' combos sit under it, so its spells go to their archetype's three
			// classes and two of those three necessarily "contradict" the line. That is what this comparison must not
			// reinstate; `ArchetypeSpells_GoToTheirArchetypesThreeClasses` gates the block instead.
			var real = contradictions
				.Where(x => System.Enum.IsDefined((ConanClass)(x.GuiStackingLine / 1000)) && x.SpellId != KnownDisagreement
				            && x.GuiStackingLine % 1000 > ArchetypeBlockWidth)
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

		// A combo is not a spellbook spell. `ClassSpells` is derived from the class-encoded stacking line and a
		// melee combo's GameAction carries one, so combos are attributed to their class exactly like spells - which is
		// correct as membership and wrong as a spellbook.
		//
		// The oracle for that - every captured character's known-spell list - lives in `--report spell-grant-check`
		// and not here: `faolan.sql` excludes every `Character*` table as per-developer play state, so the suite's
		// fixture holds zero `CharacterSpells` rows and a gate written against them would assert an empty intersection
		// against an empty left-hand side. What is below is the code half, which needs no character rows and can
		// genuinely fail.
		//
		// So neither level-up path may learn one. Both queries feed `DispatchLearnSpell` + `AddCharacterSpell`; the
		// combos of the level band travel their own way, as ComboBookIIR right after the NewLevelIIR.
		[Fact]
		public async Task NeitherSpellGrantQuery_ReturnsAComboGameAction()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;
			var comboActions = (await db.Combos.AsNoTracking().Where(c => c.GameActionId != 0).Select(c => c.GameActionId).ToListAsync(ct)).ToHashSet();

			var repo = new DatabaseRepository(new Factory(TestDb.ReadOnly(TestDb.GameDatabase)));
			foreach (var @class in ClassSpellClasses)
			{
				var dinged = await repo.GetClassSpellsUnlockedBetween(@class, 0, 80);
				Assert.True(dinged.All(id => !comboActions.Contains(id)),
					$"{@class}: level-up learns combo GameAction(s) {string.Join(", ", dinged.Where(comboActions.Contains))}");

				// The relog re-grant of everything the class should already know, and the default bar seed.
				var relog = await repo.GetStartingSpells(80, @class);
				Assert.True(relog.All(id => !comboActions.Contains(id)),
					$"{@class}: the level-80 re-grant learns combo GameAction(s) {string.Join(", ", relog.Where(comboActions.Contains))}");

				var bar = await repo.GetStartingBarSpells(80, @class);
				Assert.True(bar.All(id => !comboActions.Contains(id)),
					$"{@class}: the default bar seeds combo GameAction(s) {string.Join(", ", bar.Where(comboActions.Contains))}");
			}
		}

		// An effect is not a spellbook spell either, and it reaches `ClassSpells` by the same route a combo
		// does - the DoT a spell applies carries its caster's class-encoded stacking line. `Infestation` is what
		// `Flesh to Worms` inflicts, and all five of its ranks sit at Level 2 because an effect's ranks pair with the
		// caster's ranks rather than with a level ladder of their own, so one 1 -> 2 ding would teach all five.
		//
		// Same oracle as the combo rule, and in the same place for the same reason - `--report spell-grant-check`.
		//
		// So no grant path may teach one - the ding, the relog re-grant, or the default bar seed. The re-grant matters
		// as much as the ding: it re-derives the whole book on every world entry, so leaving it out would put back
		// what the ding does not teach.
		[Fact]
		public async Task NoSpellGrantQuery_ReturnsAnEffectOfABookSpell()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;
			var effects = await EffectsOfBookSpells(db, ct);

			var repo = new DatabaseRepository(new Factory(TestDb.ReadOnly(TestDb.GameDatabase)));
			foreach (var @class in ClassSpellClasses)
			{
				var dinged = await repo.GetClassSpellsUnlockedBetween(@class, 0, 80);
				Assert.True(dinged.All(id => !effects.Contains(id)), $"{@class}: level-up learns effect spell(s) {string.Join(", ", dinged.Where(effects.Contains))}");

				var relog = await repo.GetStartingSpells(80, @class);
				Assert.True(relog.All(id => !effects.Contains(id)), $"{@class}: the level-80 re-grant learns effect spell(s) {string.Join(", ", relog.Where(effects.Contains))}");

				var bar = await repo.GetStartingBarSpells(80, @class);
				Assert.True(bar.All(id => !effects.Contains(id)), $"{@class}: the default bar seeds effect spell(s) {string.Join(", ", bar.Where(effects.Contains))}");
			}
		}

		// The trap the rule is shaped around, kept as its own gate because the obvious predicate passes every test
		// above and is still wrong. `Flesh to Worms (Rank 1)` is itself named as the effect of `Plague Swarm`, so
		// "anything some spell points at" deletes the very spell the level-2 ding exists to teach. `Plague Swarm` is
		// Level 0 and therefore grants nothing, which is what makes it not a book spell and its target not an effect.
		//
		// So the Necromancer's 1 -> 2 ding must teach `Flesh to Worms (Rank 1)` and not the five `Infestation` ranks
		// it applies - the exact in-client report this came from.
		[Fact]
		public async Task TheNecromancerDing_TeachesFleshToWorms_AndNotTheInfestationItApplies()
		{
			const uint FleshToWormsRank1 = 1206011;
			uint[] infestationRanks = [1378899, 1378901, 1378902, 1378903, 1378904];

			var repo = new DatabaseRepository(new Factory(TestDb.ReadOnly(TestDb.GameDatabase)));
			var dinged = await repo.GetClassSpellsUnlockedBetween(ConanClass.Necromancer, 1, 2);

			Assert.Contains(FleshToWormsRank1, dinged);
			Assert.Empty(dinged.Intersect(infestationRanks));
		}

		// The same shape of in-client report, one rung further along: a level-4 Necromancer's ding taught `Lifestrike`
		// ranks 3, 4 and 5 at once, the last of which states its damage for levels 75-80. The cause was the level data
		// rather than the query - RDB field 125 rates the five ranks 2/3/4/4/4 - and `RdbSpellImporter`'s
		// `RankLadderFromEffectBands` re-reads such a family's ladder off the effect ramp bands (15/30/45/60/75, which
		// is where ellatha puts rank 1 too).
		//
		// Kept as an id-level gate for the same reason its `Flesh to Worms` neighbour is: a family that teaches several
		// of its own ranks in one ding passes every population test above, so only naming the family catches it.
		[Fact]
		public async Task TheNecromancerDing_TeachesOneLifestrikeRankPerBand_NotThreeAtLevelFour()
		{
			const uint LifestrikeRank1 = 3666302;
			uint[] lifestrikeRanks = [3666302, 3666335, 3666337, 3666338, 3666339];

			var repo = new DatabaseRepository(new Factory(TestDb.ReadOnly(TestDb.GameDatabase)));

			var atFour = await repo.GetClassSpellsUnlockedBetween(ConanClass.Necromancer, 3, 4);
			Assert.Empty(atFour.Intersect(lifestrikeRanks));

			var atFifteen = await repo.GetClassSpellsUnlockedBetween(ConanClass.Necromancer, 14, 15);
			Assert.Equal([LifestrikeRank1], atFifteen.Intersect(lifestrikeRanks));
		}

		// The default action bar may only point at spells the character actually has. It is seeded once, on first
		// world entry, from `GetStartingBarSpells`, and the book comes from `GetStartingSpells`; a slot outside that
		// book is a dead button. The bar must not re-derive its own candidate set - a second copy of the combo and
		// effect exclusions plus a plain `Level <= level` drifts, because level 1 is the create kit's exclusive domain
		// on the book side and an ordinary level on a bar-side query, which seeds a level-4 Necromancer's bar with
		// `Pestilential Blast (Rank 1)`, a spell the character has never been taught.
		[Fact]
		public async Task TheDefaultBar_OnlySeedsSpellsTheBookHolds()
		{
			var repo = new DatabaseRepository(new Factory(TestDb.ReadOnly(TestDb.GameDatabase)));
			foreach (var @class in ClassSpellClasses)
				foreach (var level in (uint[])[1, 2, 4, 20, 80])
				{
					var book = (await repo.GetStartingSpells(level, @class)).ToHashSet();
					var bar = await repo.GetStartingBarSpells(level, @class);

					var outside = bar.Where(id => !book.Contains(id)).ToList();
					Assert.True(outside.Count == 0, $"{@class} at level {level}: bar seeds {string.Join(", ", outside)}, which the book does not hold");
				}
		}

		// The guard on `DatabaseRepository.FeatMapCovers`. Gating the level path on "a feat teaches it" is applied
		// per class, because the feat map grants five classes (Barbarian, Guardian, Conqueror, Assassin, Ranger)
		// nothing at all. For those the map's silence is missing data, not a statement, and gating on it would hand
		// out an empty kit with no capture to notice. This is what would fail if the gate were ever made global.
		//
		// It asserts on the whole kit - spells plus combos - and not on the spellbook alone, because an empty
		// spellbook is the correct answer for a pure-combo class. Once the combo-chain exclusion removed the
		// debuffs their combos inflict, the Barbarian and the Assassin were left with zero level-granted spells, and
		// that is the shape of the class rather than a gap: they hold 103 and 75 combos, which travel their own path
		// (`GetClassCombosUnlockedBetween` -> ComboBookIIR). Asserting on the book alone would have made a correct
		// exclusion look like a regression and invited a rule that put the debuffs back.
		[Fact]
		public async Task EveryClass_ReachesLevel80WithSomethingToCast()
		{
			var repo = new DatabaseRepository(new Factory(TestDb.ReadOnly(TestDb.GameDatabase)));
			foreach (var @class in ClassSpellClasses)
			{
				var book = await repo.GetStartingSpells(80, @class);
				var combos = await repo.GetClassCombosUnlockedBetween(@class, 0, 80);

				Assert.True(book.Length + combos.Length > 0, $"{@class} would reach level 80 with nothing to cast at all");
			}
		}

		// ...and the two are not interchangeable: a class the feat map does speak for is a caster, and a caster with
		// an empty spellbook is a defect however many combos it holds.
		[Fact]
		public async Task AClassTheFeatMapCoversStillHasASpellbookAt80()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;
			var repo = new DatabaseRepository(new Factory(TestDb.ReadOnly(TestDb.GameDatabase)));

			foreach (var @class in ClassSpellClasses)
			{
				var covered = await db.ClassSpells.AsNoTracking().Where(cs => cs.Class == @class)
					.AnyAsync(cs => db.FeatSpellGrants.Any(g => g.SpellId == cs.SpellId && g.Feat.TemplateCategory == Feat.AutomaticUnlockCategory), ct);
				if (!covered)
					continue;

				var book = await repo.GetStartingSpells(80, @class);
				Assert.True(book.Length > 0, $"{@class} has automatic-unlock feats and would still reach level 80 knowing no spells");
			}
		}

		// The two grant paths answer one question between them - what a character of a given level knows - so
		// everything the dings taught on the way up has to come back on the relog that re-derives the book. They gate
		// on the same expression (`ClassSpell.GrantLevel` where a capture caught the grant, `Spells.Level` elsewhere),
		// and this is what holds them there: the ding path read `Spells.Level` alone until the two were reconciled, so
		// every row where the two facts differ was a spell one path placed at a level the other did not.
		//
		// From 1, not 0: level 1 is the create kit's exclusive domain on the relog side (a captured list, not a level
		// query), which is a difference of intent rather than a drift - see GetStartingSpells.
		[Fact]
		public async Task EverythingTheDingsTeach_ComesBackOnTheRelog()
		{
			var repo = new DatabaseRepository(new Factory(TestDb.ReadOnly(TestDb.GameDatabase)));
			foreach (var @class in ClassSpellClasses)
			{
				var dinged = await repo.GetClassSpellsUnlockedBetween(@class, 1, 80);
				var relog = (await repo.GetStartingSpells(80, @class)).ToHashSet();

				var lost = dinged.Where(id => !relog.Contains(id)).ToList();
				Assert.True(lost.Count == 0, $"{@class}: dinged spell(s) the level-80 re-grant does not restore: {string.Join(", ", lost)}");
			}
		}

		// A spell some other spell applies, where that other spell is itself a real leveled `ClassSpells` entry (i.e.
		// something a character can actually be taught). The referrer test is the whole point - see the trap gate
		// above.
		private static async Task<HashSet<uint>> EffectsOfBookSpells(DatabaseContext db, CancellationToken ct)
		{
			var book = await db.ClassSpells.AsNoTracking().Select(cs => cs.SpellId).Distinct().ToListAsync(ct);
			var appliers = await db.Spells.AsNoTracking().Where(s => s.EffectSpell != 0 && s.Level > 0).Select(s => new
			{
				s.Id,
				s.EffectSpell
			}).ToListAsync(ct);

			var bookSet = book.ToHashSet();
			return appliers.Where(a => bookSet.Contains(a.Id)).Select(a => a.EffectSpell).ToHashSet();
		}

		// ...and the five classes that carried no teaching feat at all were combos, not a gap: Barbarian,
		// Guardian and Assassin are 100% combo, Ranger 44 of 46. They are counted here rather than asserted class by
		// class so that a catalog refresh that genuinely adds a spell to one of them does not read as a regression.
		[Fact]
		public async Task TheMeleeClasses_LevelGatedEntries_AreOverwhelminglyCombos()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;
			var comboActions = (await db.Combos.AsNoTracking().Where(c => c.GameActionId != 0).Select(c => c.GameActionId).ToListAsync(ct)).ToHashSet();

			// The combo spell lines: a weapon variant of a combo whose own Combos row we do not hold (the catalog maps
			// 304 of 744) still sits on a line one of the mapped combos owns.
			var comboLines = await db.Spells.AsNoTracking()
				.Where(s => comboActions.Contains(s.Id) && s.GuiStackingLine != 0)
				.Select(s => s.GuiStackingLine)
				.Distinct()
				.ToListAsync(ct);

			ConanClass[] melee = [ConanClass.Barbarian, ConanClass.Guardian, ConanClass.Conqueror, ConanClass.Assassin, ConanClass.Ranger];
			var gated = await db.ClassSpells.AsNoTracking()
				.Where(c => melee.Contains(c.Class))
				.Join(db.Spells.AsNoTracking(), c => c.SpellId, s => s.Id, (c, s) => s)
				.Where(s => s.Level > 0)
				.Select(s => new
				{
					s.Id,
					s.GuiStackingLine
				})
				.Distinct()
				.ToListAsync(ct);

			var combos = gated.Count(s => comboActions.Contains(s.Id) || comboLines.Contains(s.GuiStackingLine));
			Assert.True(combos * 100 >= gated.Count * 80, $"only {combos} of {gated.Count} melee level-gated entries are combos");
		}

		// `Colossus of War` (3275545/3275640) is in no stacking line and chains twenty members of line 22200 - a rank
		// table, the same shape as the `Furious Inspiration` header that puts all ten of its ranks on the bar at once.
		// Without the exclusion the level path offers a Conqueror all twenty as twenty spells.
		//
		// The exclusion is structural, not a name match: a rule keyed on the `(N)` suffix is the kind of string
		// heuristic this codebase has repeatedly had to unwind. And it is checked against the same oracle the combo and
		// effect rules use - of the 383 spells it catches, zero appear in any captured spellbook.
		[Fact]
		public async Task AConqueror_IsNotTaughtTwentyColossusOfWarIncrements()
		{
			var repo = new DatabaseRepository(new Factory(TestDb.ReadOnly(TestDb.GameDatabase)));

			var book = await repo.GetStartingSpells(80, ConanClass.Conqueror);
			var dinged = await repo.GetClassSpellsUnlockedBetween(ConanClass.Conqueror, 0, 80);

			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;
			var increments = await db.Spells.AsNoTracking()
				.Where(s => s.GuiStackingLine == ColossusLine)
				.Select(s => s.Id)
				.ToListAsync(ct);

			Assert.Equal(20, increments.Count); // the population is real - otherwise this gate is vacuous
			Assert.Empty(book.Intersect(increments));
			Assert.Empty(dinged.Intersect(increments));
		}

		// The narrowing that makes it safe, gated on the set itself rather than on its consequences - the combo
		// rules already exclude some of the same spells, so an observable-effect test could not tell this rule's
		// narrowing from theirs.
		//
		// The carrier must be in no stacking line: a carrier that is itself in a line is a rank ladder rung, not a
		// table header. Without that clause the rule also swallows `Bleeding Puncture I..IV` (Ranger, levels
		// 5/36/52/64), which read as an ordinary learnable line - and `Flesh to Worms (Rank 1)`, the spell a level-2
		// Necromancer ding exists to teach, which must survive "anything some spell points at".
		[Fact]
		public async Task TheIncrementSet_CatchesTheRankTableAndNothingOrdinary()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var increments = await DatabaseRepository.StackingIncrements(db);
			Assert.NotEmpty(increments); // the rule finds something at all - otherwise every claim below is vacuous

			var colossus = await db.Spells.AsNoTracking().Where(s => s.GuiStackingLine == ColossusLine).Select(s => s.Id).ToListAsync(ct);
			Assert.Equal(20, colossus.Count);
			Assert.All(colossus, id => Assert.Contains(id, increments));

			var punctures = await db.Spells.AsNoTracking()
				.Where(s => s.Name != null && s.Name.StartsWith("Bleeding Puncture"))
				.Select(s => s.Id)
				.ToListAsync(ct);
			Assert.NotEmpty(punctures);
			Assert.All(punctures, id => Assert.DoesNotContain(id, increments));

			Assert.DoesNotContain(FleshToWormsRank1, increments);
		}

		// The spell the Necromancer's 1 -> 2 ding exists to teach, re-checked against this rule.
		private const uint FleshToWormsRank1 = 1206011;

		// The Colossus increments' shared stacking line - the thing that makes them one mutually-exclusive family.
		private const uint ColossusLine = 22200;

		private sealed class Factory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
		{
			public DatabaseContext CreateDbContext()
			{
				return new DatabaseContext(options);
			}

			public Task<DatabaseContext> CreateDbContextAsync(CancellationToken ct = default)
			{
				return Task.FromResult(new DatabaseContext(options));
			}
		}

		// The twelve playable classes - the same set `ClassSpell`'s own check constraint enumerates.
		private static readonly ConanClass[] ClassSpellClasses =
		[
			ConanClass.Barbarian, ConanClass.Guardian, ConanClass.Conqueror, ConanClass.PriestOfMitra,
			ConanClass.TempestOfSet, ConanClass.BearShaman, ConanClass.DarkTemplar, ConanClass.Assassin,
			ConanClass.Ranger, ConanClass.Necromancer, ConanClass.HeraldOfXotli, ConanClass.Demonologist
		];
	}
}
