using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Constants;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Apprentice;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Guild;
using Faolan.Core.Network.Codecs.Inventory;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Network.Server.GameServer;
using Faolan.Core.Progression;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Apprentice;
using Faolan.GameServer.Combat;
using Faolan.GameServer.Economy;
using Faolan.GameServer.Inventory;
using Faolan.GameServer.Progression;
using Faolan.GameServer.Quest;
using Faolan.GameServer.World;
using Faolan.GameServer.Wire;
using Microsoft.Extensions.Logging;
using ProtoBuf;
using Faolan.GameServer;

namespace Faolan.GameServer.Combat
{
	// The player STANCES (#505): search, sneak/hide and the rest cast - what retail answers a PerformSpecialAction
	// press with. Off the listener's Outbound partial, where they were domain code among wire builders (#928 item 6).
	public sealed class StanceService(WorldSimulation simulation, WorldBroadcaster broadcaster, CastService cast, ILogger logger)
	{
		private readonly ILogger _logger = logger;

		// How long the `Resting` channel runs. NOT a number chosen here: it is the Spells row for 3164147 `Resting`
		// ("Recovering health, mana, energy, and stamina"), whose 20,500 ms cast time is exactly the 2050
		// centiseconds retail puts in the rest behaviour record.
		private const int RestingChannelMs = 20_500;

		private readonly WorldSimulation _simulation = simulation;
		private readonly WorldBroadcaster _broadcaster = broadcaster;
		private readonly CastService _cast = cast;

		// The SEARCH stance (#505): retail's answer to PerformSpecialAction 0x064 is one player-addressed
		// BehaviorControlCenterIIR at slot BehaviorId.Search, and nothing else - no stat change, no buff. The client
		// waits for it before the stance takes, which is why a press with no answer reads as "the client sent
		// nothing".
		//
		// The animation action is the CHARACTER's, not the stance's, so it comes from a per-class map holding only the
		// two classes a capture shows. An unmapped class logs and does nothing, the same call the spellweave toggle
		// makes: a borrowed id would play some other class's animation and read as working.
		public async Task PerformSearchStance(INetworkClient client)
		{
			var character = client.Session.Character;
			if (!StartingData.SearchStanceAction.TryGetValue(character.Class, out var animAction))
			{
				_logger.LogInformation("Search stance: no captured animation action for class {Class} - not answering (#505).", character.Class);
				return;
			}

			var entity = _simulation.PlayerWorldEntity(client);
			// #272: a stance is something the OTHER players watch you do, so the same record is relayed to everyone who
			// can see the actor. `BehaviorControlCenterIIR` is 11/11 broadcast in the two-client capture
			// (`--report broadcast-check`), and a search crouch nobody else sees is a player frozen upright to them.
			var searchStance = BehaviorControlCenterCodec.Build(BehaviorAnimations.SearchStance(character.Id, animAction, entity?.NextBehaviorPhase() ?? 3));
			await WorldIir.Send(client, searchStance);
			await _broadcaster.BroadcastBehaviorToViewers(client, searchStance);
		}

		// The HIDE stance (#505) - a TOGGLE on one selector, so the server holds which way the next press goes.
		// Entering carries the shadow coverage the client itself reported a moment earlier (SetShadowCoverageIIR);
		// leaving is the shared stand-up action, the one part of the pair that is NOT class-specific.
		//
		// ⚠️ The EXIT record's shape is taken from the two necromancer sightings (flags 8, no echoed command id). The
		// one Herald of Xotli exit in the corpus carries flags 10 and a command id instead, on an otherwise identical
		// 8-byte-tailed record. Three sightings that disagree two-to-one is not enough to model the difference, so the
		// majority form is sent for both and the divergence is written down rather than silently averaged away.
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

			// #272: relayed like the search stance - entering or leaving hiding is visible on the actor, and a rogue
			// who slips into the shadows only on their own screen is still standing there for everyone else.
			var stance = BehaviorControlCenterCodec.Build(record);
			await WorldIir.Send(client, stance);
			await _broadcaster.BroadcastBehaviorToViewers(client, stance);
			if (entity != null)
				entity.IsHidden = !hidden;

			_logger.LogInformation("Hide stance: '{Name}' {Action}.", character.Name, hidden
				? "left hiding"
				: "entered hiding");
		}

		// REST (#505). Unlike search and hide this is not a stance at all: retail answers with a self-CAST of the
		// spell the client calls `Resting` - "Recovering health, mana, energy, and stamina" - on slot 4 MagicAttack,
		// with the spell row's own 20,500 ms cast time in the record. The player regains vitals over that channel,
		// which is what a 20-second cast bar with no spell at the end of it IS.
		//
		// The record is class-independent (the cast animation 177216 is shared with the Path of Asura recall) and is
		// byte-identical across four captured characters, so there is no per-class map here.
		//
		// ⚠️ What is emitted is the CAST. The regen RATE while resting is not modelled: no capture measures it, and
		// the tick's rates are corpus-verified numbers (RegenRates, #442) that a guessed rest multiplier would
		// quietly break. `IsResting` is set so the tick can be taught the real rate once one is measured, and it is
		// cleared when the channel elapses - the channel length is the spell row's, not invented.
		public async Task PerformRestCast(INetworkClient client)
		{
			var character = client.Session.Character;
			var entity = _simulation.PlayerWorldEntity(client);

			var record = BehaviorAnimations.RestCast(character.Id, entity?.NextBehaviorPhase() ?? 1);
			// #272: a 20-second rest cast is an animation on the actor - relayed, like every other player behaviour.
			var restCast = BehaviorControlCenterCodec.Build(record);
			await WorldIir.Send(client, restCast);
			await _broadcaster.BroadcastBehaviorToViewers(client, restCast);

			// ⚠️ Rest is a CAST, so it has to arm the cast path's cancel state or it can never be cancelled (#505).
			// The client ends this channel the same way it ends any other: it sends the c2s cancel BCC (seq 0x0002)
			// when the player moves, and `HandleBccCastSpell` answers it with retail's 0x0007 abort - but ONLY if it
			// finds the BEGIN's args here. Rest is answered to a `PerformSpecialAction` rather than to a c2s cast, so
			// nothing ever set them, `beginArgs` came back null, and the cancel fell through to a plain echo instead
			// of the abort. The client then stayed channeling: the bar hung, and a channeling client sends no further
			// cast OR melee, so one press of rest silently disabled every combat action the player had.
			//
			// Verified against `NecroL80_..._cast-general-abilities` frames 867/921/927 - begin seq 0x0004 (phase 8,
			// activation 2050) -> c2s cancel seq 0x0002 -> s2c abort seq 0x0007, whose args are these begin args with
			// the cancel's own command id spliced in. So the record to send back is not a completion at all.
			client.Session.PendingCastArgs = record.Args;
			client.Session.PendingCastMode = 0; // no client command id at begin - the cancel brings its own

			if (entity == null)
				return;

			entity.RestingUntil = DateTimeOffset.UtcNow.AddMilliseconds(RestingChannelMs);
			_logger.LogInformation("Rest: '{Name}' began resting for {Ms} ms.", character.Name, RestingChannelMs);
		}
	}
}
