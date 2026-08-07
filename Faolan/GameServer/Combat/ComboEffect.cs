using System;
using System.Collections.Generic;
using System.Linq;

namespace Faolan.GameServer.Combat
{
	// ⭐ A combo's NAME states what it does. The client's combo catalog names a player combo
	// `<Class>_<level>[_<expansion>]_<category>_<Name>` - `Guardian_20b_knockback_Shield_Sweep_II`,
	// `Barbarian_17_evadedebuff_Staggering_Blow_I`, `Halberdier_80_khitai_knockback_Stagger_V` - and the category
	// token is a small closed vocabulary across the 675 player combos: `knockback` (30), `snare` (13), `stun` (6),
	// `manaburn` (7), `armorbreak` (7), the taunt/detaunt family, the dots, the multihits.
	//
	// That matters because a combo's own SPELL says nothing about these: every stagger combo resolves to a
	// `Stagger I..V` / `Staggering Blow I..IV` record with a zero duration and NO stat effects, so the name is the
	// only place the effect family is stated at all.
	//
	// ⚠️ Matched on the token WITH its underscores rather than by position. The position is not fixed - the Khitai
	// combos carry an expansion token that pushes the category one slot along (`Halberdier_80_khitai_knockback_...`),
	// and only 422 of the 675 player names match the plain four-token shape at all (the weapon-special rows,
	// `2h_Sword_Wheel_of_Death` and friends, are a different grammar entirely).
	public static class ComboEffect
	{
		// The categories with a live consumer. Deliberately NOT the whole vocabulary: a token here is a claim that
		// something happens when the combo lands, and listing one we do not act on would read as implemented.
		private const string KnockbackToken = "_knockback_";

		/// <summary>
		///     Whether this combo's own name marks it a KNOCKBACK - `Shield Sweep`, `Clobber`, `Ferocious Smack`,
		///     `Batter Aside`, `Stagger`, `Volley Backwards`. 30 of the 675 player combos, across six classes.
	///     <para>
	///         ⚠️ 30, not 25 - and the difference is the reason this matches the token rather than a slot. A census
	///         that read `parts[2]` finds 25 and silently misses the rank-V Khitai combo of five of the six ladders
	///         (`Barbarian_80_khitai_knockback_Clobber_V`), i.e. the TOP ability of each.
	///     </para>
		/// </summary>
		public static bool IsKnockback(string? comboName)
		{
			return comboName != null && comboName.Contains(KnockbackToken, StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		///     The category token of a combo whose name carries one, for logs and reports - null for the weapon
		///     specials and the NPC rows, whose names are a different grammar.
		/// </summary>
		/// <remarks>
		///     The token is the first name part that is neither the class, the level nor an expansion marker, so this
		///     walks past those rather than indexing a fixed slot. Nothing consumes it yet beyond
		///     <see cref="IsKnockback" />; it exists so a census can be taken without re-deriving the grammar.
		/// </remarks>
		public static string? Category(string? comboName)
		{
			if (comboName == null)
				return null;

			var parts = comboName.Split('_');
			if (parts.Length < 4 || comboName.StartsWith("combo_npc", StringComparison.OrdinalIgnoreCase))
				return null;

			// [0] class, [1] level (digits, optionally with a variant letter). Anything else is not this grammar.
			if (!parts[1].TakeWhile(char.IsDigit).Any() || !parts[1].All(c => char.IsDigit(c) || char.IsLower(c)))
				return null;

			return Expansions.Contains(parts[2]) && parts.Length >= 5
				? parts[3]
				: parts[2];
		}

		// Tokens that sit where the category would and are not one. Only Khitai's is observed (58 combos); kept as a
		// set so a second expansion is one line rather than a re-read of the grammar.
		private static readonly HashSet<string> Expansions = new(StringComparer.OrdinalIgnoreCase) { "khitai" };
	}
}
