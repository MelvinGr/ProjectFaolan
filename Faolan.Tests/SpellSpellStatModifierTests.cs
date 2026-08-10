using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Core.Rdb;
using Faolan.GameServer.Combat;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #710: the SPELL-sourced half of the spell-scoped stat modifier - "while this buff is up, your next Fireball
	// crits harder". #593 imported the identical record off FEATS; the spell half was parsed, its stat and magnitude
	// stored on `SpellStatEffects`, and its TARGET dropped on the floor, because that table is keyed by the carrier
	// with the carrier as the SUBJECT and had nowhere to put "…and the thing that moves belongs to another spell".
	public class SpellSpellStatModifierTests
	{
		private const uint SpellScoped = ItemTemplateStatModifiers.SpellStatModifierKind; // 53054
		private const uint LineScoped = ItemTemplateStatModifiers.SpellLineStatModifierKind; // 53062

		// The target is the whole point of the table, and it RESOLVES - which is what says the decode is right rather
		// than merely self-consistent. Same strength of result as the feat half's 593/593 and 475/475.
		[Fact]
		public async Task EveryTarget_ResolvesAgainstTheShippedCatalog()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;
			var rows = await db.SpellSpellStatModifiers.AsNoTracking().ToListAsync(ct);

			Assert.NotEmpty(rows);

			var spellIds = (await db.Spells.AsNoTracking().Select(s => s.Id).ToListAsync(ct)).ToHashSet();
			var lines = (await db.Spells.AsNoTracking().Where(s => s.GuiStackingLine > 0).Select(s => s.GuiStackingLine).Distinct().ToListAsync(ct)).ToHashSet();

			// Spell-scoped targets resolve completely.
			var direct = rows.Where(r => r.TargetSpellId != null).ToList();
			Assert.NotEmpty(direct);
			Assert.All(direct, r => Assert.Contains(r.TargetSpellId!.Value, spellIds));

			// Line-scoped targets resolve all but a handful. Pinned as a small population rather than asserted away,
			// so it cannot quietly grow into "the target slot moved".
			var byLine = rows.Where(r => r.TargetSpellLineId != null).ToList();
			Assert.NotEmpty(byLine);
			Assert.InRange(byLine.Count(r => !lines.Contains(r.TargetSpellLineId!.Value)), 0, 20);
		}

		// The KIND picks the target column, and never both - the same invariant the feat table carries. A row with
		// neither would be a modifier aimed at nothing; a row with both would be two claims about one record.
		[Fact]
		public async Task ExactlyOneTargetColumn_IsSet_AndTheKindSaysWhich()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var rows = await db.SpellSpellStatModifiers.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);

			Assert.All(rows, r => Assert.True(r.TargetSpellId != null ^ r.TargetSpellLineId != null,
				$"spell {r.SpellId} seq {r.Sequence}: target columns are {r.TargetSpellId}/{r.TargetSpellLineId}"));
			Assert.All(rows.Where(r => r.EffectKind == SpellScoped), r => Assert.NotNull(r.TargetSpellId));
			Assert.All(rows.Where(r => r.EffectKind == LineScoped), r => Assert.NotNull(r.TargetSpellLineId));
			Assert.All(rows, r => Assert.Contains(r.EffectKind, (uint[]) [SpellScoped, LineScoped]));
		}

		// `Value` and `PercentHundredths` are ALTERNATIVES, not a pair - the client's tooltip builder renders them
		// through two different formatters. Exactly one is set on every shipped row, which is what lets them be two
		// nullable columns rather than a value plus a mode flag.
		[Fact]
		public async Task EveryRow_CarriesExactlyOneOfValueAndPercent()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var rows = await db.SpellSpellStatModifiers.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);

			Assert.All(rows, r => Assert.True(r.Value != null ^ r.PercentHundredths != null,
				$"spell {r.SpellId} seq {r.Sequence}: {r.Value}/{r.PercentHundredths}"));
		}

		// ⚠️ A carrier that targets ITSELF would be indistinguishable from an ordinary `SpellStatEffect`, and would
		// make the whole table pointless - the target is what separates the two.
		[Fact]
		public async Task TheStatsAreSPELLProperties_NotCharacterOnes()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var rows = await db.SpellSpellStatModifiers.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);

			// The families the ticket enumerated, each of which is already a column on `Spells` - which is what makes
			// applying one a per-cast override of the base row rather than a new number.
			CharacterStatId[] spellProperties =
			[
				CharacterStatId.SpellCriticalChance, CharacterStatId.SpellCriticalDamage, CharacterStatId.ManaCost,
				CharacterStatId.CastingTimeSeconds, CharacterStatId.SplashDamage, CharacterStatId.SplashRadius,
				CharacterStatId.SplashDamageMaxTargets
			];

			// Those seven cover the overwhelming majority; the tail is other spell properties, not character stats.
			Assert.True(rows.Count(r => spellProperties.Contains(r.StatId)) > rows.Count * 0.8,
				"the modifier stats stopped being dominated by spell properties - check the Stat slot is still being read");
		}

		// The RELATIVE half is what the engine applies, and the arithmetic is the client's: -1000 = -10%.
		[Theory]
		[InlineData(1000, 0, 1000)] // no modifier - the base is untouched
		[InlineData(1000, -1000, 900)] // -10%
		[InlineData(1000, -10000, 0)] // -100% clamps to free, never negative
		[InlineData(1000, 2500, 1250)] // +25%
		[InlineData(0, -5000, 0)] // a spell that costs nothing stays free
		public void ApplyPercent_ScalesTheBaseByHundredthsOfAPercent(int baseValue, int pct, int expected)
		{
			var modifiers = Modifiers(CharacterStatId.ManaCost, pct);

			Assert.Equal(expected, modifiers.ApplyPercent(baseValue, CharacterStatId.ManaCost));
		}

		// A modifier on a DIFFERENT stat must not leak onto the one being asked about - the fold is per stat.
		[Fact]
		public void ApplyPercent_IgnoresAModifierOnAnotherStat()
		{
			var modifiers = Modifiers(CharacterStatId.CastingTimeSeconds, -5000);

			Assert.Equal(1000, modifiers.ApplyPercent(1000, CharacterStatId.ManaCost));
			Assert.Equal(500, modifiers.ApplyPercent(1000, CharacterStatId.CastingTimeSeconds));
		}

		// `ApplyPercent` is still the percent-only half, and it must stay that way - `Apply` is what composes them.
		[Fact]
		public void ApplyPercent_DoesNotApplyTheAbsoluteHalf()
		{
			var modifiers = Absolute(CharacterStatId.ManaCost, -50);

			Assert.Equal(1000, modifiers.ApplyPercent(1000, CharacterStatId.ManaCost));
			Assert.Equal(-50, modifiers.Absolute(CharacterStatId.ManaCost));
		}

		// ⭐ #787: the absolute half APPLIES now, in the units the column holds. `ManaCost` stores the property-run
		// field verbatim, so the delta needs no conversion.
		[Fact]
		public void Apply_AddsTheAbsoluteHalfInTheColumnsOwnUnits()
		{
			Assert.Equal(950, Absolute(CharacterStatId.ManaCost, -50).Apply(1000, CharacterStatId.ManaCost));
			Assert.Equal(0, Absolute(CharacterStatId.ManaCost, -5000).Apply(1000, CharacterStatId.ManaCost)); // clamped
			Assert.Equal(1000, Absolute(CharacterStatId.CastingTimeSeconds, -50).Apply(1000, CharacterStatId.ManaCost));
		}

		// ⚠️ ...except where the importer converted the field on the way into the column. `CastTime` is built as
		// `field * 10` (centiseconds -> ms), so a -300 modifier is -3.00 s = -3000 ms, not -300. Reading it in the
		// column's units would take 3 s off a 5 s cast instead of 0.3 s.
		[Fact]
		public void Apply_ConvertsACentisecondModifierIntoTheMillisecondColumn()
		{
			Assert.Equal(2000, Absolute(CharacterStatId.CastingTimeSeconds, -300).Apply(5000, CharacterStatId.CastingTimeSeconds));
			Assert.Equal(3000, Absolute(CharacterStatId.Duration, -200).Apply(5000, CharacterStatId.Duration));
		}

		// Additive first, then relative - the client's own composition order for a value carrying both (#782).
		[Fact]
		public void Apply_AddsBeforeItScales()
		{
			var both = new SpellModifiers(
				new System.Collections.Generic.Dictionary<CharacterStatId, int> { [CharacterStatId.ManaCost] = -200 },
				new System.Collections.Generic.Dictionary<CharacterStatId, int> { [CharacterStatId.ManaCost] = -5000 });

			Assert.Equal(400, both.Apply(1000, CharacterStatId.ManaCost)); // (1000 - 200) * 0.5, not 1000*0.5 - 200
		}

		private static SpellModifiers Absolute(CharacterStatId stat, int value)
		{
			return new SpellModifiers(
				new System.Collections.Generic.Dictionary<CharacterStatId, int> { [stat] = value },
				new System.Collections.Generic.Dictionary<CharacterStatId, int>());
		}

		private static SpellModifiers Modifiers(CharacterStatId stat, int percentHundredths)
		{
			return new SpellModifiers(
				new System.Collections.Generic.Dictionary<CharacterStatId, int>(),
				new System.Collections.Generic.Dictionary<CharacterStatId, int> { [stat] = percentHundredths });
		}
	}
}
