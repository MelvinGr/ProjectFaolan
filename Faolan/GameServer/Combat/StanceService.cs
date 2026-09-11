using System;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Constants;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     The player stances - search, sneak/hide and the rest cast - which is what retail answers a
	///     PerformSpecialAction press with.
	/// </summary>
	public sealed class StanceService(WorldSimulation simulation, WorldBroadcaster broadcaster, CastService cast, ILogger logger)
	{
		private readonly ILogger _logger = logger;

		// How long the `Resting` channel runs. Not a number chosen here: it is the Spells row for 3164147 `Resting`
		// ("Recovering health, mana, energy, and stamina"), whose 20,500 ms cast time is exactly the 2050 centiseconds
		// retail puts in the rest behaviour record.
		private const int RestingChannelMs = 20_500;

		private readonly WorldSimulation _simulation = simulation;
		private readonly WorldBroadcaster _broadcaster = broadcaster;
		private readonly CastService _cast = cast;

		/// <summary>
		///     The search stance: retail's answer to PerformSpecialAction 0x064 is one player-addressed
		///     BehaviorControlCenterIIR at slot <c>BehaviorId.Search</c> and nothing else - no stat change, no buff.
		/// </summary>
		/// <remarks>
		///     The client waits for that answer before the stance takes, which is why a press with no answer reads as
		///     the client having sent nothing. The animation action is the character's rather than the stance's, so it
		///     comes from a per-class map holding only the two classes a capture shows; an unmapped class logs and does
		///     nothing, as the spellweave toggle does, because a borrowed id would play another class's animation and
		///     read as working.
		/// </remarks>
		public async Task PerformSearchStance(INetworkClient client)
		{
			var character = client.Session.Character;
			if (!StartingData.SearchStanceAction.TryGetValue(character.Class, out var animAction))
			{
				_logger.LogInformation("Search stance: no captured animation action for class {Class} - not answering (#505).", character.Class);
				return;
			}

			var entity = _simulation.PlayerWorldEntity(client);
			// A stance is something other players watch you do, so the same record is relayed to everyone who can see
			// the actor. `BehaviorControlCenterIIR` is 11 of 11 broadcast in the two-client capture
			// (`--report broadcast-check`), and a search crouch nobody else sees is a player frozen upright to them.
			var searchStance = BehaviorControlCenterCodec.Build(BehaviorAnimations.SearchStance(character.Id, animAction, entity?.NextBehaviorPhase() ?? 3));
			await WorldIir.Send(client, searchStance);
			await _broadcaster.BroadcastBehaviorToViewers(client, searchStance);
		}

		/// <summary>
		///     The hide stance - a toggle on one selector, so the server holds which way the next press goes.
		/// </summary>
		/// <remarks>
		///     Entering carries the shadow coverage the client itself reported a moment earlier (SetShadowCoverageIIR);
		///     leaving is the shared stand-up action, the one part of the pair that is not class-specific. The exit
		///     record's shape is taken from the two necromancer sightings (flags 8, no echoed command id); the one
		///     Herald of Xotli exit in the corpus carries flags 10 and a command id instead, on an otherwise identical
		///     8-byte-tailed record. Three sightings disagreeing two-to-one is not enough to model the difference, so
		///     the majority form is sent for both.
		/// </remarks>
		public async Task ToggleSneakHide(INetworkClient client)
		{
			var character = client.Session.Character;
			if (!StartingData.SneakHideEnterAction.TryGetValue(character.Class, out var enterAction))
			{
				_logger.LogInformation("Hide stance: no captured animation action for class {Class} - not answering (#505).", character.Class);
				return;
			}

			var entity = _simulation.PlayerWorldEntity(client);
			var hidden = entity?.IsHidden ?? false;
			var phase = entity?.NextBehaviorPhase() ?? 3;

			var record = hidden
				? BehaviorAnimations.SneakHideExit(character.Id, StartingData.SneakHideExitAction, phase)
				: BehaviorAnimations.SneakHideEnter(character.Id, enterAction, phase, entity?.NextBehaviorCommandId() ?? 3, entity?.ShadowCoverage ?? 100);

			// Relayed like the search stance: entering or leaving hiding is visible on the actor, and a rogue who slips
			// into the shadows only on their own screen is still standing there for everyone else.
			var stance = BehaviorControlCenterCodec.Build(record);
			await WorldIir.Send(client, stance);
			await _broadcaster.BroadcastBehaviorToViewers(client, stance);
			if (entity != null)
				entity.IsHidden = !hidden;

			_logger.LogInformation("Hide stance: '{Name}' {Action}.", character.Name, hidden
				? "left hiding"
				: "entered hiding");
		}

		/// <summary>
		///     Rest. Unlike search and hide this is not a stance at all: retail answers with a self-cast of the spell
		///     the client calls <c>Resting</c> - "Recovering health, mana, energy, and stamina" - on slot 4
		///     MagicAttack, with the spell row's own 20,500 ms cast time in the record.
		/// </summary>
		/// <remarks>
		///     The record is class-independent - the cast animation 177216 is shared with the Path of Asura recall -
		///     and byte-identical across four captured characters, so there is no per-class map here. Only the cast is
		///     emitted: the regen rate while resting is not modelled, because no capture measures it and the tick's
		///     rates are corpus-verified numbers (<see cref="RegenRates" />) that a guessed rest multiplier would
		///     quietly break. The resting window is tracked so the tick can be taught the real rate once one is
		///     measured, and it is cleared when the channel elapses.
		/// </remarks>
		public async Task PerformRestCast(INetworkClient client)
		{
			var character = client.Session.Character;
			var entity = _simulation.PlayerWorldEntity(client);

			var record = BehaviorAnimations.RestCast(character.Id, entity?.NextBehaviorPhase() ?? 1);
			// A 20-second rest cast is an animation on the actor, so it is relayed like every other player behaviour.
			var restCast = BehaviorControlCenterCodec.Build(record);
			await WorldIir.Send(client, restCast);
			await _broadcaster.BroadcastBehaviorToViewers(client, restCast);

			// Rest is a cast, so it has to arm the cast path's cancel state or it can never be cancelled. The client
			// ends this channel the way it ends any other: it sends the c2s cancel BCC (seq 0x0002) when the player
			// moves, and the cast service answers with retail's 0x0007 abort - but only if it finds the begin's args
			// here. Rest is answered to a `PerformSpecialAction` rather than to a c2s cast, so nothing else sets them,
			// and without them the cancel falls through to a plain echo: the client stays channeling, the bar hangs,
			// and a channeling client sends no further cast or melee at all.
			//
			// Verified against `NecroL80_..._cast-general-abilities` frames 867/921/927 - begin seq 0x0004 (phase 8,
			// activation 2050) -> c2s cancel seq 0x0002 -> s2c abort seq 0x0007, whose args are these begin args with
			// the cancel's own command id spliced in.
			client.Session.PendingCastArgs = record.Args;
			client.Session.PendingCastMode = 0; // no client command id at begin - the cancel brings its own

			if (entity == null)
				return;

			entity.RestingUntil = DateTimeOffset.UtcNow.AddMilliseconds(RestingChannelMs);
			_logger.LogInformation("Rest: '{Name}' began resting for {Ms} ms.", character.Name, RestingChannelMs);
		}
	}
}
