using System.Collections.Generic;

namespace Faolan.Core.Progression
{
	/// <summary>
	///     The death-penalty debuff a player picks up when they die - the "Death Penalty 1/2/3" spells the annotated
	///     captures name outright (<c>death penalty 1 debuff after death</c> / <c>got death penalty 2 debuf</c> /
	///     <c>got death penalty 3 debuff</c>).
	/// </summary>
	/// <remarks>
	///     <para>
	///         The tiers stack - they are not one debuff that escalates. That is what the wire shows: in
	///         <c>chat-annotated-4</c> the playfield reload at 20:24:36 bulk re-applies the player's surviving buffs
	///         with their remaining durations, and both <c>Death Penalty 1</c> (1,261,960 ms left of 1,800,000) and
	///         <c>Death Penalty 2</c> (1,775,260 ms left) are in the same batch - two independent countdowns from two
	///         separate deaths, ~8.9 min and ~28 s old. The lava death at 20:26:01 then adds <c>Death Penalty 3</c> on
	///         top. All tiers can be active simultaneously.
	///     </para>
	///     <para>
	///         The "tier" is therefore how many death penalties the player is already carrying: the Nth concurrent one
	///         is the spell named "Death Penalty N". Nothing tracks a death counter - the live buff set is the
	///         counter, which is also why the penalty fades on its own as each 30-minute stack lapses.
	///     </para>
	///     <para>
	///         All three rows are identical in <c>faolan.db</c> - 1,800,000 ms (30 min) and, per stack, Magic Damage
	///         Modifier -100, Damage Multiplier -1 and Damage Mitigation -100 - so three stacks is three times the
	///         penalty, applied by the existing buff-stat sync.
	///     </para>
	///     <para>
	///         Retail applies the new penalty atomically with stripping every other buff: at the 20:24:08 death, one
	///         batch carries the <c>Death Penalty 2</c> apply and the end-twins of all four buffs the player was
	///         holding. The apply therefore has to come after our own death-strip, not before it, or the strip removes
	///         the penalty it just granted.
	///     </para>
	/// </remarks>
	public static class DeathPenalty
	{
		/// <summary>
		///     Spell ids by stack depth: the first death gives <c>Death Penalty 1</c> (2544), the second adds 2543, the
		///     third adds 2542. Descending ids, which is why this is a table and not arithmetic.
		/// </summary>
		public static readonly uint[] TierSpellIds = [2544, 2543, 2542];

		/// <summary>Every death-penalty spell id, for testing whether a live buff is one.</summary>
		public static bool IsDeathPenalty(uint spellBaseId)
		{
			foreach (var id in TierSpellIds)
			{
				if (id == spellBaseId)
					return true;
			}

			return false;
		}

		/// <summary>
		///     The penalty to apply to a player who is already carrying <paramref name="activeSpellBaseIds" />, or 0
		///     when they already hold every tier (a fourth death inside 30 minutes adds nothing - the corpus never
		///     shows a "Death Penalty 4").
		/// </summary>
		public static uint NextTier(IEnumerable<uint> activeSpellBaseIds)
		{
			var held = 0;
			foreach (var id in activeSpellBaseIds)
			{
				if (IsDeathPenalty(id))
					held++;
			}

			return held < TierSpellIds.Length ? TierSpellIds[held] : 0;
		}
	}
}
