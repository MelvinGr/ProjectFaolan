using System;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     The fatality burst a killing blow produces.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The corpus carries the server side of a fatality on a caster's kill in seven captures, and that is what
	///         this reproduces:
	///     </para>
	///     <code>
	///     s2c BccIIR seq=0x000A action=4          the killing cast completing
	///     s2c ApplySpell 5070797 "Fatality"       self, 0.1s
	///     s2c ApplySpell  791221 "Fatality"       self, 10s
	///     s2c StatDamage
	///     s2c ApplySpell 3666099 "Frozen Hatred"  self, 6s - only when that buff is up
	///     </code>
	///     <para>
	///         The mage-necromancer beach-to-city capture, frame 1267, is the worked example: 36 bursts carry both
	///         Fatality ids and 21 carry the Frozen Hatred proc. Both Fatality ids are applied together and always to
	///         the killer, never to the corpse.
	///     </para>
	///     <para>
	///         The two Fatality ids are what a fatality is, so they are constants here. What rides a fatality is not:
	///         a proc such as Frozen Hatred is a <c>SpellEffect</c> row with an OnFatality trigger, so the next buff
	///         that procs on a fatality is an imported row rather than an edit to this file.
	///     </para>
	///     <para>
	///         The roll is the client's own: RDB 1010219 gives <c>fatalitychance</c> as a percent, tiered by
	///         <c>minlevel</c> - see <see cref="FatalityChancePercent" />. The fatality animation is deliberately not
	///         here: the finisher anims live in the client's combo and 1010219 tables keyed by weapon and level, and
	///         the server does not drive them. This is the state and buff half, which is what the wire shows and what
	///         gameplay reads.
	///     </para>
	/// </remarks>
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

		/// <summary>
		///     How often a killing blow is a fatality.
		/// </summary>
		/// <remarks>
		///     <para>
		///         Retail gates it on the client's fatality groups (RDB 1010219): each <c>fatalitygroup</c> lists
		///         <c>fatality</c> entries whose <c>fatalitychance</c> is a straight percent. Across the 58 groups and
		///         205 real entries (3 dev rows at 9980% excluded) <c>minlevel</c> maps onto chance exactly:
		///     </para>
		///     <code>
		///     minlevel  1 -> 5%     minlevel 30 -> 15%     minlevel 80 -> 30%
		///     minlevel  5 -> 10%    minlevel 40/60 -> 20%
		///     </code>
		///     <para>
		///         So the best a character qualifies for rises with level, which is what this returns. The group is
		///         chosen by <c>criteria</c> (weapon or stance: 23 = 1H-Sword family, 24 = 1H-Axe, 27 = stealth 1hb,
		///         30 = stealth dual-dagger, ...), which only picks which animation plays - client-side either way -
		///         and the captures show casters getting fatalities with no weapon group at all. Level alone drives the
		///         roll.
		///     </para>
		/// </remarks>
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

		/// <summary>
		///     A killing blow landed: rolls the fatality chance for the killer's level and, when it hits, emits the
		///     burst on the killer (both Fatality buffs) plus whatever the killer's own live buffs arm on a fatality.
		/// </summary>
		/// <param name="killerEntity">
		///     Carries those buffs - the proc reads the killer's state, never the corpse's.
		/// </param>
		public async Task OnKill(INetworkClient killer, WorldEntity? killerEntity, WorldEntity victim)
		{
			var level = killer.Session.Character.Level;
			var chance = FatalityChancePercent(level);
			if (Random.Shared.Next(100) >= chance)
				return;

			// `walkChain: false`, because a Fatality row's 53051 elements are a candidate registry rather than "when
			// this lands, also run X". 791221's five edges are `Frozen Hatred`, `Companion Spirit`, `Furious
			// Inspiration`, `XX Daily Fatality Counter` and a second `Fatality` row: three different classes' fatality
			// procs, a dev counter and a sibling carrier. They are what can hang off a fatality, and the killer's own
			// state decides which one does - which is what `ForFatality` below resolves.
			//
			// The capture settles it: over its 36 fatality bursts all 36 carry the two Fatality ids and only 21 carry
			// the Frozen Hatred proc. Applied wholesale, the other four edges would be in all 36; they are in none.
			await _outbound.ApplySelfBuff(killer, FatalityBuffA << 8, walkChain: false);
			await _outbound.ApplySelfBuff(killer, FatalityBuffB << 8, walkChain: false);

			// Whatever the killer is carrying that procs on a fatality rides the same burst in the capture (frame 1267:
			// both Fatality ids then Frozen Hatred's 3666099, in one frame).
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
