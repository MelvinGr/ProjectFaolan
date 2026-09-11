using System;
using System.Collections.Generic;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     Parses the inconsistently formatted <c>Combo.Actions</c> field into a canonical
	///     <see cref="ComboInput" /> sequence.
	/// </summary>
	/// <remarks>
	///     The catalog XML uses many spellings for the same key - abbreviations ("UR"), engine names
	///     ("CombatUpperRight"), spaced ("Upper Right"), cased and step-numbered ("1_Combatupperleft") - so this
	///     normalises them all. Non-directional markers ("action", "MagicCombo",
	///     "Magic_Reactive"/"Melee_Reactive"/"Combo_Reactive") flag a combo that is not performed by a directional key
	///     sequence (spellweaving or reactive procs); <see cref="TryParse" /> returns false for those.
	/// </remarks>
	public static class ComboActionParser
	{
		private static readonly Dictionary<string, ComboInput> Map = new Dictionary<string, ComboInput>(StringComparer.OrdinalIgnoreCase)
		{
			["UL"] = ComboInput.UpperLeft,
			["CombatUpperLeft"] = ComboInput.UpperLeft,
			["UpperLeft"] = ComboInput.UpperLeft,
			["Up Left"] = ComboInput.UpperLeft,
			["UpLeft"] = ComboInput.UpperLeft,
			["UR"] = ComboInput.UpperRight,
			["CombatUpperRight"] = ComboInput.UpperRight,
			["UpperRight"] = ComboInput.UpperRight,
			["Up Right"] = ComboInput.UpperRight,
			["UpRight"] = ComboInput.UpperRight,
			["LL"] = ComboInput.LowerLeft,
			["CombatLowerLeft"] = ComboInput.LowerLeft,
			["LowerLeft"] = ComboInput.LowerLeft,
			["Lower Left"] = ComboInput.LowerLeft,
			["LR"] = ComboInput.LowerRight,
			["CombatLowerRight"] = ComboInput.LowerRight,
			["LowerRight"] = ComboInput.LowerRight,
			["Lower Right"] = ComboInput.LowerRight,
			["UP"] = ComboInput.Up,
			["Up"] = ComboInput.Up,
			["Head"] = ComboInput.Head,
			["CombatHead"] = ComboInput.Head,
			["Thrust"] = ComboInput.Thrust,
			["CombatThrust"] = ComboInput.Thrust
		};

		// Non-directional Action tokens: the combo is triggered by a mechanic other than a key sequence (a reactive
		// proc, or a spellweaving magic combo), so it has no directional grammar to match.
		private static readonly Dictionary<string, ComboTrigger> Markers = new Dictionary<string, ComboTrigger>(StringComparer.OrdinalIgnoreCase)
		{
			["action"] = ComboTrigger.NpcCombo,
			["MagicCombo"] = ComboTrigger.MagicCombo,
			["Magic_Reactive"] = ComboTrigger.MagicReactive,
			["Melee_Reactive"] = ComboTrigger.MeleeReactive,
			["Combo_Reactive"] = ComboTrigger.ComboReactive
		};

		/// <summary>
		///     Parses the comma-separated <c>Actions</c> into the <see cref="ComboInput" /> sequence.
		/// </summary>
		/// <returns>
		///     False, with an empty <paramref name="sequence" />, when the combo is non-directional (reactive, magic or
		///     a placeholder) or when a token is unrecognised - callers only match directional combos.
		/// </returns>
		/// <remarks>
		///     The sequence must be left empty on failure rather than partially filled: a combo whose second key is
		///     unrecognised would otherwise hand back a valid-looking one-key sequence, and a caller that matched on
		///     the sequence without checking the result would match the wrong combo.
		/// </remarks>
		public static bool TryParse(string actions, out IReadOnlyList<ComboInput> sequence)
		{
			sequence = [];
			if (string.IsNullOrWhiteSpace(actions))
				return false;

			var list = new List<ComboInput>();
			foreach (var raw in actions.Split(','))
			{
				var token = StripStepPrefix(raw.Trim());
				if (token.Length == 0)
					continue;

				if (Markers.ContainsKey(token))
					return false;

				if (!Map.TryGetValue(token, out var input))
					return false; // unknown token - don't guess

				list.Add(input);
			}

			if (list.Count == 0)
				return false;

			sequence = list;
			return true;
		}

		/// <summary>
		///     True when the value carries a non-directional marker - that is, <see cref="TryParse" /> returned false
		///     because the combo is deliberately not key-sequenced rather than because its spelling was unrecognised.
		/// </summary>
		/// <remarks>
		///     <see cref="TryParse" /> returns false for both cases, so this is what separates "accounted for" from
		///     "unknown".
		/// </remarks>
		public static bool IsNonDirectional(string actions)
		{
			return TryParseMarker(actions, out _);
		}

		/// <summary>
		///     The trigger family a marked combo carries, if any. False for a plain directional sequence and for a
		///     blank <c>Actions</c>.
		/// </summary>
		public static bool TryParseMarker(string actions, out ComboTrigger trigger)
		{
			trigger = ComboTrigger.Directional;
			if (string.IsNullOrWhiteSpace(actions))
				return false;

			foreach (var raw in actions.Split(','))
			{
				var token = StripStepPrefix(raw.Trim());
				if (token.Length > 0 && Markers.TryGetValue(token, out trigger))
					return true;
			}

			return false;
		}

		/// <summary>
		///     The direction a marked combo is keyed to.
		/// </summary>
		/// <remarks>
		///     <para>
		///         The marker is a prefix, not the whole value: the four real markers each read
		///         <c>&lt;marker&gt;,&lt;direction&gt;</c>, and the direction is a selector - which of the family's
		///         five procs this row is - not a step in a key sequence. The catalog shows it: the marked rows are a
		///         complete 4 x 5 grid with each cell present exactly once, and the three reactive families share one
		///         GameActionId per column.
		///     </para>
		///     <code>
		///     CombatHead        -> 231964 Debilitating Cleave   | MagicCombo: 700777 Exhaustion
		///     Combatupperleft   -> 227061 Rage                  |             700755 Mana Leech
		///     Combatupperright  -> 227064 Furious Blow          |             700751 Discourage
		///     Combatlowerleft   -> 227060 Clumsy                |             700749 Boiling Mana
		///     Combatlowerright  -> 227063 Weaken                |             700791 Mind Purge
		///     </code>
		///     <para>
		///         Each row's own Name is that spell's name (<c>Clumsy_combo</c> / <c>Clumsy_magic</c> /
		///         <c>Clumsy</c>), so the family says what triggers the proc - a completed combo, a spell or a melee
		///         hit - and the direction says which proc.
		///     </para>
		///     <para>
		///         <c>action</c> (69 rows) is the exception, the NPC-combo marker with no direction after it, so it
		///         returns false here while still being a known marker.
		///     </para>
		/// </remarks>
		public static bool TryParseTriggered(string actions, out ComboTrigger trigger, out ComboInput direction)
		{
			direction = default;
			if (!TryParseMarker(actions, out trigger))
				return false;

			foreach (var raw in actions.Split(','))
			{
				var token = StripStepPrefix(raw.Trim());
				if (token.Length > 0 && Map.TryGetValue(token, out direction))
					return true;
			}

			return false;
		}

		// A few catalog rows number the step, e.g. "1_Combatupperleft" / "2_Combatlowerleft": drop the "N_" prefix.
		private static string StripStepPrefix(string token)
		{
			var us = token.IndexOf('_');
			if (us > 0 && us < token.Length - 1)
			{
				var head = token[..us];
				if (int.TryParse(head, out _))
					return token[(us + 1)..];
			}

			return token;
		}
	}
}
