using System;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Combat
{
	// The FATALITY burst a killing blow produces (#140), and the one buff known to hang off it.
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
	// The ROLL is the client's own: RDB 1010219 gives fatalitychance as a percent, tiered by minlevel (see
	// FatalityChancePercent). What is deliberately NOT here: the fatality ANIMATION. The finisher anims live in the client's own combo /
	// 1010219 tables keyed by weapon and level, the server does not drive them, and inventing an emission on the
	// high-traffic kill path is exactly what the earlier RE notes warned against. This is the state + buff half,
	// which is what the wire shows and what gameplay (Frozen Hatred's pet) actually reads.
	public sealed class FatalityService
	{
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

		// The two self-buffs every captured fatality applies, in wire order.
		public const uint FatalityBuffA = 5070797; // 0.1s - the instantaneous "a fatality happened" marker
		public const uint FatalityBuffB = 791221; // 10s

		// The necromancer's Frozen Hatred proc: the 6s buff retail applies on a fatality while the character carries
		// the Frozen Hatred self-buff (4377209, the effect record its castable 4377207 points at - #434).
		public const uint FrozenHatredProc = 3666099;
		public const uint FrozenHatredBuff = 4377209;

		// "the Frozen Hatred pet appears and is under your control for 60 seconds. If you already have a Frozen Hatred
		// pet, the duration of the pet will increase by another 60 seconds. This timer is capped at 5 minutes."
		// (in-client tooltip). The pet is NPC template "Frozen Hatred" (14580).
		public const string FrozenHatredPetTemplate = "Frozen Hatred";
		public static readonly TimeSpan FrozenHatredPetDuration = TimeSpan.FromSeconds(60);
		public static readonly TimeSpan FrozenHatredPetCap = TimeSpan.FromMinutes(5);

		private readonly Func<INetworkClient, uint, Task> _applySelfBuff; // ApplySpell(client, wireSpellId) on the killer
		private readonly Func<INetworkClient, uint, bool> _hasBuff; // is this buff live on the killer's entity
		private readonly ILogger _logger;
		private readonly Func<INetworkClient, string, TimeSpan, Task> _summonTimedPet; // pet template + how long it lives

		public FatalityService(ILogger logger, Func<INetworkClient, uint, Task> applySelfBuff, Func<INetworkClient, uint, bool> hasBuff,
			Func<INetworkClient, string, TimeSpan, Task> summonTimedPet)
		{
			_logger = logger;
			_applySelfBuff = applySelfBuff;
			_hasBuff = hasBuff;
			_summonTimedPet = summonTimedPet;
		}

		// A killing blow landed. Rolls the fatality chance for the killer's level and, when it hits, emits the burst on
		// the KILLER (both Fatality buffs) plus anything that keys off it.
		public async Task OnKill(INetworkClient killer, string victimName)
		{
			var level = killer.Session.Character?.Level ?? 1;
			var chance = FatalityChancePercent(level);
			if (Random.Shared.Next(100) >= chance)
				return;

			await _applySelfBuff(killer, FatalityBuffA << 8);
			await _applySelfBuff(killer, FatalityBuffB << 8);

			if (!_hasBuff(killer, FrozenHatredBuff << 8))
			{
				_logger.LogInformation("Fatality on {Victim} by '{Killer}' (level {Level}, {Chance}% chance).", victimName, killer.Session.Character?.Name, level, chance);
				return;
			}

			// The proc rides the same burst in the capture (fr1267: both Fatality ids then 3666099, one frame).
			await _applySelfBuff(killer, FrozenHatredProc << 8);
			await _summonTimedPet(killer, FrozenHatredPetTemplate, FrozenHatredPetDuration);
			_logger.LogInformation("Fatality on {Victim} by '{Killer}' with Frozen Hatred up: proc applied, pet summoned for {Seconds}s.", victimName, killer.Session.Character?.Name,
				FrozenHatredPetDuration.TotalSeconds);
		}
	}
}
