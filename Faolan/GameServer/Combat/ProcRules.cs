using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;

namespace Faolan.GameServer.Combat
{
	// The pure half of the feat PROC engine (#697): which of a character's procs an event reaches, and how likely each
	// is to fire. ProcService is the side-effecting half.
	//
	// The rate is PROCS PER MINUTE - the client's own label for key 237 ("Procs per Minute", the composed
	// AddSpellLineProc sentence, docs/rdb/spell-tooltip.md) - not a percent chance, which is how it was first read.
	// A per-minute rate turns into a per-event chance through the event's own length, which is what makes a 200 the
	// "always" the feat texts describe (200 x 1.5 s / 60 = 5, clamped to 1) and a 10 a real roll (10 x 2 s / 60 = 33%).
	public static class ProcRules
	{
		// An instant cast still occupies at least the global cooldown, so a per-minute rate cannot be applied to a
		// zero-length event; this is the floor. ⚠️ An assumption, not a decode: the client states the rate and the
		// cast time, and the floor is what "an event's length" means for an instant one.
		public const double MinEventSeconds = 1.0;

		public static double Chance(int procsPerMinute, double eventSeconds)
		{
			if (procsPerMinute <= 0)
				return 0;

			return Math.Clamp(procsPerMinute * Math.Max(eventSeconds, MinEventSeconds) / 60.0, 0, 1);
		}

		// The procs a landed COMBO reaches: both combo-form triggers fire on the landing, because the finisher's hit
		// and its damage are one event in our model (the combo's payload is its finisher's spell).
		public static IEnumerable<FeatProc> ForCombo(IEnumerable<FeatProc> procs, uint comboId)
		{
			return procs.Where(p => p.ComboId == comboId && (FeatProcTrigger)p.Trigger is FeatProcTrigger.Hit or FeatProcTrigger.Damage);
		}

		// The procs a CAST of a spell on `lineId` reaches: a Cast trigger on any cast, a SpellDamage trigger only when
		// the cast was offensive (it landed on a hostile target).
		public static IEnumerable<FeatProc> ForSpellLine(IEnumerable<FeatProc> procs, uint lineId, bool offensive)
		{
			return procs.Where(p => p.SpellLineId == lineId
				&& ((FeatProcTrigger)p.Trigger == FeatProcTrigger.Cast || (offensive && (FeatProcTrigger)p.Trigger == FeatProcTrigger.SpellDamage)));
		}
	}
}
