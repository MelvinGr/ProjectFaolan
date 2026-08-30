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
	///                 mana 0.38 × (Int 39 + Wis 26) + 0 = 24.70 → <c>2470</c></description>
	///         </item>
	///         <item>
	///             <term>Hakotep (L24 Herald of Xotli, Int 118 / Wis 33, class mana base 20.00)</term>
	///             <description>mana 0.38 × 151 + 20 = 77.38 → captured <c>7738</c> - the second, independent
	///                 confirmation of the mana branch, and the one that pins its base as class data rather than 0</description>
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
	///         ×10 wire scale that is exactly the formulas above, because the wire scale is <c>display × 10 + 10</c> - the
	///         client seeds every attribute to 10 before applying anything, so its <c>(raw − 10) × 0.1</c> is just a
	///         conversion back to display units. And <c>stat(0x116)</c> is the flat term: the client seeds stat 278 to
	///         <b>2000</b> and 279 to <b>1000</b>, i.e. display 20.00 and 10.00. So neither the modifiers nor the flat
	///         terms are fitted - the server's formula and the client's display are the same computation.
	///     </para>
	///     <para>
	///         The rates need the character's TOTAL attribute scores, which the client computes locally: retail sends
	///         Strength/Constitution/… as a flat 10 in the snapshot stat sheet, so the class/level attribute BASE is not
	///         on the wire and is not in any of the client's 10005xx data tables either. It IS decoded now - the RDB's
	///         per-class, per-level class-feat templates (#444, <c>ClassAttributeBaseModule</c>) - and reaches this
	///         through <c>WorldEntity.AttributeScore</c>, so a geared character no longer regenerates short.
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

		// The flat term each pool adds on top of its attribute sum. NOT in the tweak table (there is no TWK_*RegenBase);
		// it is the pool's regen stat's OWN seeded value, which the client adds the attribute "trickledown" onto (see the
		// class remark). Read straight off the client: stat 278 → 2000 and 279 → 1000 in the ×100 wire scale, i.e. the
		// display 20.00 / 10.00 below, and identical across all four characters observed - including a Necromancer with
		// no stamina pool at all.
		public const double HealthFlatRate = 20.0; // stat 278 seeds to 2000
		public const double StaminaFlatRate = 10.0; // stat 279 seeds to 1000

		// Mana has NO universal flat term - unlike health and stamina, its base is CLASS data, the class's own value for
		// stat 280 (Non Combat Mana Regen). Observed: never written at all for a level 23 Assassin (so 0), 2000 for a
		// level 24 Herald of Xotli, and 2200 for both level 80 mage classes (a 2000 seed plus 200 from their class
		// template). Callers pass it; 0 is the no-mana-base default, NOT a claim that every class has none.
		//
		// ⭐ All three of those observations are now SOURCED rather than merely recorded (#863). The 2000 is the
		// auto-trained `Mage` feat (Necromancer / Herald of Xotli / Demonologist) and its `Priest` twin, which the
		// CLIENT trains off its featclass table at level 1 and never puts on the wire; the 200 is the class
		// progression template, already in ClassAttributeBases. The Assassin's class trains neither, hence nothing.
		// `WorldEntity.NonCombatManaRegenBase` is the sum, and the simulation passes it here.
		public const double ManaFlatRate = 0.0;

		// ⭐ The CEILING the attribute+flat sum is held under, as a share of the character's MAX health per second.
		//
		// Measured off the corpus (#902): retail's own-handle `RegenIIR` ticks at a median 1.01s and carries a median
		// 3.90% of the character's max health per second - p25 3.07%, p75 4.63%. That share is the rule rather than a
		// coincidence of one pool size: the captured pools span **17.2x** (13,800 to 237,100 wire) and the
		// share-of-pool reading is the TIGHTER of the two (CV 0.49 against 0.75 for a flat HP/s), which is the
		// comparison that makes it identifiable at all.
		//
		// ⚠️ It is a CAP, not a replacement, because the formula below is already right where retail was captured. Every
		// capture is a level 23-80 character, and there the attribute term dominates: a level-80 Necromancer computes
		// 106 HP/s against the 92 HP/s the corpus measures, i.e. 4.5% of its 2,371 pool - just under this ceiling, so
		// nothing about #442's verified shape changes.
		//
		// ⛔ What it bites is the LOW-LEVEL band, which no capture covers. `HealthFlatRate` is a flat 20 HP/s read off
		// the client's own stat 278, and a flat term does not scale down: on a level-4's 202-point pool it alone is
		// 10% of the bar PER SECOND (a full bar in 9s), against the 3.9% that holds across every captured character.
		// Capping at the observed p75 leaves the verified band untouched and pulls the unverified one into the measured
		// range, which is the conservative direction when the evidence stops before the level you are standing on.
		public const double HealthRegenMaxFractionPerSecond = 0.0463;

		// Health: Constitution + Strength. Halted in combat by the caller (retail's OOC vs Natural split).
		public static double HealthPerSecond(uint constitution, uint strength, uint maxHealth = 0)
		{
			var rate = OutOfCombatAttributeModifier * (constitution + strength) + HealthFlatRate;

			return maxHealth == 0
				? rate // no pool stated: the historical rate, unchanged
				: Math.Min(rate, maxHealth * HealthRegenMaxFractionPerSecond);
		}

		// Stamina: Constitution + Strength + Dexterity. Keeps ticking in combat - it is the action resource, and the
		// tweak table has no "Natural" health twin but does have Natural stamina/mana modifiers.
		public static double StaminaPerSecond(uint constitution, uint strength, uint dexterity)
		{
			return OutOfCombatAttributeModifier * (constitution + strength + dexterity) + StaminaFlatRate;
		}

		// Mana: Intelligence + Wisdom, at the higher 0.38 modifier, plus the CLASS's mana-regen base in display units
		// (see ManaFlatRate - the class's stat 280, which the server cannot source yet, so it is 0 in practice today).
		public static double ManaPerSecond(uint intelligence, uint wisdom, double classManaBase = ManaFlatRate)
		{
			return OutOfCombatManaAttributeModifier * (intelligence + wisdom) + classManaBase;
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
