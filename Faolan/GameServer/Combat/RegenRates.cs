using System;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     The out-of-combat vitals REGEN rates, per second, in DISPLAY units (#442).
	///     <para>
	///         These are not tuned guesses. Each coefficient is a named constant out of the client's own tweak table (RDB
	///         1000516, <c>docs/rdb/client-tweak-table.md</c>) and every result is confirmed byte-for-byte against retail's
	///         own <c>RegenIIR</c> (0x28D0AC99) deltas in the corpus, which carry the rate ×100 - so the corpus pins these
	///         formulas to two decimals, with no rounding slack:
	///     </para>
	///     <list type="table">
	///         <item>
	///             <term>NecroL80 (L80 Necromancer, Con 375 / Str 70)</term>
	///             <description>0.15 × 445 + 20 = 86.75 → captured hp delta <c>8675</c></description>
	///         </item>
	///         <item>
	///             <term>DemoL80 (L80 Demonologist, Con 345 / Str 70)</term>
	///             <description>0.15 × 415 + 20 = 82.25 → captured <c>20353</c> = 8225 + the same 12128 buff term NecroL80's
	///                 <c>20803</c> = 8675 + 12128 carries, so its base is 8225</description>
	///         </item>
	///         <item>
	///             <term>AssassinL23 (L23 Assassin, Con 56 / Str 20 / Dex 79)</term>
	///             <description>health 0.15 × 76 + 20 = 31.40 → <c>3140</c>; stamina 0.15 × 155 + 10 = 33.25 → <c>3325</c>;
	///                 mana 0.38 × (Int 39 + Wis 26) = 24.70 → <c>2470</c></description>
	///         </item>
	///     </list>
	///     <para>
	///         The rate is PER SECOND: the median <c>RegenIIR</c> inter-arrival across all 1502 captured packets is 1.00 s
	///         (min 0.77, and the gaps above that are pools already full, not a slower cadence). The character sheet's
	///         "Non Combat Health Regen" row is the same number - it reads the stat and prints <c>value / 100</c> with an
	///         integer divide (client <c>FUN_016d9cb0</c> case 0x116), which is what pins the constants exactly: 86.75 and
	///         82.25 both display as their floor, so a rounding reading of the sheet gives the wrong constant (19.5) and
	///         the truncating one gives 20.
	///     </para>
	///     <para>
	///         The client computes the sheet's row the same way, from its own copy of the same tweaks (verified live:
	///         <c>FUN_0111a520</c> for health / <c>FUN_0111a6e0</c> for stamina read <c>0x02D244A0</c>/<c>B0</c> and
	///         <c>E0</c>/<c>F0</c>/<c>500</c>, all 0.15 in the running process). Its form is
	///         <c>stat(0x116) + Σ mod × (attr − 10) × 0.1 × 100</c>: the pool's regen stat's own value, plus an attribute
	///         "trickledown" on top (hence the sheet's <c>HealthOOCRegenTrickledown</c> tooltip). With attributes at the
	///         ×10 wire scale that reduces to exactly the formulas above -
	///         <c>2030 + 15 × (Con + Str − 2) = 2000 + 15 × (Con + Str)</c> - so the flat term is not a mystery constant,
	///         it is that base stat, and 2030 / 1045 are what the wire carries for an entity with no attributes above the
	///         baseline.
	///     </para>
	///     <para>
	///         ⚠️ The rates need the character's TOTAL attribute scores, which the client computes locally: retail sends
	///         Strength/Constitution/… as a flat 10 in the snapshot stat sheet, so the class/level attribute BASE is not
	///         on the wire and is not in any of the client's 10005xx data tables either (open work tracked in Gitea #444).
	///         Until it is decoded the server only knows the gear + buff part, so a geared character regenerates slower
	///         than retail - the shape and the constants are right, one input is short.
	///     </para>
	/// </summary>
	public static class RegenRates
	{
		// TWK_OOCHealthRegenConModifier / TWK_OOCHealthRegenStrModifier, and the same 0.15 on all three
		// TWK_OOCStaminaRegen{Con,Str,Dex}Modifier. The CLIENT reads these too, to compute the character sheet's row -
		// see the class remark on how its form reduces to this one.
		public const double OutOfCombatAttributeModifier = 0.15;

		// TWK_OOCManaRegenIntModifier / TWK_OOCManaRegenWisModifier.
		public const double OutOfCombatManaAttributeModifier = 0.38;

		// The flat term each pool adds on top of its attribute sum. NOT in the tweak table (there is no
		// TWK_*RegenBase); it is the pool's regen stat's OWN base value, which the client adds the attribute
		// "trickledown" onto (see the class remark) - health's is 2030 and stamina's 1045 in the ×100 wire scale, the
		// bare values that recur as regen deltas all over the corpus for entities whose attributes are all at the
		// baseline 10. Expressed here in display units, folded with the ×(attr − 1) offset the client's form carries.
		public const double HealthFlatRate = 20.0;
		public const double StaminaFlatRate = 10.0;
		public const double ManaFlatRate = 0.0; // AssassinL23's 2470 = 0.38 x 65 exactly, with nothing added

		// Health: Constitution + Strength. Halted in combat by the caller (retail's OOC vs Natural split).
		public static double HealthPerSecond(uint constitution, uint strength)
		{
			return OutOfCombatAttributeModifier * (constitution + strength) + HealthFlatRate;
		}

		// Stamina: Constitution + Strength + Dexterity. Keeps ticking in combat - it is the action resource, and the
		// tweak table has no "Natural" health twin but does have Natural stamina/mana modifiers.
		public static double StaminaPerSecond(uint constitution, uint strength, uint dexterity)
		{
			return OutOfCombatAttributeModifier * (constitution + strength + dexterity) + StaminaFlatRate;
		}

		// Mana: Intelligence + Wisdom, at the higher 0.38 modifier.
		public static double ManaPerSecond(uint intelligence, uint wisdom)
		{
			return OutOfCombatManaAttributeModifier * (intelligence + wisdom) + ManaFlatRate;
		}

		// A per-second display rate turned into the whole DISPLAY points gained over `elapsedSeconds`. The wire scale is
		// ×100, so a fractional rate is not lost: the caller multiplies by 100 for the RegenIIR delta. Never negative.
		public static uint Gain(double perSecond, double elapsedSeconds)
		{
			return perSecond <= 0 || elapsedSeconds <= 0
				? 0u
				: (uint)Math.Max(0, (int)Math.Round(perSecond * elapsedSeconds));
		}
	}
}
