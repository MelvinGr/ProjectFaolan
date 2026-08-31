using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.GameServer.World;

namespace Faolan.GameServer
{
	// The retail client's OWN command verbs (#914), implemented against the argument grammar recovered from the
	// client in #913 (`docs/re/gm-command-args.tsv`) rather than one we chose.
	//
	// ⭐ Why these are worth having and our invented verbs are not a substitute: the client's hidden GM affordances
	// only ever emit verbs from its own registry, and they arrive here as a `FanaticCommandIIR` that
	// `HandleGmCommand` already routes into the same table as our `.` commands. So a verb implemented here is
	// reachable from the client's own UI; one we invent never is.
	//
	// ⚠️ The grammar is retail's, including its quirks - a verb that "obviously" wants an argument may declare none
	// and parse the line ad hoc, and a verb whose NAME matches one of ours may do a different job (retail's `/time`
	// manipulates FRAME times; the time-of-day setter is `/settime`).
	public partial class GameServerListener
	{
		// The target a GM command acts on. Most of retail's observability verbs are written as "... for your target"
		// and declare no parameters at all, so this is the single most reused step in the whole family.
		private bool TryResolveTarget(INetworkClient client, out WorldEntity target, out string failure)
		{
			target = null!;
			failure = "";

			var player = _simulation.PlayerWorldEntity(client);
			if (player == null)
			{
				failure = "You're not in the world yet.";
				return false;
			}

			if (client.Session.SelectedTargetHandle is not { } handle)
			{
				failure = "No target selected.";
				return false;
			}

			var zone = _simulation.World.ZoneForEntity(player);
			if (zone == null || !zone.TryGet(handle, out var found))
			{
				failure = $"Target 0x{handle:X8} is not in your zone (it may have despawned).";
				return false;
			}

			target = found;
			return true;
		}

		// /distance  - "Prints the 2D and 3D distance between you and your target". Declares NO parameters.
		// ⭐ The 2D/3D split is the point of the command: they diverge exactly where a height difference is doing the
		// work, which is what makes it the instrument for an aggro or interaction range that behaves differently on a
		// slope or a rooftop than it does on flat ground.
		private async Task DistanceCommand(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			var player = _simulation.PlayerWorldEntity(client);
			if (player == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "You're not in the world yet.");
				return;
			}

			if (!TryResolveTarget(client, out var target, out var failure))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, failure);
				return;
			}

			var d = target.Position - player.Position;
			var flat = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"'{target.Name}': 2D {flat:0.##}, 3D {d.Length():0.##} (dx {d.X:0.##}, dy {d.Y:0.##}, dz {d.Z:0.##}).");
		}

		// /whereis <name>  - "Reports the Playfield proxy info on a character". A live character answers from the
		// world model; an offline one still answers, from its persisted position, because "where did they log out"
		// is the question as often as "where are they now" - and the reply says which of the two it is.
		private async Task WhereIsCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var name = (arguments ?? "").Trim();
			if (name.Length == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .whereis <name>");
				return;
			}

			foreach (var other in _simulation.Players.Values)
			{
				if (!other.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
					continue;

				var zone = _simulation.World.ZoneForEntity(other);
				await _agentServerDispatcher.SendSystemMessage(characterId,
					$"'{other.Name}' is ONLINE on map {other.MapId} instance {other.InstanceId}"
					+ $"{(zone == null ? "" : $" (token 0x{zone.PlayfieldToken:X8}, {zone.Count} entities)")}"
					+ $" at {other.Position.X:0.#}, {other.Position.Y:0.#}, {other.Position.Z:0.#}.");
				return;
			}

			var stored = await Database.GetCharacterByName(name, client.Session.Character.RealmId);
			if (stored == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Color("red", $"No character named '{name}' on this realm."));
				return;
			}

			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"'{stored.Name}' (id {stored.Id}) is OFFLINE, last on map {stored.MapId} at {stored.PositionX:0.#}, {stored.PositionY:0.#}, {stored.PositionZ:0.#}.");
		}

		// /playfieldinfo  - "get playfield info for target". Declares no parameters; falls back to the caller's own
		// playfield when nothing is targeted, since that is the reading wanted most of the time.
		private async Task PlayfieldInfoCommand(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			var player = _simulation.PlayerWorldEntity(client);
			if (player == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "You're not in the world yet.");
				return;
			}

			var subject = TryResolveTarget(client, out var target, out _) ? target : player;
			var zone = _simulation.World.ZoneForEntity(subject);
			if (zone == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, $"'{subject.Name}' is on map {subject.MapId} but has no live zone.");
				return;
			}

			var map = await Database.GetMap(subject.MapId);
			var players = zone.Entities.Count(e => e.IsPlayer);
			await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Lines([
				$"Playfield for '{subject.Name}':",
				$"  map {subject.MapId}{(string.IsNullOrEmpty(map?.Name) ? "" : $" ({map.Name})")}, instance {zone.InstanceId}{(zone.IsEpic ? " EPIC" : "")}",
				$"  token 0x{zone.PlayfieldToken:X8}, {zone.Count} entities ({players} players), {zone.WorldObjects.Count} world objects",
				$"  terrain: {(_simulation.Terrain?.Available == true ? "loaded" : "NOT loaded - ground snap and line-of-sight are off")}"
			]));
		}

		// /playfieldinstancelock  - "get list of locked playfields for the target". Our instance model locks a
		// non-default instance to the team that owns it (WorldState.TeamOwning), which is the same idea; report the
		// real state rather than a stub, and say plainly when nothing is locked.
		private async Task PlayfieldInstanceLockCommand(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			var subject = TryResolveTarget(client, out var target, out _) ? target : _simulation.PlayerWorldEntity(client);
			if (subject == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "You're not in the world yet.");
				return;
			}

			var owner = _simulation.World.TeamOwning(subject.MapId, subject.InstanceId);
			await _agentServerDispatcher.SendSystemMessage(characterId, owner is { } teamId
				? $"'{subject.Name}': map {subject.MapId} instance {subject.InstanceId} is LOCKED to team {teamId}."
				: $"'{subject.Name}': map {subject.MapId} instance {subject.InstanceId} is not locked to any team.");
		}

		// /ghost <charid>  - "Prints some info about a character specified by the charid parameter".
		private async Task GhostCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();
			if (!uint.TryParse(arg, out var wanted))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .ghost <charid>");
				return;
			}

			var character = await Database.GetCharacter(wanted);
			if (character == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Color("red", $"No character with id {wanted}."));
				return;
			}

			var live = _simulation.Players.Values.FirstOrDefault(p => p.IsPlayer && p.Client.Session.Character.Id == wanted);
			await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Lines([
				$"Character {character.Id} '{character.Name}':",
				$"  level {character.Level} {character.Class}, realm {character.RealmId}, account {character.AccountId}",
				$"  map {character.MapId} at {character.PositionX:0.#}, {character.PositionY:0.#}, {character.PositionZ:0.#}",
				$"  {(live == null ? "OFFLINE" : $"ONLINE (handle 0x{live.Handle:X8}, instance {live.InstanceId})")}"
			]));
		}

		// /system  - "System info." ⚠️ Retail registers this at mask 0x0, i.e. UNRESTRICTED - the only verb besides
		// our own `.help` that is, and we match retail rather than inventing a bit for it.
		private async Task SystemInfoCommand(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			var zones = _simulation.World.ZonesForMap(client.Session.Character.MapId).ToArray();
			var timeOfDay = (uint)GameClock.TimeOfDay(DateTimeOffset.UtcNow, _gameTimeOffset);

			await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Lines([
				"System info:",
				$"  realm {client.Session.Character.RealmId}, {NetworkClients.Count()} connection(s), {_simulation.Players.Count} player(s) in world",
				$"  your map {client.Session.Character.MapId}: {zones.Length} live instance(s)",
				$"  world time {timeOfDay / 3600:00}:{timeOfDay % 3600 / 60:00} (game day = {GameClock.GameDayRealSeconds / 3600.0:0.#} real hours)",
				$"  terrain {(_simulation.Terrain?.Available == true ? "loaded" : "NOT loaded")}"
			]));
		}

		// /hatedbylist  - "Command for listing which NPCs hate the target". Declares no parameters.
		//
		// ⚠️ We have no persistent threat table: aggro is decided per tick from proximity plus an irritation source,
		// so the honest answer is "who is engaging this entity RIGHT NOW", and the reply says so rather than implying
		// a stored hate list we do not keep. That distinction is exactly what makes this useful for #577 - an empty
		// list next to a hostile NPC standing 5m away is an aggro-gate finding, not a missing-data one.
		private async Task HatedByListCommand(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			var subject = TryResolveTarget(client, out var target, out _) ? target : _simulation.PlayerWorldEntity(client);
			if (subject == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "You're not in the world yet.");
				return;
			}

			var zone = _simulation.World.ZoneForEntity(subject);
			if (zone == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, $"'{subject.Name}' has no live zone.");
				return;
			}

			// What the tick actually uses is proximity plus hostility, so that is what gets reported - split into the
			// two populations that matter for an aggro complaint: who is ENGAGED, and who is close enough to engage
			// and has not. A hostile NPC sitting in the second list a few metres away IS the #577 finding.
			var nearby = zone.Entities
				.Where(e => !e.IsPlayer && e.Hostile && e.Aggressive)
				.Select(e => (Npc: e, Distance: Vector3.Distance(e.Position, subject.Position)))
				.Where(e => e.Distance <= WorldState.NpcLeashRange)
				.OrderBy(e => e.Distance)
				.ToArray();

			var engaged = nearby.Where(e => e.Npc.InCombat).ToArray();
			var idle = nearby.Where(e => !e.Npc.InCombat && e.Distance <= WorldState.NpcAggroRange).ToArray();

			if (nearby.Length == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId,
					$"No hostile NPC within {WorldState.NpcLeashRange:0}u of '{subject.Name}'.");
				return;
			}

			// ⚠️ Said out loud because it changes how the output is read: we keep no persistent hate list, so this is
			// derived state, not a stored one. "Engaged" means the tick has it in combat right now.
			var lines = new List<string> { $"Hostiles near '{subject.Name}' (derived - we keep no stored hate list):" };
			lines.Add($"  ENGAGED: {engaged.Length}");
			lines.AddRange(engaged.Take(10).Select(e => $"    '{e.Npc.Name}' (0x{e.Npc.Handle:X8}) {e.Distance:0.#}u"));
			lines.Add($"  IN AGGRO RANGE BUT NOT ENGAGED: {idle.Length}");
			lines.AddRange(idle.Take(10).Select(e => $"    '{e.Npc.Name}' (0x{e.Npc.Handle:X8}) {e.Distance:0.#}u"));

			await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Lines([.. lines]));
		}

		// /settime <hour:minutes>  - "Set time on the current client and server".
		//
		// ⭐ This IS what our `.time <hh:mm>` did, and it is why `.time` is gone (#914's rule 1). Retail's own `/time`
		// is a different command entirely - it manipulates FRAME times ("speeds up or slows the deltatimer system on
		// server and client") - so keeping our time-of-day setter under that name was a verb with retail's name and
		// someone else's meaning. The `reset` form moved to `/gametime reset`, which is where retail puts it.
		private async Task SetTimeCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();
			if (arg.Length > 0)
			{
				var parts = arg.Split(':', StringSplitOptions.TrimEntries);
				if (parts.Length != 2 || !uint.TryParse(parts[0], out var hours) || hours > 23 || !uint.TryParse(parts[1], out var minutes) || minutes > 59)
				{
					await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .settime <hour:minutes>  (no argument reports the current time)");
					return;
				}

				// Shift the phase so the CURRENT instant reads as the requested time of day.
				var wanted = (long)(hours * 3600 + minutes * 60);
				_gameTimeOffset = (_gameTimeOffset + wanted - (long)GameClock.TimeOfDay(DateTimeOffset.UtcNow, _gameTimeOffset) + GameClock.GameDaySeconds) % GameClock.GameDaySeconds;
			}

			await client.SendAsync(BuildGameTime(client));

			var timeOfDay = (uint)GameClock.TimeOfDay(DateTimeOffset.UtcNow, _gameTimeOffset);
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"World time {timeOfDay / 3600:00}:{timeOfDay % 3600 / 60:00} (game day = {GameClock.GameDayRealSeconds / 3600.0:0.#} real hours, offset {_gameTimeOffset}s)");
		}

		// /gametime * <factor> | / <factor> | reset  - "Speeds up or slows the game timer".
		//
		// ⭐ This one rides a REAL wire field rather than a server-side fudge: GameTimeIIR's trailing f32 `rate` feeds
		// the client clock's own multiplier (+0xD4 in GameTime_t, see GameClock), so the client speeds its day/night
		// cycle up to match instead of drifting against ours. `reset` puts BOTH the rate and the phase offset back to
		// default, which is what retail's "back in the default values" means.
		private async Task GameTimeCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var tokens = (arguments ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

			if (tokens.Length == 1 && tokens[0].Equals("reset", StringComparison.OrdinalIgnoreCase))
			{
				_gameTimeRate = 1.0f;
				_gameTimeOffset = 0;
			}
			else if (tokens.Length == 2 && (tokens[0] == "*" || tokens[0] == "/") &&
			         float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var factor) && factor > 0f)
			{
				_gameTimeRate = tokens[0] == "*" ? _gameTimeRate * factor : _gameTimeRate / factor;
			}
			else if (tokens.Length != 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .gametime * <factor> | / <factor> | reset  (no argument reports the current rate)");
				return;
			}

			await client.SendAsync(BuildGameTime(client));
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Game time rate {_gameTimeRate:0.###}x - a game day now takes {GameClock.GameDayRealSeconds / 3600.0 / _gameTimeRate:0.##} real hours (offset {_gameTimeOffset}s).");
		}
	}
}
