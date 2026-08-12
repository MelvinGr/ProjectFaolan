using System;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Combat
{
	// The FATALITY burst a killing blow produces (#140).
	//
	// Long tracked as un-pinnable: the RE notes concluded the trigger wire was unknown and that no capture showed a
	// fatality. That was true only of MELEE finishers - the animated decapitations from the combo tables. What the
	// corpus does contain, in SEVEN captures, is the server side of a fatality on a CASTER's kill, and that is what
	// this reproduces:
	//
	//   s2c BccIIR seq=0x000A action=4       the killing cast completing
	//   s2c ApplySpell 5070797 "Fatality"    self, 0.1s
	//   s2c ApplySpell  791221 "Fatality"    self, 10s
	//   s2c StatDamage
	//   s2c ApplySpell 3666099 "Frozen Hatred"  self, 6s - ONLY when that buff is up
	//
	// (xxx_mage-necromancer beach->city fr1267 is the worked example; 36 bursts carry both Fatality ids and 21 carry
	// the Frozen Hatred proc.) Both Fatality ids are applied together and always to the KILLER, never to the corpse.
	//
	// The two Fatality ids are what a fatality IS, so they are constants here. What RIDES a fatality is not: the
	// Frozen Hatred proc + its pet were four more constants and an `if` on this path, and are now SpellEffect rows
	// with an OnFatality trigger (#439) - so the next buff that procs on a fatality is an imported row rather than an
	// edit to this file.
	//
	// The ROLL is the client's own: RDB 1010219 gives fatalitychance as a percent, tiered by minlevel (see
	// FatalityChancePercent). What is deliberately NOT here: the fatality ANIMATION. The finisher anims live in the
	// client's own combo / 1010219 tables keyed by weapon and level, the server does not drive them, and inventing an
	// emission on the high-traffic kill path is exactly what the earlier RE notes warned against. This is the state +
	// buff half, which is what the wire shows and what gameplay (Frozen Hatred's pet) actually reads.
	public sealed class FatalityService
	{
		// The two self-buffs every captured fatality applies, in wire order.
		public const uint FatalityBuffA = 5070797; // 0.1s - the instantaneous "a fatality happened" marker
		public const uint FatalityBuffB = 791221; // 10s

		private readonly ILogger _logger;
		private readonly ISpellEffectOutbound _outbound;
		private readonly SpellEffectService _spellEffects;

		public FatalityService(ILogger logger, ISpellEffectOutbound outbound, SpellEffectService spellEffects)
		{
			_logger = logger;
			_outbound = outbound;
			_spellEffects = spellEffects;
		}

		// How often a killing blow IS a fatality. Retail gates it on the client's fatality groups (RDB 1010219): each
		// <fatalitygroup criteria="..."> lists <fatality fatalitychance="N" minlevel="M" .../> entries, and the chance
		// is a straight PERCENT. Across the 58 groups / 205 real entries (3 dev rows at 9980% excluded) minlevel maps
		// onto chance exactly:
		//
		//   minlevel  1 -> 5%     minlevel 30 -> 15%     minlevel 80 -> 30%
		//   minlevel  5 -> 10%    minlevel 40/60 -> 20%
		//
		// so the best a character qualifies for rises with level, which is what this returns. The GROUP is chosen by
		// `criteria` (weapon/stance: 23 = 1H-Sword family, 24 = 1H-Axe, 27 = stealth 1hb, 30 = stealth dual-dagger, ...)
		// and that mapping is not lifted yet - it only picks WHICH animation plays, which is client-side anyway, and
		// the captures show casters getting fatalities with no weapon group at all. So level alone drives the roll.
		public static int FatalityChancePercent(uint level)
		{
			return level switch
			{
				>= 80 => 30,
				>= 40 => 20,
				>= 30 => 15,
				>= 5 => 10,
				_ => 5
			};
		}

		// A killing blow landed. Rolls the fatality chance for the killer's level and, when it hits, emits the burst on
		// the KILLER (both Fatality buffs) plus whatever the killer's own live buffs arm on a fatality. `killerEntity`
		// is what carries those buffs - the proc reads the killer's state, never the corpse's.
		public async Task OnKill(INetworkClient killer, WorldEntity? killerEntity, WorldEntity victim)
		{
			var level = killer.Session.Character.Level;
			var chance = FatalityChancePercent(level);
			if (Random.Shared.Next(100) >= chance)
				return;

			// ⛔ `walkChain: false`, because a Fatality row's 53051 elements are a candidate REGISTRY - not #784's
			// "when this lands, also run X". `791221`'s five edges are `Frozen Hatred`, `Companion Spirit`,
			// `Furious Inspiration`, `XX Daily Fatality Counter` and a second `Fatality` row: three different classes'
			// fatality procs, a dev counter, and a sibling carrier. They are the things that CAN hang off a fatality,
			// and the killer's own state decides which one does - which is what `ForFatality` below resolves.
			//
			// The capture settles it: over its 36 fatality bursts, ALL 36 carry the two Fatality ids and only 21 carry
			// the Frozen Hatred proc. If the edges were applied wholesale, the other four would be in all 36; they are
			// in none. Walking them handed a level-80 NECROMANCER `Companion Spirit`, an ungated `Frozen Hatred` she
			// had not armed, and - through the chained rank ladder below `Furious Inspiration`'s nameless line header
			// - all TEN ranks of a class-44 buff at once, eleven icons expiring together 15 s later.
			await _outbound.ApplySelfBuff(killer, FatalityBuffA << 8, walkChain: false);
			await _outbound.ApplySelfBuff(killer, FatalityBuffB << 8, walkChain: false);

			// Whatever the killer is carrying that procs on a fatality rides the SAME burst in the capture (fr1267:
			// both Fatality ids then Frozen Hatred's 3666099, one frame).
			var procs = killerEntity != null
				? await _spellEffects.ForFatality(killerEntity)
				: [];
			await _spellEffects.RunAll(killer, procs);

			_logger.LogInformation("Fatality on {Victim} by '{Killer}' (level {Level}, {Chance}% chance){Procs}.", victim.Name, killer.Session.Character?.Name, level, chance, procs.Count > 0
				? $", {procs.Count} triggered effect(s)"
				: "");
		}
	}
}
