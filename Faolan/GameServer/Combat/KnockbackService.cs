using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Progression;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     Knockback: resolve the shove against the victim's resistance, apply retail's immunity buff, and put the
	///     displacement on the wire for the victim and everyone watching.
	/// </summary>
	/// <remarks>
	///     This is built after <see cref="ISpellEffectOutbound" /> exists, which is what lets the self-buff it applies
	///     be an ordinary constructor argument. Moving the shove back onto <see cref="WorldBroadcaster" /> - which is
	///     built below that port - would reintroduce a construction cycle that only a bound seam could break.
	/// </remarks>
	public sealed class KnockbackService
	{
		private readonly ILogger _logger;
		private readonly WorldSimulation _sim;
		private readonly ISpellEffectOutbound _spellEffects;

		public KnockbackService(WorldSimulation sim, ISpellEffectOutbound spellEffects, ILogger logger)
		{
			_sim = sim;
			_spellEffects = spellEffects;
			_logger = logger;
		}

		/// <summary>
		///     The server shoving an entity along a segment it did not walk: decides where the victim lands, then
		///     hands the pair of records below the job of saying so.
		/// </summary>
		/// <remarks>
		///     <para>
		///         The wire form is decoded and byte-exact; the gameplay around it is not. The whole corpus holds
		///         exactly one knockback, so distance, direction and how KnockbackResistance scales it are a single
		///         measurement plus a plausible shape (see <see cref="Knockback" />). Nothing here is
		///         retail-confirmed except the packets it sends.
		///     </para>
		///     <para>
		///         The corpus also does not say which NPC abilities knock back: the captured knockback came from an
		///         NPC ability, but the ApplySpell in that batch is the "Knockback immunity" guard rather than the
		///         knocking ability, so attaching a shove to an NPC attack would be a guess.
		///     </para>
		/// </remarks>
		/// <returns>
		///     False when the geometry is degenerate (source and victim on the same spot) or the victim resists the
		///     shove outright.
		/// </returns>
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
				to = to with { Y = y };
			else if (_sim.Terrain is { Available: true })
				_logger.LogDebug("Knockback: no ground height at ({X:F1}, {Z:F1}) on map {Map}; leaving the destination unsnapped.", to.X, to.Z, victim.MapId);

			// Retail applies "Knockback immunity" to the victim in the same batch as the displacement. Sent before the
			// move so a client that acts on it has it in hand when the shove lands. Only a player victim has a client
			// to buff; an NPC is displaced without one.
			if (victim.Client != null)
				await _spellEffects.ApplySelfBuff(victim.Client, Knockback.ImmunitySpellId << 8);

			await BroadcastKnockback(victim, to);
			return true;
		}

		/// <summary>
		///     Two records, in retail's own order (the annotated dungeon capture, 18:49:54.284 -&gt; 18:49:56.020):
		///     the forced-move MoveTask (mode 0x05) carrying from -&gt; to, which is what the client animates, then a
		///     SetPos pinning the destination about 1.7 s later, which is what makes it authoritative.
		/// </summary>
		/// <remarks>
		///     <para>
		///         Both go to the victim and to nearby viewers: the victim's client plays the shove, and a viewer who
		///         only got the SetPos would see the other player teleport. The model position is advanced first so
		///         interest, aggro and the next move agree with where the player now is; otherwise a displaced player
		///         is invisible to the NPCs still chasing their old position.
		///     </para>
		///     <para>
		///         The caller is expected to have ground-snapped <paramref name="to" />. This does not clamp it: a
		///         knockback is allowed to put a player somewhere they could not have walked - off a ledge, which then
		///         costs them fall damage - and clamping here would quietly cancel that.
		///     </para>
		/// </remarks>
		private async Task BroadcastKnockback(WorldEntity victim, Vector3 to)
		{
			var from = victim.Position;
			var move = MoveTaskCodec.Build(new MoveTaskIIR(victim.Handle, from, MoveTaskNodes.ForcedMove(to, from)));
			var pin = SetPosCodec.Build(victim.Handle, to, default, 0x01);

			victim.Position = to;

			if (victim.Client != null)
			{
				await WorldIir.Send(victim.Client, move);
				await WorldIir.Send(victim.Client, pin);
			}

			var zone = _sim.World.ZoneForEntity(victim);
			if (zone == null)
				return;

			foreach (var viewer in zone.Entities)
			{
				if (!viewer.IsPlayer || viewer.Handle == victim.Handle || viewer.Client == null)
					continue;
				if (!viewer.Knowledge.Known.Contains(victim.Handle))
					continue; // only players whose client already holds the victim's handle

				await WorldIir.Send(viewer.Client, move);
				await WorldIir.Send(viewer.Client, pin);
			}

			_logger.LogInformation("Knockback: {Victim} shoved {Distance:F2} units from {From} to {To}.", victim.Name, Vector3.Distance(from, to), from, to);
		}
	}
}
