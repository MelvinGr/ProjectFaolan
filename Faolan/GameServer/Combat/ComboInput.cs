using System;
using System.Collections.Generic;

namespace Faolan.GameServer.Combat
{
	// The canonical combat-input directions a combo step requires. AoC combos are a SEQUENCE of directional melee
	// swings the player performs in order (Combo.Actions, e.g. "UR,UP"); completing the sequence fires the finisher.
	// The five directional keys (the four quadrants + overhead) dominate the catalog; Head/Thrust are rare specials.
	// (The on-wire direction code carried by each NormalAttack maps INTO this enum - that mapping is pinned from a
	// capture separately; this enum is the wire-independent vocabulary the Combo.Actions field parses into.)
	public enum ComboInput
	{
		UpperLeft,
		UpperRight,
		LowerLeft,
		LowerRight,
		Up, // overhead
		Head,
		Thrust
	}

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
		private static readonly HashSet<string> NonDirectional = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"action",
			"MagicCombo",
			"Magic_Reactive",
			"Melee_Reactive",
			"Combo_Reactive"
		};

		// Parses the comma-separated Actions into the ComboInput sequence. Returns false (and an empty list) when the
		// combo is non-directional (reactive/magic/placeholder) or when a token is unrecognised - callers only match
		// directional combos.
		public static bool TryParse(string actions, out IReadOnlyList<ComboInput> sequence)
		{
			var list = new List<ComboInput>();
			sequence = list;
			if (string.IsNullOrWhiteSpace(actions))
				return false;

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

			return list.Count > 0;
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
