using System;
using System.Collections.Generic;
using System.Linq;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     A combo's name states what it does.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The client's combo catalog names a player combo
	///         <c>&lt;Class&gt;_&lt;level&gt;[_&lt;expansion&gt;]_&lt;category&gt;_&lt;Name&gt;</c> -
	///         <c>Guardian_20b_knockback_Shield_Sweep_II</c>, <c>Barbarian_17_evadedebuff_Staggering_Blow_I</c>,
	///         <c>Halberdier_80_khitai_knockback_Stagger_V</c> - and the category token is a small closed vocabulary
	///         across the 675 player combos: <c>knockback</c> (30), <c>snare</c> (13), <c>stun</c> (6),
	///         <c>manaburn</c> (7), <c>armorbreak</c> (7), the taunt/detaunt family, the dots and the multihits.
	///     </para>
	///     <para>
	///         That matters because a combo's own spell says nothing about these: every stagger combo resolves to a
	///         <c>Stagger I..V</c> / <c>Staggering Blow I..IV</c> record with a zero duration and no stat effects, so
	///         the name is the only place the effect family is stated.
	///     </para>
	///     <para>
	///         Match on the token with its underscores rather than by position. The position is not fixed - the Khitai
	///         combos carry an expansion token that pushes the category one slot along
	///         (<c>Halberdier_80_khitai_knockback_...</c>) - and only 422 of the 675 player names match the plain
	///         four-token shape at all, the weapon-special rows (<c>2h_Sword_Wheel_of_Death</c> and friends) being a
	///         different grammar entirely.
	///     </para>
	/// </remarks>
	public static class ComboEffect
	{
		// The categories with a live consumer. Deliberately not the whole vocabulary: a token here is a claim that
		// something happens when the combo lands, and listing one nothing acts on would read as implemented.
		private const string KnockbackToken = "_knockback_";

		/// <summary>
		///     Whether this combo's own name marks it a knockback - <c>Shield Sweep</c>, <c>Clobber</c>,
		///     <c>Ferocious Smack</c>, <c>Batter Aside</c>, <c>Stagger</c>, <c>Volley Backwards</c>: 30 of the 675
		///     player combos, across six classes.
		/// </summary>
		/// <remarks>
		///     The count is 30 rather than 25 because this matches the token rather than a slot. A census reading
		///     <c>parts[2]</c> finds 25 and silently misses the rank-V Khitai combo of five of the six ladders
		///     (<c>Barbarian_80_khitai_knockback_Clobber_V</c>), the top ability of each.
		/// </remarks>
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
		///     walks past those rather than indexing a fixed slot. Nothing consumes it beyond
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

		// Tokens that sit where the category would and are not one. Only Khitai's is observed (58 combos); it is a set
		// so a second expansion is one line rather than a re-read of the grammar.
		private static readonly HashSet<string> Expansions = new(StringComparer.OrdinalIgnoreCase) { "khitai" };
	}
}
