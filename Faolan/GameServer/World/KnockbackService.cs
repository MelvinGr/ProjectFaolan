using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Progression;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.World
{
	// Knockback (#496): the server shoving an entity along a segment it did not walk. Composed collaborator - decides
	// WHERE the victim lands, and reaches the wire through the broadcaster (the forced-move MoveTask + SetPos pair).
	//
	// The wire form is decoded and byte-exact; the GAMEPLAY around it is not, and the split is worth keeping in mind:
	// the whole corpus has exactly ONE knockback, so distance, direction and how KnockbackResistance scales it are a
	// single measurement plus a reasonable shape (see Knockback). Nothing here should be quoted as retail-confirmed
	// except the packets it sends.
	//
	// ⚠️ Nothing TRIGGERS this yet, deliberately. The captured knockback came from an NPC ability, but the ApplySpell
	// in that batch is the "Knockback immunity" guard rather than the knocking ability, so the corpus does not say
	// WHICH abilities knock back - and guessing would put a shove on attacks that never had one. It runs from the GM
	// command until a capture names an ability (the same position #506's conversation trigger is in).
	public sealed class KnockbackService
	{
		// The listener's ApplySpell op, as the single-op delegate seam the composition pattern prescribes (CLAUDE.md) -
		// one shared operation does not warrant its own named port. Takes the WIRE id ((base << 8) | rank).
		private readonly Func<INetworkClient, uint, Task> _applySelfBuff;
		private readonly WorldBroadcaster _broadcaster;
		private readonly ILogger _logger;
		private readonly WorldSimulation _sim;

		public KnockbackService(WorldSimulation sim, WorldBroadcaster broadcaster, Func<INetworkClient, uint, Task> applySelfBuff, ILogger logger)
		{
			_sim = sim;
			_broadcaster = broadcaster;
			_applySelfBuff = applySelfBuff;
			_logger = logger;
		}

		/// <summary>
		///     Shove <paramref name="victim" /> directly away from <paramref name="source" />. Returns false when the
		///     geometry is degenerate (source and victim on the same spot) or the victim resists it entirely.
		/// </summary>
		public async Task<bool> Knock(WorldEntity victim, Vector3 source, float distance = Knockback.DefaultDistance)
		{
			// Read the victim's own resistance rather than assuming none - the stat exists (0x040D) and this is the
			// one place that consults it. It is normally absent, in which case ResistanceScale returns 1.
			var resistance = victim.BuffedStats.GetValueOrDefault(CharacterStatId.KnockbackResistance,
				(uint)victim.GearBonuses.GetValueOrDefault(CharacterStatId.KnockbackResistance));

			var to = Knockback.Resolve(source, victim.Position, distance, resistance);
			if (to == victim.Position)
			{
				_logger.LogInformation("Knockback: {Victim} not displaced (resistance {Resistance}, source {Source} vs position {Pos}).", victim.Name, resistance, source, victim.Position);
				return false;
			}

			// Ground-snap so the shove follows the terrain instead of leaving the victim at their old altitude - the
			// difference between being pushed down a slope and being pushed into the air above it. Degrades to the
			// unsnapped point when no terrain is loaded for the map, which is the documented no-terrain behaviour
			// rather than a silent failure.
			var ground = _sim.Terrain?.GroundHeight(victim.MapId, to.X, to.Z);
			if (ground is { } y)
				to = new Vector3(to.X, y, to.Z);
			else if (_sim.Terrain is { Available: true })
				_logger.LogDebug("Knockback: no ground height at ({X:F1}, {Z:F1}) on map {Map}; leaving the destination unsnapped.", to.X, to.Z, victim.MapId);

			// Retail applies "Knockback immunity" to the victim in the same batch as the displacement. Sent before the
			// move so a client that acts on it has it in hand when the shove lands.
			if (victim.Client != null)
				await _applySelfBuff(victim.Client, Knockback.ImmunitySpellId << 8);

			await _broadcaster.BroadcastKnockback(victim, to);
			return true;
		}
	}
}
