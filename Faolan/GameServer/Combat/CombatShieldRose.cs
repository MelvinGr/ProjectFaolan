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

		// The five `Normal Attack` direction abilities the client puts on the swing tail, named by the LDB and in the
		// client's own enum order (`SwingDirection`). The two LOWER ids carry slot 0 in the catalog and appear on no
		// captured PLAYER swing - which is the retail unlock rather than an absence: a character swings in three
		// directions and gains the two lower zones later (`FUN_005edd90` folds them onto the upper zone on the same
		// side below level 40). Mobs are not gated and use all five.
		private static readonly uint[] DirectionAbilities = [769094, 769095, 769099, 769096, 769097];

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

		// The zone a swing came from, or null when the ability is not one of the five directional attacks (a combo
		// step, a spell, an unarmed jab) - those are undirected and take no arc modifier.
		public static SwingDirection? DirectionFor(uint abilityId)
		{
			var index = Array.IndexOf(DirectionAbilities, abilityId);
			return index < 0 ? null : (SwingDirection)index;
		}

		// The guard arc a zone is covered by. THE CLIENT'S OWN COLLAPSE, not a modelling choice: `FUN_005edd90`
		// rewrites `Lower Left -> Upper Left` and `Lower Right -> Upper Right`, so a lower zone belongs to the arc on
		// its own side. Five zones over three arcs is exactly why the rose stays three digits wide while the wire
		// alphabet is five.
		public static ShieldArc ArcOf(SwingDirection direction)
		{
			return direction switch
			{
				SwingDirection.UpperLeft or SwingDirection.LowerLeft => ShieldArc.Left,
				SwingDirection.UpperRight or SwingDirection.LowerRight => ShieldArc.Right,
				SwingDirection.Overhead => ShieldArc.Mid,
				_ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "unknown swing direction")
			};
		}

		// The arc a swing lands in, or null when the ability is undirected.
		public static ShieldArc? ArcFor(uint abilityId)
		{
			return DirectionFor(abilityId) is { } direction ? ArcOf(direction) : null;
		}

		// The arc an NPC's swing lands in (#621). An arc is the direction the ATTACKER SWINGS - left, overhead,
		// right - not where the attacker is standing: the live game's own description is "you could instantly stack
		// all of your shielding to your right side if you SEE AN ENEMY CHARGING UP A RIGHT-SIDE ATTACK", which is a
		// property of the swing, and the client's three direction abilities (`Upper Left` / `Overhead` / `Upper
		// Right Attack`) are exactly that on the player's side.
		//
		// A geometric reading was tried and is WRONG - the "attacks from behind" line in the same description is a
		// separate positional rule, not the arc split, and the client's 294-entry balance sheet carries no
		// back/flank number to go with it.
		//
		// ⚠️ An NPC's swing is NOT a different kind of thing from a player's - it is the same Left/Mid/Right, and
		// only that (the maintainer, 2026-08-05). Nothing about it is visible on the mob: the shields drawn over a defender
		// are a UI OVERLAY of the rose, not a model or animation state, so there is no left-swing/right-swing pose to
		// look for on either side.
		//
		// ⚠️ CORRECTED (#673). This used to say "not one of ~63k NPC-actor BCC records carries a direction word".
		// That is true of the direction ABILITY - a mob has none to name, and the actor-split census confirms word 5
		// is present on 515 player-actor swings and ZERO of 2,691 NPC-actor ones - but the swing states its direction
		// a SECOND way, in each hit result's own type word, and that IS on a mob's swing.
		//
		// Mitigation is server-side either way (the client registers all eight `Combat_{PvE,PvP}{0..3}ShieldBonus`
		// tweaks and reads none), so the server picks the zone. NO LONGER A PLACEHOLDER - both halves below are
		// measured over the 2,709 NPC-actor swings in the corpus (`--report bcc-attack-tail`):
		//
		//     Upper Left x545  Upper Right x511  Overhead x896  Lower Left x373  Lower Right x384
		//
		// ⭐ The five-zone alphabet was what made this look unfittable, and collapsing it onto the three ARCS - by
		// the client's own rule, a lower zone guarded by the arc on its own side - is what settled it:
		//
		//     Left 918 (33.9%)   Mid 896 (33.1%)   Right 895 (33.0%)   chi-square 0.37 on 2 df
		//
		// So the arc pick IS uniform, and always was - the apparent skew was the height axis the rose does not model.
		// Within the two side arcs the swing is upper 1,056 / lower 757, i.e. 58.2% upper (n=1,813, 7 sigma off an
		// even split, so the bias is real rather than sampling noise). WHY it leans upper is not explained here; it
		// is measured, not derived.
		//
		// ⚠️ It is NOT a per-mob repertoire: 55 of the 56 templates with >= 8 captured swings use all five zones, so
		// there is no "this archetype only swings the three" rule to key it on.
		private const double UpperShareOfASideArc = 0.582;

		// The zone an NPC's swing comes from: uniform over the three guarded arcs, then the height within a side arc.
		public static SwingDirection RollSwingDirection(Random random)
		{
			ArgumentNullException.ThrowIfNull(random);
			var upper = random.NextDouble() < UpperShareOfASideArc;
			return (ShieldArc)random.Next(3) switch
			{
				ShieldArc.Left => upper ? SwingDirection.UpperLeft : SwingDirection.LowerLeft,
				ShieldArc.Right => upper ? SwingDirection.UpperRight : SwingDirection.LowerRight,
				_ => SwingDirection.Overhead
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
