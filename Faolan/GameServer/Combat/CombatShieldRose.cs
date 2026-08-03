using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Network.Codecs.Combat;

namespace Faolan.GameServer.Combat
{
	// Directional combat (#20): the defender's blue shield rose - how many of its three guards cover each arc, how that
	// distribution moves during a fight, and what a swing into a covered arc costs the attacker.
	//
	// Policy, not encoding: `CombatShieldCodec` owns the wire record and `CombatShieldDistribution` the digit packing;
	// this is the live behaviour the simulation drives, so it stays here (the `GameClock` / `RemoteFormatSession`
	// split, #530 rule 8).
	//
	// EVERY number below is measured, not invented:
	//   * the mitigation table is the client's own tweak table (RDB 1000516, `--report rdb-tweaks --report-arg Shield`):
	//     `Combat_PvE{0..3}ShieldBonus` = 0.2 / 0 / -0.33 / -0.66 and `Combat_PvP{0..3}ShieldBonus` =
	//     0.2 / 0 / -0.15 / -0.30. The client REGISTERS those eight tweaks and never reads them (Ghidra
	//     `TweakGlobals`: reads=0 on every one), which is the same tell recast had - they are the SERVER's numbers,
	//     shipped in the client's balance sheet.
	//   * the rest is the capture corpus: 110 `CombatShieldIIR` records over 71 engaged NPCs across all captures.
	public static class CombatShieldRose
	{
		// Every distribution in the corpus sums to exactly three - a defender guards with three shields, spread over
		// the arcs.
		public const uint TotalShields = 3;

		// A shift moves ONE shield to another arc. 34 of the 39 captured transitions move exactly one (4 re-send the
		// same value unchanged; the single 3-shield jump is `030 -> 003`, which reads as a lossy capture collapsing
		// consecutive steps rather than a distinct move size).
		public const uint ShieldsMovedPerShift = 1;

		// The shift cadence while engaged, from the 39 captured intervals: a hard floor at 5.0s, 34 of them inside
		// [5, 12] and the five above (14.2 / 18.1 / 28.3 / 48.0) consistent with dropped packets. Median 7.3s.
		public const double MinShiftSeconds = 5.0;
		public const double MaxShiftSeconds = 12.0;

		// The three `Normal Attack` direction abilities the client puts on the swing tail, named by the LDB. The two
		// LOWER ids (769096/769097) exist in the ability catalog with slot 0 and appear in no capture, so they are
		// mapped for completeness but never drive a live swing.
		private const uint UpperLeftAttack = 769094;
		private const uint UpperRightAttack = 769095;
		private const uint LowerLeftAttack = 769096;
		private const uint LowerRightAttack = 769097;
		private const uint OverheadAttack = 769099;

		// `Combat_PvE{n}ShieldBonus` / `Combat_PvP{n}ShieldBonus`, indexed by the shields covering the struck arc.
		// A POSITIVE entry is a bonus to the ATTACKER: an arc with no guard on it takes 20% more, which is what makes
		// re-aiming at the weakest arc the point of the system.
		private static readonly double[] PvEShieldBonus = [0.2, 0.0, -0.33, -0.66];
		private static readonly double[] PvPShieldBonus = [0.2, 0.0, -0.15, -0.30];

		// The ten ways three shields spread over three arcs. The corpus's 71 opening distributions land on nine of the
		// ten (only `120` never opens) at 15/12/11/9/8/5/5/4/2 - flat enough that a uniform pick over the compositions
		// fits (chi-square 2.8) and a per-shield multinomial does not (9.3, over-predicting `111` and under-predicting
		// the single-arc stacks).
		private static readonly uint[] Compositions = BuildCompositions();

		// The opening distribution an NPC engages with.
		public static uint Roll(Random random)
		{
			ArgumentNullException.ThrowIfNull(random);
			return Compositions[random.Next(Compositions.Length)];
		}

		// One shift: take a shield off an arc that has one and put it on a different arc. Returns `value` unchanged
		// when it does not decode to `TotalShields` shields (nothing to move).
		public static uint Shift(uint value, Random random)
		{
			ArgumentNullException.ThrowIfNull(random);
			var counts = Counts(value);
			if (counts.Sum(c => c) != TotalShields)
				return value;

			var from = counts.Select((count, arc) => (count, arc)).Where(x => x.count > 0).ToArray();
			var source = from[random.Next(from.Length)].arc;
			var destinations = Enumerable.Range(0, counts.Length).Where(arc => arc != source).ToArray();
			var destination = destinations[random.Next(destinations.Length)];

			counts[source] -= ShieldsMovedPerShift;
			counts[destination] += ShieldsMovedPerShift;
			return CombatShieldDistribution.Pack(counts[0], counts[1], counts[2]);
		}

		// A distribution the CLIENT sent up (#620), reduced to one the client would itself render. The player owns their
		// own rose - `AgeOfConan.exe` FUN_01117a20 moves a guard on the shield keybinds, writes stat 0xA5 and uploads the
		// result - so this value is the player's claim, not ours, and it is the one wire number in the system we do not
		// author. The client applies exactly one rule to an incoming spread (FUN_0113de90, the CombatShieldIIR Activate):
		// re-read the three digits and, when they sum above TotalShields, write DefaultDistribution back. Applying the
		// same rule here keeps a hand-built packet from handing a player a `999` rose that the client would never draw.
		public static uint Sanitize(uint distribution)
		{
			var counts = Counts(distribution);
			return distribution > 999 || counts.Sum(c => c) > TotalShields ? CombatShieldCodec.DefaultDistribution : distribution;
		}

		// Seconds until this defender shifts again.
		public static double NextShiftSeconds(Random random)
		{
			ArgumentNullException.ThrowIfNull(random);
			return MinShiftSeconds + random.NextDouble() * (MaxShiftSeconds - MinShiftSeconds);
		}

		// The arc a swing lands in, or null when the ability is not one of the five directional attacks (a combo step,
		// a spell, an unarmed jab) - those are undirected and take no arc modifier.
		public static ShieldArc? ArcFor(uint abilityId)
		{
			return abilityId switch
			{
				UpperLeftAttack or LowerLeftAttack => ShieldArc.Left,
				UpperRightAttack or LowerRightAttack => ShieldArc.Right,
				OverheadAttack => ShieldArc.Mid,
				_ => null
			};
		}

		// The damage multiplier for a swing into `arc` against `distribution`. 1.0 when the swing carries no direction,
		// and 1.0 for an UNROLLED rose (`distribution` 0) - a defender that never engaged has no arcs at all, which is
		// not the same as one standing with three bare arcs (that would hand the attacker the +20% unguarded bonus).
		public static double DamageMultiplier(uint distribution, ShieldArc? arc, bool playerVersusPlayer = false)
		{
			if (arc is not { } struck || distribution == 0)
				return 1.0;

			var table = playerVersusPlayer ? PvPShieldBonus : PvEShieldBonus;
			var shields = (int)Math.Min(CombatShieldDistribution.Count(distribution, struck), (uint)table.Length - 1);
			return 1.0 + table[shields];
		}

		// Applies the arc modifier to a resolved damage number, never dropping a landed hit below 1.
		public static uint ApplyArcMitigation(uint damage, uint distribution, ShieldArc? arc, bool playerVersusPlayer = false)
		{
			var scaled = (int)Math.Round(damage * DamageMultiplier(distribution, arc, playerVersusPlayer));
			return (uint)Math.Max(damage == 0 ? 0 : 1, scaled);
		}

		private static uint[] BuildCompositions()
		{
			var compositions = new List<uint>();
			for (var left = 0u; left <= TotalShields; left++)
			for (var mid = 0u; mid <= TotalShields - left; mid++)
				compositions.Add(CombatShieldDistribution.Pack(left, mid, TotalShields - left - mid));

			return compositions.ToArray();
		}

		private static uint[] Counts(uint value)
		{
			var (left, mid, right) = CombatShieldDistribution.Unpack(value);
			return [left, mid, right];
		}
	}
}
