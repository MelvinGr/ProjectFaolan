using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.GameServer.Combat;
using Xunit;

namespace Faolan.Tests
{
	// The feat proc engine's pure half: which procs an event reaches, and the per-event chance a per-minute
	// rate becomes. The trigger values come from the client's own proc tooltip switch (FeatProcTrigger); the rate's
	// unit from its "Procs per Minute" label.
	public class FeatProcRulesTests
	{
		private static FeatProc Combo(uint comboId, FeatProcTrigger trigger, int amount = 120)
		{
			return new FeatProc { FeatId = 1, ComboId = comboId, Trigger = (uint)trigger, Amount = amount, EffectKind = 53212, SelfSpellId = 10 };
		}

		private static FeatProc Line(uint lineId, FeatProcTrigger trigger, int amount = 120)
		{
			return new FeatProc { FeatId = 2, SpellLineId = lineId, Trigger = (uint)trigger, Amount = amount, EffectKind = 53070, SelfSpellId = 20 };
		}

		// The client's switch: 0 Hit, 1 Damage, 2 SpellDamage; 3 is its default case, named by the heal-line feats
		// that carry it. Pinned so a renumbering cannot pass silently.
		[Fact]
		public void TheTriggerValues_AreTheClientsSwitchCases()
		{
			Assert.Equal(0u, (uint)FeatProcTrigger.Hit);
			Assert.Equal(1u, (uint)FeatProcTrigger.Damage);
			Assert.Equal(2u, (uint)FeatProcTrigger.SpellDamage);
			Assert.Equal(3u, (uint)FeatProcTrigger.Cast);
		}

		// A per-minute rate over the event's length: 10/min on a 2 s combo is one in three; 200/min is "always" on
		// anything, which is what the feat texts describe for the 120/200 rows; an instant cast is floored at the
		// global cooldown rather than never firing.
		[Theory]
		[InlineData(10, 2.0, 1.0 / 3)]
		[InlineData(200, 0.5, 1.0)]
		[InlineData(120, 1.5, 1.0)]
		[InlineData(10, 0.0, 1.0 / 6)]
		[InlineData(0, 2.0, 0.0)]
		public void Chance_IsTheRateOverTheEventsLength_Clamped(int perMinute, double seconds, double expected)
		{
			Assert.Equal(expected, ProcRules.Chance(perMinute, seconds), 6);
		}

		// A combo landing reaches both combo-form triggers of that combo and nothing else - not another combo's, and
		// never a spell-line row, whatever its trigger.
		[Fact]
		public void ForCombo_ReachesBothComboTriggersOfThatComboOnly()
		{
			var procs = new[] { Combo(5, FeatProcTrigger.Hit), Combo(5, FeatProcTrigger.Damage), Combo(6, FeatProcTrigger.Damage), Line(5, FeatProcTrigger.Cast) };

			var reached = ProcRules.ForCombo(procs, 5).ToList();

			Assert.Equal(2, reached.Count);
			Assert.All(reached, p => Assert.Equal(5u, p.ComboId));
		}

		// A cast reaches a Cast trigger whether or not it was offensive, and a SpellDamage trigger only when it was;
		// a combo row on the same id is a different key space and is never reached by a cast.
		[Fact]
		public void ForSpellLine_CastFiresOnAnyCast_SpellDamageOnlyOnAnOffensiveOne()
		{
			var procs = new[] { Line(24020, FeatProcTrigger.Cast), Line(24020, FeatProcTrigger.SpellDamage), Line(9, FeatProcTrigger.Cast), Combo(24020, FeatProcTrigger.Hit) };

			var friendly = ProcRules.ForSpellLine(procs, 24020, offensive: false).ToList();
			var hostile = ProcRules.ForSpellLine(procs, 24020, offensive: true).ToList();

			Assert.Equal([FeatProcTrigger.Cast], friendly.Select(p => (FeatProcTrigger)p.Trigger));
			Assert.Equal([FeatProcTrigger.Cast, FeatProcTrigger.SpellDamage], hostile.Select(p => (FeatProcTrigger)p.Trigger));
		}
	}
}
