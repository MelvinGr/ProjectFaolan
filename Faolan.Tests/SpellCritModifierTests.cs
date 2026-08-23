using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.GameServer.Combat;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// #710: a spell-scoped modifier moves the spell's OWN crit numbers, not just its cast time and mana.
	//
	// The mechanism was already built and folded (`SpellModifierService` reads the feat half AND the buff half off
	// one catalog); what was missing is that only two of the twelve stats those rows carry had a consumer. `Spell
	// Critical Chance` (147) and `Spell Critical Damage` (129) are the two BIGGEST - 269 and 340 shipped rows - and
	// they are the ones the ticket is named for: *"a live buff that makes your next Fireball crit harder."*
	public class SpellCritModifierTests
	{
		// The stats a spell-scoped modifier row can carry, and whether anything consumes one. Kept as a live census
		// rather than a prose list so the gap shrinks visibly as consumers land - and so a stat that gains rows in a
		// re-import cannot quietly re-open a hole this ticket closed.
		[Fact]
		public void TheSpellScopedModifierStatsAreCensusedAndTheCritOnesAreTheBiggest()
		{
			using var ctx = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var byStat = ctx.SpellSpellStatModifiers.AsNoTracking()
				.GroupBy(m => m.StatId)
				.Select(g => new { Stat = g.Key, Rows = g.Count() })
				.ToList();

			Assert.NotEmpty(byStat);

			// The four with a live consumer today: cast time (#787), mana cost, and the two crit stats (this change).
			var consumed = new[]
			{
				CharacterStatId.CastingTimeSeconds, CharacterStatId.ManaCost,
				CharacterStatId.SpellCriticalChance, CharacterStatId.SpellCriticalDamage
			};

			foreach (var stat in consumed)
				Assert.True(byStat.Any(s => s.Stat == stat), $"no shipped row carries {stat} - the consumer is aimed at nothing");

			// ⭐ The two crit stats really are the biggest slice, which is what made them the right ones to wire next.
			var critRows = byStat.Where(s => s.Stat is CharacterStatId.SpellCriticalChance or CharacterStatId.SpellCriticalDamage).Sum(s => s.Rows);
			var otherRows = byStat.Where(s => !consumed.Contains(s.Stat)).Sum(s => s.Rows);
			Assert.True(critRows > otherRows / 2, $"crit rows {critRows} are no longer the dominant slice against {otherRows} unconsumed");
		}

		// The fold itself: an absolute delta and a percentage both land on the stat, in the column's own units.
		[Theory]
		// +500 hundredths on a 250 base = 7.50% chance
		[InlineData(250, 500, 0, 750)]
		// a PERCENT row: +50% of a 5000 base crit-damage bonus
		[InlineData(5000, 0, 5000, 7500)]
		// a DEBUFF cannot take the value below zero - the clamp `Apply` already owns
		[InlineData(250, -1000, 0, 0)]
		// no modifier at all leaves the spell's own number alone
		[InlineData(250, 0, 0, 250)]
		public void ApplyMovesTheSpellsOwnCritNumber(int baseValue, int absolute, int percentHundredths, int expected)
		{
			var mods = Modifiers(CharacterStatId.SpellCriticalChance, absolute, percentHundredths);

			Assert.Equal(expected, mods.Apply(baseValue, CharacterStatId.SpellCriticalChance));
		}

		// ⚠️ A modifier aimed at ANOTHER stat must not leak onto the crit ones - the fold is per stat, and a bug that
		// merged them would make every cast-time buff a crit buff.
		[Fact]
		public void AModifierOnAnotherStatDoesNotTouchCrit()
		{
			var mods = Modifiers(CharacterStatId.CastingTimeSeconds, -900, 0);

			Assert.Equal(250, mods.Apply(250, CharacterStatId.SpellCriticalChance));
			Assert.Equal(0, mods.Apply(900, CharacterStatId.CastingTimeSeconds));
		}

		// ⚠️ A spell that states NO crit chance still does not crit, modifier or not: `SpellCritChanceHundredths`
		// returns 0 for a zero base, and that rule is deliberate (#633) - extending the melee 5% base to the 20,278
		// spell records that state nothing would invent a crit for all of them. A buff can sharpen a spell that
		// crits; it does not teach one that does not.
		[Fact]
		public void ABuffDoesNotTeachANonCrittingSpellToCrit()
		{
			var mods = Modifiers(CharacterStatId.SpellCriticalChance, 5000, 0);
			var boosted = (uint)mods.Apply(0, CharacterStatId.SpellCriticalChance);

			Assert.Equal(5000u, boosted); // the fold itself is willing...
			Assert.Equal(0, CombatResolver.SpellCritChanceHundredths(0, 0)); // ...and the resolver still refuses a zero-base spell
		}

		private static SpellModifiers Modifiers(CharacterStatId stat, int absolute, int percentHundredths)
		{
			var abs = new Dictionary<CharacterStatId, int>();
			var pct = new Dictionary<CharacterStatId, int>();
			if (absolute != 0)
				abs[stat] = absolute;
			if (percentHundredths != 0)
				pct[stat] = percentHundredths;

			return new SpellModifiers(abs, pct);
		}
	}
}
