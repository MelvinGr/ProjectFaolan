using System;
using System.Collections.Generic;

namespace Faolan.GameServer.Combat
{
	// Parses the (inconsistently-formatted) Combo.Actions field into a canonical ComboInput sequence. The catalog
	// XML uses many spellings for the same key - abbreviations ("UR"), engine names ("CombatUpperRight"), spaced
	// ("Upper Right"), cased and step-numbered ("1_Combatupperleft") - so this normalises them all. Non-directional
	// markers ("action", "MagicCombo", "Magic_Reactive"/"Melee_Reactive"/"Combo_Reactive") flag a combo that is NOT
	// performed by a directional key sequence (spellweaving / reactive procs); TryParse returns false for those.
	public static class ComboActionParser
	{
		private static readonly Dictionary<string, ComboInput> Map = new(StringComparer.OrdinalIgnoreCase)
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
		private static readonly HashSet<string> NonDirectional = new(StringComparer.OrdinalIgnoreCase)
		{
			"action",
			"MagicCombo",
			"Magic_Reactive",
			"Melee_Reactive",
			"Combo_Reactive"
		};

		// Parses the comma-separated Actions into the ComboInput sequence. Returns false AND an empty sequence when
		// the combo is non-directional (reactive/magic/placeholder) or when a token is unrecognised - callers only
		// match directional combos.
		//
		// The empty-on-failure half was only in this comment: the old code returned the partially-filled list, so a
		// combo whose second key was unrecognised handed back a valid-looking ONE-key sequence. A caller that
		// matched on the sequence without checking the bool would have matched the wrong combo.
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

				if (NonDirectional.Contains(token))
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

		// True when the value carries a non-directional marker - i.e. TryParse returned false because the combo is
		// deliberately not key-sequenced, rather than because it carried a spelling we failed to recognise. TryParse
		// returns false for both, so this is what separates "accounted for" from "unknown" (ComboActionParserTests).
		//
		// The marker is a PREFIX, not the whole value: every one of the 21 non-directional rows in the catalog reads
		// `<marker>,<direction...>` - "MagicCombo,Combatlowerleft", "Magic_Reactive,CombatHead",
		// "Combo_Reactive,Combatupperright". So the marker tags HOW the combo triggers and a real directional
		// sequence still follows it, which this parser currently discards. Whether the client uses that direction for
		// a spellweave/reactive proc is undecoded - Gitea #455. [V0]
		public static bool IsNonDirectional(string actions)
		{
			if (string.IsNullOrWhiteSpace(actions))
				return false;

			foreach (var raw in actions.Split(','))
			{
				var token = StripStepPrefix(raw.Trim());
				if (token.Length > 0 && NonDirectional.Contains(token))
					return true;
			}

			return false;
		}

		// A few catalog rows number the step, e.g. "1_Combatupperleft" / "2_Combatlowerleft" - drop the "N_" prefix.
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
