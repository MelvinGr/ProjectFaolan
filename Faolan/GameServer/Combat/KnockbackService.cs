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
	// Knockback (#826): resolve the shove against the victim's resistance, apply retail's immunity buff, and put the
	// displacement on the wire for the victim and everyone watching.
	//
	// It came off `WorldBroadcaster` and took that class's LAST bound seam with it. `Knock` needs to apply a self-buff,
	// which the broadcaster could only reach through `BindApplySelfBuff` - a `Func<>` bound after construction, because
	// what answers it (`SpellEffectOutbound` -> `BuffService`) is built ON the broadcaster. Built here instead, after
	// the port exists, the dependency is an ordinary constructor argument and the cycle is simply gone.
	//
	// ⭐ That is the general move when a bound seam looks unavoidable: the cycle is usually between the CLASS the op
	// sits in and its answer, not between the op and its answer. Move the op to a class built later and it dissolves.
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

		// Relays a player's ApplySpell (the MagicBuffIIR spell EFFECT - a buff applied, or an offensive cast) to every
		// other player who can already see the caster, so the effect renders on their clients too. This is the EFFECT
		// analogue of BroadcastBehaviorToViewers, which relays only the cast GESTURE (the BCC animation): retail streams
		// BOTH to nearby observers. `body` is the same ApplySpell carrier the caster's own client receives - it already
		// carries the acting player's handle as applyFrom (and, for a self-buff, applyTo); retail relays that exact body
		// to nearby players, only the frame receiver differs. Ground truth: the other-players-visible capture
		// (Hoxxling ...other_playersvisible) shows s2c ApplySpell (0x6B87E316) for other-player handles 0x1316xxx framed
		// to our endpoint - both self-buffs (e.g. player 0x01316e68 at frame 3809) and offensive casts (player
		// 0x01316cee on NPC targets). Gated on the viewer's Known set (#245: never relay for a handle the viewer's
		// client hasn't built). No-op solo / when the caster is unseen.
		// Knockback (#496): shove a PLAYER from where they stand to `to`, and make everyone who can see them watch it.
		//
		// KNOCKBACK (#496): the server shoving an entity along a segment it did not walk. Decides WHERE the victim
		// lands, then hands the pair of records below the job of saying so.
		//
		// The wire form is decoded and byte-exact; the GAMEPLAY around it is not, and the split is worth keeping in
		// mind: the whole corpus has exactly ONE knockback, so distance, direction and how KnockbackResistance scales
		// it are a single measurement plus a reasonable shape (see Knockback). Nothing here should be quoted as
		// retail-confirmed except the packets it sends.
		//
		// ⚠️ Until #139's combo finisher wired it, nothing TRIGGERED this - a GM command was its only caller. The
		// captured knockback came from an NPC ability, but the ApplySpell in that batch is the "Knockback immunity"
		// guard rather than the knocking ability, so the corpus still does not say WHICH NPC abilities knock back;
		// guessing would put a shove on attacks that never had one.
		//
		// Returns false when the geometry is degenerate (source and victim on the same spot) or the victim resists it
		// outright.
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
			// move so a client that acts on it has it in hand when the shove lands. Only a PLAYER victim has a client
			// to buff; an NPC is displaced without one.
			if (victim.Client != null)
				await _spellEffects.ApplySelfBuff(victim.Client, Knockback.ImmunitySpellId << 8);

			await BroadcastKnockback(victim, to);
			return true;
		}

		// Two records, in retail's own order (the annotated dungeon capture, 18:49:54.284 -> 18:49:56.020):
		//   1. the forced-move MoveTask (mode 0x05) carrying from -> to, which is what the client ANIMATES;
		//   2. a SetPos pinning the destination ~1.7 s later, which is what makes it authoritative.
		//
		// Both go to the victim AND to nearby viewers - the victim's client plays the shove, and a viewer who only got
		// the SetPos would see the other player teleport. The model position is advanced first so interest, aggro and
		// the next move all agree with where the player now is; skipping that is how a displaced player ends up
		// invisible to the NPCs still chasing their old position.
		//
		// ⚠️ The caller is expected to have ground-snapped `to`. This does NOT clamp it: a knockback is allowed to put
		// a player somewhere they could not have walked (off a ledge - which then costs them fall damage, #495), and
		// clamping here would quietly cancel that.
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
