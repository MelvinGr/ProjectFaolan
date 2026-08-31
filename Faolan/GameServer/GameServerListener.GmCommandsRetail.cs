using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
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
		// The spell id a GM /resurrect offer is filed under. Self-resurrection offers are keyed by (character, spell)
		// so that declining one potion's offer cannot drop another's; a GM offer needs an id of its own for the same
		// reason, and 0 is the one value no real resurrection spell uses.
		private const uint GmResurrectionSpellId = 0;

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

		// Finds a live player by character id or by NAME. ⭐ Retail's own help for /summon and /assist warns that
		// "only a small cache of names is kept, so summon on name may fail if the name is no longer cached" - so
		// resolution by id is the reliable form there and by name is best-effort. Ours has no such cache (the world
		// model holds every online player), so the by-name form is exact; the id form is still accepted because that
		// is what the client's own UI is most likely to send.
		private WorldEntity? FindOnlinePlayer(string idOrName)
		{
			if (uint.TryParse(idOrName, out var id))
			{
				var byId = _simulation.Players.Values.FirstOrDefault(p => p.IsPlayer && p.Client.Session.Character.Id == id);
				if (byId != null)
					return byId;
			}

			return _simulation.Players.Values.FirstOrDefault(p => p.IsPlayer && p.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
		}

		// /summon <id | name>  - "Summons a character to your position by id or name."
		private async Task SummonCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();
			if (arg.Length == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .summon <id | name>");
				return;
			}

			var me = _simulation.PlayerWorldEntity(client);
			if (me == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "You're not in the world yet.");
				return;
			}

			var subject = FindOnlinePlayer(arg);
			if (subject == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Color("red", $"No ONLINE character matching '{arg}'. (Summon moves a live character; an offline one has nothing to move.)"));
				return;
			}

			if (subject.Handle == me.Handle)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "You are already where you are.");
				return;
			}

			await _teleport.DoTeleport(subject.Client.Session.Character.Id, me.MapId, me.Position);
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Summoned '{subject.Name}' to your position on map {me.MapId}.");
			await _agentServerDispatcher.SendSystemMessage(subject.Client.Session.Character.Id, $"You have been summoned by '{me.Name}'.");
		}

		// /unsummon <name>  - "Dismisses a character to the last saved location". ⭐ Note what retail means by that:
		// not "send them home" and not "remove them", but back to the position PERSISTED for them - i.e. undo the
		// summon by returning them to wherever they were last saved. That is a DB read, not a bind point.
		private async Task UnsummonCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();
			if (arg.Length == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .unsummon <name>");
				return;
			}

			var subject = FindOnlinePlayer(arg);
			if (subject == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Color("red", $"No ONLINE character matching '{arg}'."));
				return;
			}

			var stored = await Database.GetCharacter(subject.Client.Session.Character.Id);
			if (stored == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Color("red", $"'{subject.Name}' has no saved location to return to."));
				return;
			}

			await _teleport.DoTeleport(stored.Id, stored.MapId, new Vector3(stored.PositionX, stored.PositionY, stored.PositionZ));
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Dismissed '{subject.Name}' to their last saved location (map {stored.MapId} at {stored.PositionX:0.#}, {stored.PositionY:0.#}, {stored.PositionZ:0.#}).");
		}

		// /assist <id | name>  - "Assists a player by teleporting to his/her position." The mirror of /summon: it
		// moves YOU, not them.
		private async Task AssistCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();
			if (arg.Length == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .assist <id | name>");
				return;
			}

			var subject = FindOnlinePlayer(arg);
			if (subject == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Color("red", $"No ONLINE character matching '{arg}'."));
				return;
			}

			await _teleport.DoTeleport(characterId, subject.MapId, subject.Position);
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Teleported to '{subject.Name}' on map {subject.MapId}.");
		}

		// /falldamage on | off  - retail's help states the default: "Enables falldamage (it is default on all players)".
		private async Task FallDamageCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();
			var entity = _simulation.PlayerWorldEntity(client);
			if (entity == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "You're not in the world yet.");
				return;
			}

			if (arg.Equals("on", StringComparison.OrdinalIgnoreCase))
				entity.FallDamageEnabled = true;
			else if (arg.Equals("off", StringComparison.OrdinalIgnoreCase))
				entity.FallDamageEnabled = false;
			else if (arg.Length > 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .falldamage on | off  (no argument reports the current setting)");
				return;
			}

			await _agentServerDispatcher.SendSystemMessage(characterId, entity.FallDamageEnabled
				? ChatMarkup.Raw($"Fall damage {ChatMarkup.Color("lime", "ON")} (retail's default for every player).")
				: ChatMarkup.Raw($"Fall damage {ChatMarkup.Color("red", "OFF")} - falls are still detected, they just do not hurt."));
		}

		// /cash [ [+|-]amount ]  - "Without arguments it gets the current cash value, with an absolute value it sets
		// the cash, with + in front of the value it adds to the cash". ⚠️ The recovered parameter help for `+` and `-`
		// is retail's own COPY-PASTE slip - it reads "Lists all the known tradeskill recipes" / "Learn a recipe",
		// lifted from the tradeskill command. The long description above is the accurate one.
		private async Task CashCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();
			var character = await Database.GetCharacter(characterId);
			if (character == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Your character row could not be loaded.");
				return;
			}

			if (arg.Length > 0)
			{
				var signed = arg[0] is '+' or '-';
				if (!ulong.TryParse(signed ? arg[1..] : arg, out var amount))
				{
					await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .cash [ [+|-]<amount> ]  (no argument reports the balance; bare value SETS it)");
					return;
				}

				// Clamped at zero rather than wrapping: Money is unsigned, so subtracting past empty would hand the
				// player about eighteen quintillion tin.
				var updated = arg[0] switch
				{
					'+' => character.Money + amount,
					'-' => amount >= character.Money ? 0UL : character.Money - amount,
					_ => amount
				};

				// ⚠️ The reason word is decided against the PREVIOUS balance, so it has to be read before the
				// assignment overwrites it - comparing `updated` to an already-updated field is always "gain".
				var reason = updated >= character.Money ? UpdateCashReason.Gain : UpdateCashReason.Spend;
				character.Money = updated;
				await Database.SetCharacterMoney(characterId, updated);
				await _economyOutbound.SendUpdateCash(client, updated, reason);
			}

			await _agentServerDispatcher.SendSystemMessage(characterId, $"Cash: {character.Money} tin.");
		}

		// /inflict <damage>  - "Inflicts damage of the specified damage type on your target."
		//
		// ⚠️ Not a duplicate of our `.hurt`, and neither is removed: `.hurt [percent]` lowers YOUR OWN three pools by
		// a proportion, while /inflict deals a flat amount to your TARGET. Same subsystem, opposite ends of it.
		private async Task InflictCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();
			if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var damage) || damage <= 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .inflict <damage>  (a positive amount, dealt to your target)");
				return;
			}

			var me = _simulation.PlayerWorldEntity(client);
			if (me == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "You're not in the world yet.");
				return;
			}

			if (!TryResolveTarget(client, out var target, out var failure))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, failure);
				return;
			}

			await _combat.ApplyGmDamage(client, target.Handle, damage, me.Handle);
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Inflicted {damage} on '{target.Name}'.");
		}

		// /areaknockback <distance>  - "Performs an area knockback on everybody within the distance parameter of your
		// target".
		//
		// ⚠️ Retail's own two descriptions of the single parameter CONTRADICT each other: the summary calls it the
		// radius ("everybody within the distance parameter of your target") while the parameter's own help calls it
		// the force ("How far targets will be knocked back"), and the sibling /knockback uses the identical parameter
		// purely as force. We follow the parameter help - one value, the FORCE, matching /knockback - and take the
		// area from the same aggro radius the rest of the simulation uses, because a command whose single number
		// silently meant something different from its singular twin would be the worse surprise.
		private async Task AreaKnockbackCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();
			if (!float.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var distance) || distance <= 0f)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .areaknockback <distance>  (the force, as .knockback - everyone around your target is shoved)");
				return;
			}

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

			// Everyone around the epicentre is pushed AWAY from it, so the source is the subject's own position.
			var knocked = 0;
			foreach (var victim in zone.EntitiesInRange(subject.Position, WorldState.NpcAggroRange, subject.Handle).ToArray())
			{
				if (await _knockback.Knock(victim, subject.Position, distance))
					knocked++;
			}

			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Area knockback {distance:0.##} around '{subject.Name}': {knocked} entity(s) shoved.");
		}

		// /resurrect <strength>  - "Resurrects the character with a strength between 1 and 100."
		//
		// ⭐ Strength maps exactly onto the restore PERCENTAGE our resurrection offers already carry (a Potion
		// restores 100, a Minor Elixir 25), so this arms the same self-resurrection path a consumable does rather
		// than inventing a second revive. The player still takes the offer, which is what keeps the revive on the one
		// code path that knows how to put a dead character back on its feet.
		private async Task ResurrectCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();
			if (!uint.TryParse(arg, out var strength) || strength is 0 or > 100)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .resurrect <strength 1-100>  (percent of each pool restored)");
				return;
			}

			// The target when it is a player, else the caller - retail's class is "resurrects the character", and the
			// character in question is whoever is dead and selected.
			var subject = TryResolveTarget(client, out var target, out _) && target.IsPlayer ? target : _simulation.PlayerWorldEntity(client);
			if (subject == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "You're not in the world yet.");
				return;
			}

			var subjectId = subject.Client.Session.Character.Id;
			_playerDeaths.ArmSelfResurrection(subjectId, GmResurrectionSpellId, strength);
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Armed a {strength}% resurrection for '{subject.Name}' - they accept it from the death screen.");
			if (subjectId != characterId)
				await _agentServerDispatcher.SendSystemMessage(subjectId, $"A resurrection ({strength}%) has been offered to you.");
		}

		// A stat named either as a number (decimal or 0x-hex) or by its CLIENT name - retail's whole stat family takes
		// `statid | statname`, and the enum carries all 1,515 names, so the name form costs nothing to support.
		private static bool TryResolveStat(string text, out CharacterStatId stat)
		{
			if (TryParseStatId(text, out stat) && Enum.IsDefined(stat))
				return true;

			return Enum.TryParse(text, ignoreCase: true, out stat) && Enum.IsDefined(stat);
		}

		// The three views retail's /get offers, which happen to be exactly the three sources our stat model keeps
		// separate: `base` is the class/level base before anything is added, `item` is the gear bonus, and `full` is
		// the fully buffed live value.
		private static (int Base, int Item, int Full) StatViews(WorldEntity entity, CharacterStatId stat)
		{
			// ⚠️ ClassBase is the RAW WIRE value while the gear bonus is already divided by the stat's scale, so the
			// base has to be converted before the two are comparable - the same trap AttributeScore documents.
			var scaled = entity.ClassBase.GetValueOrDefault(stat) / (int)stat.GetScale();
			var item = entity.GearBonuses.GetValueOrDefault(stat);
			var full = entity.BuffedStats.TryGetValue(stat, out var buffed) ? (int)buffed : scaled + item;
			return (scaled, item, full);
		}

		// /get <statname | statnr> [base | full | item] [server | client]
		//   "Gets a stat or stat range from the target dynel. It will default to you if you do not have a target."
		private async Task GetStatCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var tokens = (arguments ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
			if (tokens.Length == 0 || !TryResolveStat(tokens[0], out var stat))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .get <statname | statnr> [base | full | item] [server | client]");
				return;
			}

			// Retail's own fallback, stated in its long description: the target if there is one, else you.
			var subject = TryResolveTarget(client, out var target, out _) ? target : _simulation.PlayerWorldEntity(client);
			if (subject == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "You're not in the world yet.");
				return;
			}

			var wants = tokens.Skip(1).Select(t => t.ToLowerInvariant()).ToArray();
			if (wants.Contains("client"))
			{
				// ⚠️ Said plainly rather than answered with the server's number wearing a "client" label: the client's
				// own copy of a stat is not readable from here - we only ever WRITE it. `/setlocal` is what pushes one.
				await _agentServerDispatcher.SendSystemMessage(characterId,
					ChatMarkup.Color("orange", "The CLIENT's copy of a stat cannot be read from the server - only written (.setlocal). Showing nothing rather than the server value mislabelled."));
				return;
			}

			var (baseValue, item, full) = StatViews(subject, stat);
			var asked = wants.FirstOrDefault(w => w is "base" or "full" or "item");
			var body = asked switch
			{
				"base" => $"base {baseValue}",
				"item" => $"item {item}",
				"full" => $"full {full}",
				_ => $"base {baseValue}, item {item}, full {full}"
			};

			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"'{subject.Name}' {stat} ({(uint)stat}, 0x{(uint)stat:X}, scale {stat.GetScale()}): {body}.");
		}

		// /set and /setlocal: `statid | statname [+ | - | t | c | s]value`
		//
		// ⭐ The two are ONE class in the client, told apart by the registry record's mode byte (0 = /set, 1 =
		// /setlocal) - see docs/re/gm-command-args.tsv. So they are one implementation with a flag here too.
		//
		// ⭐⭐ `t`, `c` and `s` are BIT operations - toggle, clear and set a flag on the stat. That is worth noticing
		// on its own: retail's stat setter assumes stats are routinely BITFIELDS, not just scalars, which is the same
		// shape the GM privilege mask and the vanity mask already have.
		private async Task SetStatValueCommand(INetworkClient client, string arguments, bool localOnly)
		{
			var characterId = client.Session.Character.Id;
			var verb = localOnly ? ".setlocal" : ".set";
			var tokens = (arguments ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
			if (tokens.Length != 2 || !TryResolveStat(tokens[0], out var stat))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId,
					$"Usage: {verb} <statid | statname> [+|-|t|c|s]<value>   (+ add, - subtract, t toggle bit, c clear bit, s set bit, bare = assign)");
				return;
			}

			var spec = tokens[1];
			var op = spec.Length > 0 && "+-tcs".Contains(spec[0]) ? spec[0] : '=';
			if (!uint.TryParse(op == '=' ? spec : spec[1..], out var operand))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, $"Not a value: '{spec}'. {verb} takes [+|-|t|c|s]<number>.");
				return;
			}

			var subject = TryResolveTarget(client, out var target, out _) ? target : _simulation.PlayerWorldEntity(client);
			if (subject == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "You're not in the world yet.");
				return;
			}

			var (_, _, current) = StatViews(subject, stat);
			// ⚠️ The bit operators take the operand as a BIT INDEX, not a mask - "toggles a flag on the stat" is one
			// flag, and a mask would make `t1` and `t2` the same edit for adjacent bits.
			var bit = operand < 32 ? 1u << (int)operand : 0u;
			var updated = op switch
			{
				'+' => current + (int)operand,
				'-' => current - (int)operand,
				't' => (int)((uint)current ^ bit),
				'c' => (int)((uint)current & ~bit),
				's' => (int)((uint)current | bit),
				_ => (int)operand
			};

			updated = Math.Max(0, updated);
			if (!localOnly)
				subject.BuffedStats[stat] = (uint)updated;

			await SendStatUpdate(client, new CharacterStatBlock().SetRaw(stat, (uint)updated), characterId);
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"'{subject.Name}' {stat} ({(uint)stat}): {current} -> {updated}{(localOnly ? " (CLIENT only - server state unchanged)" : "")}.");
		}

		// /getskill <statid | statname>  - "Prints the skillvalue buffed and base, max skill value and the float, rank
		// and level."
		//
		// ⚠️ Retail prints six things and we hold four. The float and the rank are not modelled (#507), so they are
		// named as missing rather than printed as zero - a zero rank would read as a real answer.
		private async Task GetSkillCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();
			if (arg.Length == 0 || !TryResolveStat(arg, out var stat))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .getskill <statid | statname>");
				return;
			}

			var subject = TryResolveTarget(client, out var target, out _) ? target : _simulation.PlayerWorldEntity(client);
			if (subject == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "You're not in the world yet.");
				return;
			}

			var (baseValue, item, full) = StatViews(subject, stat);
			await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Lines([
				$"'{subject.Name}' skill {stat} ({(uint)stat}, 0x{(uint)stat:X}):",
				$"  base {baseValue}, item {item}, buffed {full} (scale {stat.GetScale()})",
				$"  level {subject.Level}",
				"  float and rank: not modelled (#507)"
			]));
		}

		// /teleporttarget <posx> <posz> [y <posy>] [<playfield>]  - the same grammar as the rest of the family, on the
		// TARGET instead of yourself. ⭐ It is the mode byte in the registry record that says so: /teleporttarget is
		// mode 0 while /teleport, /teleportself and /tp are all mode 1.
		private async Task TeleportTargetCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var tokens = (arguments ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
			if (!TryResolveTarget(client, out var target, out var failure))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, failure);
				return;
			}

			if (!target.IsPlayer)
			{
				// Honest gap rather than a silent no-op: DoTeleport moves a CHARACTER (it re-runs world entry), and an
				// NPC has no character to move. Relocating a live NPC entity is a different mechanism we have not built.
				await _agentServerDispatcher.SendSystemMessage(characterId,
					ChatMarkup.Color("orange", $"'{target.Name}' is an NPC - only player targets can be teleported (moving a live NPC entity is a separate mechanism we do not have yet)."));
				return;
			}

			if (tokens.Length is not (2 or 4 or 5) ||
			    !float.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
			    !float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var z) ||
			    (tokens.Length > 2 && !tokens[2].Equals("y", StringComparison.OrdinalIgnoreCase)))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .teleporttarget <x> <z> [y <y>] [<playfield>]");
				return;
			}

			var mapId = target.MapId;
			if (tokens.Length == 5 && uint.TryParse(tokens[4], out var statedMap))
				mapId = statedMap;

			float y;
			if (tokens.Length >= 4 && float.TryParse(tokens[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var statedY))
				y = statedY;
			else if (_simulation.Terrain?.GroundHeight(mapId, x, z) is { } ground)
				y = ground;
			else
			{
				y = target.Position.Y;
				await _agentServerDispatcher.SendSystemMessage(characterId,
					ChatMarkup.Color("orange", $"No terrain loaded for map {mapId} - keeping their current height ({y:0.#})."));
			}

			await _teleport.DoTeleport(target.Client.Session.Character.Id, mapId, new Vector3(x, y, z));
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Teleported '{target.Name}' to {x:0.#}, {y:0.#}, {z:0.#} on map {mapId}.");
		}
	}
}
