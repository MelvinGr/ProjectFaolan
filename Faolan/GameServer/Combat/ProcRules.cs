using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     The pure half of the feat proc engine: which of a character's procs an event reaches, and how likely each is
	///     to fire. <see cref="ProcService" /> is the side-effecting half.
	/// </summary>
	/// <remarks>
	///     The rate is procs per minute - the client's own label for key 237 ("Procs per Minute", the composed
	///     AddSpellLineProc sentence, docs/rdb/spell-tooltip.md) - not a percent chance. A per-minute rate turns into a
	///     per-event chance through the event's own length, which is what makes a 200 the "always" the feat texts
	///     describe (200 x 1.5 s / 60 = 5, clamped to 1) and a 10 a real roll (10 x 2 s / 60 = 33%).
	/// </remarks>
	public static class ProcRules
	{
		// An instant cast still occupies at least the global cooldown, so a per-minute rate cannot be applied to a
		// zero-length event; this is the floor. An assumption rather than a decode: the client states the rate and the
		// cast time, and the floor is what "an event's length" means for an instant one.
		public const double MinEventSeconds = 1.0;

		public static double Chance(int procsPerMinute, double eventSeconds)
		{
			if (procsPerMinute <= 0)
				return 0;

			return Math.Clamp(procsPerMinute * Math.Max(eventSeconds, MinEventSeconds) / 60.0, 0, 1);
		}

		/// <summary>
		///     The procs a landed combo reaches: both combo-form triggers fire on the landing, because the finisher's
		///     hit and its damage are one event in this model - a combo's payload is its finisher's spell.
		/// </summary>
		public static IEnumerable<FeatProc> ForCombo(IEnumerable<FeatProc> procs, uint comboId)
		{
			return procs.Where(p => p.ComboId == comboId && (FeatProcTrigger)p.Trigger is FeatProcTrigger.Hit or FeatProcTrigger.Damage);
		}

		/// <summary>
		///     The procs a cast of a spell on <paramref name="lineId" /> reaches: a Cast trigger on any cast, a
		///     SpellDamage trigger only when the cast was offensive - that is, it landed on a hostile target.
		/// </summary>
		public static IEnumerable<FeatProc> ForSpellLine(IEnumerable<FeatProc> procs, uint lineId, bool offensive)
		{
			return procs.Where(p => p.SpellLineId == lineId
				&& ((FeatProcTrigger)p.Trigger == FeatProcTrigger.Cast || (offensive && (FeatProcTrigger)p.Trigger == FeatProcTrigger.SpellDamage)));
		}
	}
}
