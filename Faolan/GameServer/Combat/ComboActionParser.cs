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

		// True when the value carries a non-directional marker - i.e. TryParse returned false because the combo is
		// deliberately not key-sequenced, rather than because it carried a spelling we failed to recognise. TryParse
		// returns false for both, so this is what separates "accounted for" from "unknown" (ComboActionParserTests).
		public static bool IsNonDirectional(string actions)
		{
			return TryParseMarker(actions, out _);
		}

		// The trigger family a marked combo carries, if any. False for a plain directional sequence and for a blank
		// Actions.
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

		// The direction a MARKED combo is keyed to (#455). The marker is a PREFIX, not the whole value: the four real
		// markers each read `<marker>,<direction>`, and the direction is a SELECTOR - which of the family's five procs
		// this row is - not a step in a key sequence.
		//
		// The catalog proves it: the marked rows are a complete 4 x 5 grid, each cell present EXACTLY ONCE, and the
		// three reactive families share one GameActionId per column:
		//     CombatHead        -> 231964 Debilitating Cleave   | MagicCombo: 700777 Exhaustion
		//     Combatupperleft   -> 227061 Rage                  |             700755 Mana Leech
		//     Combatupperright  -> 227064 Furious Blow          |             700751 Discourage
		//     Combatlowerleft   -> 227060 Clumsy                |             700749 Boiling Mana
		//     Combatlowerright  -> 227063 Weaken                |             700791 Mind Purge
		// Each row's own Name is that spell's name (`Clumsy_combo` / `Clumsy_magic` / `Clumsy`), so the family says
		// WHAT triggers the proc (a completed combo / a spell / a melee hit) and the direction says WHICH proc. A key
		// SEQUENCE could never come out as a perfect grid with shared ids down the columns.
		//
		// `action` (69 rows) is the odd one out - the NPC-combo marker, with no direction after it - so it returns
		// false here while still being a known marker above.
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
