using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Enums;
using Faolan.Core.Helpers;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Progression;
using Faolan.GameServer.Economy;
using Faolan.GameServer.Inventory;
using Faolan.GameServer.Wire;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Gm
{
	public sealed partial class GmCommandService
	{
		/// <summary>
		///     Where a character stood before /summon moved them, so /unsummon can put them back.
		/// </summary>
		/// <remarks>
		///     Required, not a convenience: DoTeleport persists the destination (SaveCharacterTransform), so by the time
		///     /unsummon runs the character's "last saved location" is the summoned position, and dismissing them would
		///     return them to exactly where they already stand. The return point has to be captured at summon time or
		///     it is gone.
		///     <para>
		///         Not persisted, so it does not survive a server restart - the fallback below (the stored position)
		///         then applies, which is the same answer a character that was never summoned gets.
		///     </para>
		/// </remarks>
		private readonly ConcurrentDictionary<uint, (uint MapId, Vector3 Position)> _summonReturns = new();

		/// <summary>
		///     The spell id a GM /resurrect offer is filed under. Self-resurrection offers are keyed by
		///     (character, spell) so that declining one potion's offer cannot drop another's; a GM offer needs an id of
		///     its own for the same reason, and 0 is the one value no real resurrection spell uses.
		/// </summary>
		private const uint GmResurrectionSpellId = 0;

		/// <summary>
		///     The target a GM command acts on. Most of retail's observability verbs are written as "... for your
		///     target" and declare no parameters at all, so this is the single most reused step in the whole family.
		/// </summary>
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

		// /distance  - "Prints the 2D and 3D distance between you and your target". Declares no parameters.
		// The 2D/3D split is the point of the command: they diverge exactly where a height difference is doing the
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

			var stored = await _database.GetCharacterByName(name, client.Session.Character.RealmId);
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

			var map = await _database.GetMap(subject.MapId);
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

			var character = await _database.GetCharacter(wanted);
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

		// /system  - "System info." Retail registers this at mask 0x0, i.e. unrestricted - the only verb besides
		// `.help` that is, and that registration is matched rather than inventing a bit for it.
		private async Task SystemInfoCommand(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			var zones = _simulation.World.ZonesForMap(client.Session.Character.MapId).ToArray();
			var timeOfDay = (uint)GameClock.TimeOfDay(DateTimeOffset.UtcNow, _clock.OffsetGameSeconds);

			await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Lines([
				"System info:",
				$"  realm {client.Session.Character.RealmId}, {_connections.Clients.Count()} connection(s), {_simulation.Players.Count} player(s) in world",
				$"  your map {client.Session.Character.MapId}: {zones.Length} live instance(s)",
				$"  world time {timeOfDay / 3600:00}:{timeOfDay % 3600 / 60:00} (game day = {GameClock.GameDayRealSeconds / 3600.0:0.#} real hours)",
				$"  terrain {(_simulation.Terrain?.Available == true ? "loaded" : "NOT loaded")}"
			]));
		}

		// /hatedbylist  - "Command for listing which NPCs hate the target". Declares no parameters.
		//
		// There is no persistent threat table: aggro is decided per tick from proximity plus an irritation source, so
		// the answer is "who is engaging this entity right now", and the reply says so rather than implying a stored
		// hate list. That distinction is what makes the command an aggro-gate instrument - an empty list next to a
		// hostile NPC standing 5 m away is a finding, not missing data.
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

			// What the tick uses is proximity plus hostility, so that is what gets reported - split into the two
			// populations that matter for an aggro complaint: who is engaged, and who is close enough to engage and has
			// not. A hostile NPC sitting a few metres away in the second list is the finding.
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

			// Said out loud because it changes how the output is read: there is no persistent hate list, so this is
			// derived state. "Engaged" means the tick has it in combat right now.
			var lines = new List<string>
			{
				$"Hostiles near '{subject.Name}' (derived - we keep no stored hate list):",
				$"  ENGAGED: {engaged.Length}"
			};
			lines.AddRange(engaged.Take(10).Select(e => $"    '{e.Npc.Name}' (0x{e.Npc.Handle:X8}) {e.Distance:0.#}u"));
			lines.Add($"  IN AGGRO RANGE BUT NOT ENGAGED: {idle.Length}");
			lines.AddRange(idle.Take(10).Select(e => $"    '{e.Npc.Name}' (0x{e.Npc.Handle:X8}) {e.Distance:0.#}u"));

			await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Lines([.. lines]));
		}

		// /settime <hour:minutes>  - "Set time on the current client and server". This is the time-of-day setter;
		// retail's `/time` is a different command entirely (it manipulates frame times, "speeds up or slows the
		// deltatimer system on server and client"), and the `reset` form belongs to `/gametime reset`.
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

				// Shift the phase so the current instant reads as the requested time of day.
				var wanted = (long)(hours * 3600 + minutes * 60);
				_clock.OffsetGameSeconds = (_clock.OffsetGameSeconds + wanted - (long)GameClock.TimeOfDay(DateTimeOffset.UtcNow, _clock.OffsetGameSeconds) + GameClock.GameDaySeconds) % GameClock.GameDaySeconds;
			}

			await client.SendAsync(GameWire.BuildGameTime(client, DateTimeOffset.UtcNow, _clock.OffsetGameSeconds, _clock.Rate));

			var timeOfDay = (uint)GameClock.TimeOfDay(DateTimeOffset.UtcNow, _clock.OffsetGameSeconds);
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"World time {timeOfDay / 3600:00}:{timeOfDay % 3600 / 60:00} (game day = {GameClock.GameDayRealSeconds / 3600.0:0.#} real hours, offset {_clock.OffsetGameSeconds}s)");
		}

		// /gametime * <factor> | / <factor> | reset  - "Speeds up or slows the game timer".
		//
		// This rides a real wire field rather than a server-side fudge: GameTimeIIR's trailing f32 `rate` feeds the
		// client clock's own multiplier (+0xD4 in GameTime_t, see GameClock), so the client speeds its day/night cycle
		// up to match instead of drifting against the server's. `reset` puts both the rate and the phase offset back to
		// default, which is what retail's "back in the default values" means.
		private async Task GameTimeCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var tokens = (arguments ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

			if (tokens.Length == 1 && tokens[0].Equals("reset", StringComparison.OrdinalIgnoreCase))
			{
				_clock.Rate = 1.0f;
				_clock.OffsetGameSeconds = 0;
			}
			else if (tokens.Length == 2 && (tokens[0] == "*" || tokens[0] == "/") &&
			         float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var factor) && factor > 0f)
			{
				_clock.Rate = tokens[0] == "*" ? _clock.Rate * factor : _clock.Rate / factor;
			}
			else if (tokens.Length != 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .gametime * <factor> | / <factor> | reset  (no argument reports the current rate)");
				return;
			}

			await client.SendAsync(GameWire.BuildGameTime(client, DateTimeOffset.UtcNow, _clock.OffsetGameSeconds, _clock.Rate));
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Game time rate {_clock.Rate:0.###}x - a game day now takes {GameClock.GameDayRealSeconds / 3600.0 / _clock.Rate:0.##} real hours (offset {_clock.OffsetGameSeconds}s).");
		}

		// Finds a live player by character id or by name. Retail's own help for /summon and /assist warns that "only a
		// small cache of names is kept, so summon on name may fail if the name is no longer cached", so there the id
		// form is the reliable one. There is no such cache here (the world model holds every online player), so the
		// by-name form is exact; the id form is still accepted because that is what the client's UI is most likely to
		// send.
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

			// Captured before the teleport, which overwrites the persisted position.
			var subjectId = subject.Client.Session.Character.Id;
			_summonReturns[subjectId] = (subject.MapId, subject.Position);

			await _teleport.DoTeleport(subjectId, me.MapId, me.Position);
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Summoned '{subject.Name}' to your position on map {me.MapId}. (.unsummon {subject.Name} returns them to map {subject.MapId}.)");
			await _agentServerDispatcher.SendSystemMessage(subjectId, $"You have been summoned by '{me.Name}'.");
		}

		// /unsummon <name>  - "Dismisses a character to the last saved location". That means neither "send them home"
		// nor "remove them", but back to the position persisted for them - a DB read, not a bind point.
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

			var subjectId = subject.Client.Session.Character.Id;

			// The summon's own return point wins. Only when there is none - they were never summoned by us, or the
			// server restarted since - does the persisted position apply, which is what retail's wording describes.
			if (_summonReturns.TryRemove(subjectId, out var mark))
			{
				await _teleport.DoTeleport(subjectId, mark.MapId, mark.Position);
				await _agentServerDispatcher.SendSystemMessage(characterId,
					$"Dismissed '{subject.Name}' to where they were summoned from (map {mark.MapId} at {mark.Position.X:0.#}, {mark.Position.Y:0.#}, {mark.Position.Z:0.#}).");
				return;
			}

			var stored = await _database.GetCharacter(subjectId);
			if (stored == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Color("red", $"'{subject.Name}' has no saved location to return to."));
				return;
			}

			await _teleport.DoTeleport(stored.Id, stored.MapId, new Vector3(stored.PositionX, stored.PositionY, stored.PositionZ));
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Dismissed '{subject.Name}' to their last saved location (map {stored.MapId} at {stored.PositionX:0.#}, {stored.PositionY:0.#}, {stored.PositionZ:0.#})."
				+ " No summon of ours to undo, so this is the persisted position.");
		}

		// /assist <id | name>  - "Assists a player by teleporting to his/her position." The mirror of /summon: it moves
		// the caller, not the target.
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
		// the cash, with + in front of the value it adds to the cash". The recovered parameter help for `+` and `-` is
		// retail's own copy-paste slip - it reads "Lists all the known tradeskill recipes" / "Learn a recipe", lifted
		// from the tradeskill command - so the long description above is the accurate one.
		private async Task CashCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();
			var character = await _database.GetCharacter(characterId);
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

				// The reason word is decided against the previous balance, so it has to be read before the assignment
				// overwrites it - comparing `updated` to an already-updated field is always "gain".
				var reason = updated >= character.Money ? UpdateCashReason.Gain : UpdateCashReason.Spend;
				character.Money = updated;
				await _database.SetCharacterMoney(characterId, updated);
				await _economyOutbound.SendUpdateCash(client, updated, reason);
			}

			await _agentServerDispatcher.SendSystemMessage(characterId, $"Cash: {character.Money} tin.");
		}

		// /inflict <damage>  - "Inflicts damage of the specified damage type on your target." Not a duplicate of
		// `.hurt`: that lowers the caller's own three pools by a proportion, while /inflict deals a flat amount to the
		// target.
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
		// Retail's two descriptions of the single parameter contradict each other: the summary calls it the radius
		// ("everybody within the distance parameter of your target") while the parameter's own help calls it the force
		// ("How far targets will be knocked back"). The client settles it in favour of the parameter help:
		// `FanaticAreaKnockbackCommand_c`'s parameter descriptor (vftable 0x1fba5c4 slot 5, `FUN_00626e60`) declares
		// exactly one parameter - name "distance", help "Float: How far targets will be knocked back" - and the sibling
		// `FanaticKnockbackCommand_c` (`FUN_00626bb0`) declares the identical one, differing only in "target" vs
		// "targets". So both verbs take one force, and the area comes from elsewhere: it uses the same aggro radius the
		// rest of the simulation does.
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

			// Everyone around the epicentre is pushed away from it, so the source is the subject's own position.
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
		// Strength maps onto the restore percentage the resurrection offers already carry (a Potion restores 100, a
		// Minor Elixir 25), so this arms the same self-resurrection path a consumable does rather than a second revive.
		// The player still takes the offer, which keeps the revive on the one code path that knows how to put a dead
		// character back on its feet.
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

		// A stat named either as a number (decimal or 0x-hex) or by its client name - retail's whole stat family takes
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
			// ClassBase is the raw wire value while the gear bonus is already divided by the stat's scale, so the base
			// has to be converted before the two are comparable - the same rule AttributeScore documents.
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
				// Said plainly rather than answered with the server's number wearing a "client" label: the client's own
				// copy of a stat is not readable from here, only writable. `/setlocal` is what pushes one.
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
		// The two are one class in the client, told apart by the registry record's mode word at [0xC] (0 = /set,
		// 1 = /setlocal) - see docs/re/gm-command-args.tsv - so they are one implementation with a flag here too.
		//
		// `t`, `c` and `s` are bit operations - toggle, clear and set a flag on the stat. Retail's stat setter assumes
		// stats are routinely bitfields, not just scalars, the same shape the GM privilege mask and the vanity mask
		// have.
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
			// The bit operators take the operand as a bit index, not a mask - "toggles a flag on the stat" is one flag,
			// and a mask would make `t1` and `t2` the same edit for adjacent bits.
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

			await GameWire.SendStatUpdate(client, new CharacterStatBlock().SetRaw(stat, (uint)updated), characterId);
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"'{subject.Name}' {stat} ({(uint)stat}): {current} -> {updated}{(localOnly ? " (CLIENT only - server state unchanged)" : "")}.");
		}

		// /getskill <statid | statname>  - "Prints the skillvalue buffed and base, max skill value and the float, rank
		// and level."
		//
		// Retail prints six things and four are held here. The float and the rank are not modelled, so they are named
		// as missing rather than printed as zero - a zero rank would read as a real answer.
		// Open work tracked in Gitea #507.
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
		// target instead of the caller. The mode word in the registry record says so: /teleporttarget is mode 0 while
		// /teleport, /teleportself and /tp are all mode 1.
		//
		// Retail's description says the family teleports "self or another dynel", so the target may be an NPC. The two
		// halves are different mechanisms: a player teleport is a changemap handoff that re-runs world entry on a new
		// connection; an NPC has neither character nor connection and is moved in the world model, with every viewer in
		// the zone re-resolved around it.
		private async Task TeleportTargetCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var tokens = (arguments ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
			if (!TryResolveTarget(client, out var target, out var failure))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, failure);
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

			var destination = new Vector3(x, y, z);

			// Retail describes the whole family as teleporting "self or another dynel", and a dynel is any world
			// entity, so an NPC target is in scope. The two halves are genuinely different mechanisms, not one with a
			// branch: a player teleport is a changemap handoff that re-runs world entry on a new connection, while an
			// NPC has no character and no connection - it is moved in the world model and the viewers are re-resolved
			// around it.
			if (!target.IsPlayer)
			{
				if (mapId != target.MapId)
				{
					// Cross-map for a non-player would mean taking the handle out of one zone and into another while
					// every viewer of the old one still holds it. Refused by name rather than half-done.
					await _agentServerDispatcher.SendSystemMessage(characterId,
						ChatMarkup.Color("orange", $"'{target.Name}' is an NPC - it can only be moved within its own playfield (map {target.MapId}), not to map {mapId} (#917)."));
					return;
				}

				var resolved = await _simulation.RelocateEntity(target, destination);
				await _agentServerDispatcher.SendSystemMessage(characterId,
					$"Moved '{target.Name}' to {x:0.#}, {y:0.#}, {z:0.#} on map {mapId} ({resolved} player view(s) re-resolved). Live only - it respawns at its placed spot.");
				return;
			}

			await _teleport.DoTeleport(target.Client.Session.Character.Id, mapId, destination);
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Teleported '{target.Name}' to {x:0.#}, {y:0.#}, {z:0.#} on map {mapId}.");
		}

		// The most repeats a `[duration pause]` broadcast will ever schedule. Retail states no bound; this one exists
		// so a fat-fingered `.broadcast 100000 1 hi` cannot pin a background loop to the process for a day.
		private const int MaxBroadcastRepeats = 60;

		// /broadcast, /servcast and /sitecast: `[duration pause] message`
		//
		// The three scopes, in the client's own words. All three are one class, and the discriminator is the word at
		// [obj+0xC] (a byte-wide read of it truncates the distinction away). The registration sites write it
		// distinctly:
		//
		//     /servcast   0x0000     /broadcast  0x0100     /sitecast  0x0001
		//
		// and `FUN_00637270` (the class's own description getter) branches on the two bytes and names each one:
		//
		//     [0xC]=0 [0xD]=0  ->  "Broadcasts a message to the server"
		//     [0xC]=0 [0xD]=1  ->  "Broadcasts a message to the playfield"
		//     [0xC]=1 [0xD]=0  ->  "Broadcasts a message to the dimension"
		//     anything else    ->  "ERROR, impossible combination of flags"
		//
		// So the hierarchy is playfield < server < dimension, and `/broadcast` is the narrowest of the three.
		//
		// `/sitecast`'s dimension is wider than one realm and a realm host only holds its own players, so it reaches
		// this realm and says so rather than implying a cross-realm fan-out that does not exist.
		private async Task BroadcastCommand(INetworkClient client, string arguments, string verb)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();
			if (arg.Length == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, $"Usage: .{verb} [<duration> <pause>] <message>   (duration and pause in seconds)");
				return;
			}

			// `[duration pause]` is optional and positional, so it is present only when the line starts with two
			// integers. A message beginning with a number is therefore ambiguous in retail's own grammar too; two
			// leading integers win, which is what the declaration order says.
			var tokens = arg.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
			var duration = 0;
			var pause = 0;
			var message = arg;
			if (tokens.Length >= 3 && int.TryParse(tokens[0], out duration) && int.TryParse(tokens[1], out pause) && duration > 0 && pause > 0)
				message = string.Join(' ', tokens.Skip(2));
			else
				duration = pause = 0;

			var repeats = pause > 0 ? Math.Min(MaxBroadcastRepeats, duration / pause) : 0;
			var announcement = ChatMarkup.Color("yellow", message);

			// The scope of this verb (see the remark above).
			var playfieldOnly = verb.Equals("broadcast", StringComparison.OrdinalIgnoreCase);
			var scope = playfieldOnly
				? "in this playfield"
				: "on this realm";

			var reached = await FanOutBroadcast(announcement, playfieldOnly ? client : null);
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$".{verb} -> {reached} player(s) {scope}"
				+ (verb.Equals("sitecast", StringComparison.OrdinalIgnoreCase) ? " (retail's DIMENSION is wider than one realm; this host holds only its own - #324)" : "")
				+ $"{(repeats > 0 ? $", repeating every {pause}s for {duration}s ({repeats} more)" : "")}.");

			if (repeats <= 0)
				return;

			// Fire-and-forget on purpose, and off the GM's zone: the GM gets their prompt back immediately and the repeats
			// run behind it. They only read the player list and send chat, so they need no zone. Failures are logged
			// rather than thrown into nothing, and the loop is bounded above.
			WorkLoop.Spawn(async () =>
			{
				for (var i = 0; i < repeats; i++)
				{
					await Task.Delay(TimeSpan.FromSeconds(pause));
					await FanOutBroadcast(announcement, playfieldOnly ? client : null);
				}
			}, _logger, $"GM .{verb} repeat loop");
		}

		// `inPlayfieldOf` non-null narrows the fan-out to that client's own playfield, which is `/broadcast`'s scope.
		// Null is the realm, which is `/servcast` and as much of `/sitecast`'s dimension as a realm host can reach.
		private async Task<int> FanOutBroadcast(ChatMarkup announcement, INetworkClient? inPlayfieldOf = null)
		{
			var recipients = _simulation.Players.Values.Where(p => p.IsPlayer).ToList();
			if (inPlayfieldOf != null)
			{
				// The playfield is the (map, instance) pair, not the map alone - two copies of one zone are different
				// playfields and a message to one must not reach the other.
				var here = _simulation.PlayerWorldEntity(inPlayfieldOf);
				recipients = here == null
					? []
					: recipients.Where(p => p.MapId == here.MapId && p.InstanceId == here.InstanceId).ToList();
			}

			foreach (var player in recipients)
				await _agentServerDispatcher.SendSystemMessage(player.Client.Session.Character.Id, announcement);

			return recipients.Count;
		}

		// /mobs off | on [id | name]  - "Enables/disables InvisibleToMonsters". Explicit on/off rather than a toggle,
		// and it can be aimed at another character. Retail's note "only works on debugservers" is about their
		// deployment, not a limit here.
		private async Task MobsCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var tokens = (arguments ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
			if (tokens.Length == 0 || !(tokens[0].Equals("on", StringComparison.OrdinalIgnoreCase) || tokens[0].Equals("off", StringComparison.OrdinalIgnoreCase)))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .mobs on | off [<id> | <name>]   (no id/name = yourself)");
				return;
			}

			var wanted = tokens[0].Equals("on", StringComparison.OrdinalIgnoreCase);
			var subject = tokens.Length > 1 ? FindOnlinePlayer(string.Join(' ', tokens.Skip(1))) : _simulation.PlayerWorldEntity(client);
			if (subject == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Color("red", tokens.Length > 1
					? $"No ONLINE character matching '{string.Join(' ', tokens.Skip(1))}'."
					: "You're not in the world yet."));
				return;
			}

			subject.Invisible = wanted;

			// Persisted on the account, so it survives relog and map changes.
			var account = subject.Client.Session.Account;
			if (account != null)
				account.GmLevel = await _database.SetAccountGmPrivilege(account.Id, GmPrivileges.InvisibleToMonsters, wanted);

			await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Raw(wanted
				? $"'{subject.Name}' is now {ChatMarkup.Color("lime", "INVISIBLE")} to monsters - NPCs won't aggro."
				: $"'{subject.Name}' is {ChatMarkup.Color("red", "VISIBLE")} to monsters again."));
		}

		// /char  - "Flags the character so all IIRs will be logged." Declares no parameters, so it is a toggle on the
		// caller's own session. The point is scope, not volume: one flagged character logs at information level while
		// everyone else stays at debug, which is what makes a trace usable on a populated server.
		private async Task CharLogCommand(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			client.Session.LogAllIirs = !client.Session.LogAllIirs;
			await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Raw(client.Session.LogAllIirs
				? $"IIR logging {ChatMarkup.Color("lime", "ON")} for this character - every inbound packet goes to faolan.log."
				: $"IIR logging {ChatMarkup.Color("red", "OFF")} for this character."));
		}

		// /feat  - "Lists all the feats on your target." Read-only, mask GetStats: retail's `/feat` only lists, and
		// does so for the target; the manipulation verb is `/feats` (mask SetStats).
		private async Task FeatListCommand(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			var subject = TryResolveTarget(client, out var target, out _) && target.IsPlayer ? target : _simulation.PlayerWorldEntity(client);
			if (subject == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "You're not in the world yet.");
				return;
			}

			var feats = await _database.GetCharacterFeats(subject.Client.Session.Character.Id);
			await _agentServerDispatcher.SendSystemMessage(characterId, feats.Length == 0
				? $"'{subject.Name}' has no trained feats."
				: $"'{subject.Name}' has {feats.Length} feat(s): {string.Join(", ", feats)}");
		}

		// /kickban [Character Name] minutes  - "Kicks a player for the specified amount of time (minutes)." Not a
		// duplicate of `.ban`, which is indefinite and account-wide until someone runs `.unban`; this is a timed kick
		// that lifts itself.
		//
		// The lift is a DB property, not a background task: `Account.BanExpiresAt` is written with the state in one go,
		// and the next login attempt lifts it if the moment has passed (`ClearExpiredBan`, called from the
		// UniverseAgent's login gate). So a restart inside the window still holds the ban, and one that spans the whole
		// window releases it.
		private async Task KickBanCommand(INetworkClient client, string arguments)
		{
			var gmId = client.Session.Character.Id;
			var tokens = (arguments ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

			// The name is optional in retail's grammar (`[Character Name] minutes`), so with one token it is the
			// duration and the subject is the caller's target.
			string? name = null;
			int minutes;
			if (tokens.Length == 1 && int.TryParse(tokens[0], out minutes))
			{
				if (!TryResolveTarget(client, out var target, out var failure) || !target.IsPlayer)
				{
					await _agentServerDispatcher.SendSystemMessage(gmId, tokens.Length == 1 ? failure : "Target a player, or name one.");
					return;
				}

				name = target.Name;
			}
			else if (tokens.Length >= 2 && int.TryParse(tokens[^1], out minutes))
			{
				name = string.Join(' ', tokens[..^1]);
			}
			else
			{
				await _agentServerDispatcher.SendSystemMessage(gmId, "Usage: .kickban [<character name>] <minutes>");
				return;
			}

			if (minutes <= 0)
			{
				await _agentServerDispatcher.SendSystemMessage(gmId, "Minutes must be positive - use .ban for an indefinite ban.");
				return;
			}

			var character = await _database.GetCharacterByName(name, client.Session.Character.RealmId);
			var account = character == null ? null : await _database.GetAccount(character.AccountId);
			if (account == null)
			{
				await _agentServerDispatcher.SendSystemMessage(gmId, ChatMarkup.Color("red", $"No character named '{name}' on this realm."));
				return;
			}

			if (account.Id == client.Session.Account.Id)
			{
				await _agentServerDispatcher.SendSystemMessage(gmId, ChatMarkup.Color("red", "You can't kickban your own account."));
				return;
			}

			var expiresAt = DateTimeOffset.UtcNow.AddMinutes(minutes);
			if (!await _database.BanUntil(account.Id, expiresAt))
			{
				await _agentServerDispatcher.SendSystemMessage(gmId, ChatMarkup.Color("red", "Error changing account state."));
				return;
			}

			var kicked = 0;
			foreach (var c in _connections.Clients.Where(nc => nc.Session.Account?.Id == account.Id).ToList())
			{
				if (c.Session.Character?.Id is { } cid)
					await _agentServerDispatcher.SendSystemMessage(cid, ChatMarkup.Color("red", $"You have been kicked by a GM for {minutes} minute(s)."));
				c.Stop();
				kicked++;
			}

			await _agentServerDispatcher.SendSystemMessage(gmId,
				$"Kickbanned '{name}' (account '{account.UserName}') for {minutes} minute(s){(kicked > 0 ? $", {kicked} live session(s) dropped" : "")}."
				+ $" Lifts at {expiresAt:u} on their next login attempt; .unban ends it sooner.");
		}

		// /getfaction <faction id>  - "Get the faction points for the player with the specified faction id."
		//
		// The pair of these says something structural about factions: retail's getter takes a faction id and its setter
		// takes id + points, with no rank argument anywhere. Standing is a flat `faction id -> points` map and the rank
		// is derived from the point total, not stored - which is how FactionLadder works.
		private async Task GetFactionCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();
			if (!uint.TryParse(arg, out var factionId))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .getfaction <faction id>");
				return;
			}

			var standings = await _database.GetCharacterFactionStandings(characterId);
			var points = standings.TryGetValue(factionId, out var stored) ? stored : 0;
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Faction {factionId}: {points} points, rank {FactionLadder.RankFor(points)}{(standings.ContainsKey(factionId) ? "" : " (no standing row - reported as zero)")}.");
		}

		// /setfaction <faction id> <faction points>
		private async Task SetFactionCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var tokens = (arguments ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
			if (tokens.Length != 2 || !uint.TryParse(tokens[0], out var factionId) ||
			    !int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var points))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .setfaction <faction id> <faction points>");
				return;
			}

			await _database.SetCharacterFactionStanding(characterId, factionId, points);
			var rank = (uint)FactionLadder.RankFor(points);
			await _factions.SendAll(client);
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Faction {factionId} set to {points} points (rank {rank}).");
		}

		// /magic list | learn <spellid> | forget <spellid> | forgetall | ...  - "Spellbook/magic handling".
		//
		// Retail's full grammar also carries `npccast` and three token-state-counter verbs; that machinery does not
		// exist here, and those verbs say so rather than failing silently. `forget`/`forgetall` are backed: the
		// repository can shrink the spellbook, and the client is told with `DispatchForgetSpell` (0x200B), whose body
		// the client's own dispatch reader settles (see SpellBookChangeCodec).
		private async Task MagicCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var tokens = (arguments ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
			var verb = tokens.Length > 0 ? tokens[0].ToLowerInvariant() : "list";

			switch (verb)
			{
				case "list":
				{
					var known = await _database.GetCharacterSpells(characterId);
					await _agentServerDispatcher.SendSystemMessage(characterId, known.Length == 0
						? "No known spells."
						: $"{known.Length} known spell(s): {string.Join(", ", known.Select(k => k.SpellId).Order())}");
					return;
				}

				case "learn":
				{
					if (tokens.Length < 2 || !uint.TryParse(tokens[1], out var spellId))
					{
						await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .magic learn <spellid>");
						return;
					}

					var added = await _database.AddCharacterSpell(characterId, spellId);
					if (added)
						await GameWire.SendLearnSpell(client, spellId);

					await _agentServerDispatcher.SendSystemMessage(characterId, added
						? $"Learned spell {spellId}."
						: $"Spell {spellId} was already known.");
					return;
				}

				case "forget":
				{
					if (tokens.Length < 2 || !uint.TryParse(tokens[1], out var spellId))
					{
						await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .magic forget <spellid>");
						return;
					}

					var removed = await _database.RemoveCharacterSpell(characterId, spellId);
					if (removed)
						await GameWire.SendForgetSpell(client, spellId);

					await _agentServerDispatcher.SendSystemMessage(characterId, removed
						? $"Forgot spell {spellId}."
						: $"Spell {spellId} was not known.");
					return;
				}

				case "forgetall":
				{
					// Read the set before the delete: the client is told per spell, so the ids have to survive the wipe.
					var known = await _database.GetCharacterSpells(characterId);
					var removed = await _database.ClearCharacterSpells(characterId);
					foreach (var spell in known)
						await GameWire.SendForgetSpell(client, spell.SpellId);

					await _agentServerDispatcher.SendSystemMessage(characterId, removed == 0
						? "No known spells to forget."
						: $"Forgot {removed} spell(s).");
					return;
				}

				default:
					await _agentServerDispatcher.SendSystemMessage(characterId,
						$"Usage: .magic list | learn <spellid> | forget <spellid> | forgetall   (retail also has npccast and the token-state-counter verbs; those are not backed here)");
					return;
			}
		}

		// /inventory delete <slot> [bag name] | deleteall [bag name] | equipall | unequipall
		//
		// All four verbs are backed; the equip-all pair resolves its destination slots from the client's own
		// item-template fields (`EquipSlotResolver`).
		private async Task InventoryCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var tokens = (arguments ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
			var verb = tokens.Length > 0 ? tokens[0].ToLowerInvariant() : "";

			switch (verb)
			{
				case "delete":
				{
					if (tokens.Length < 2 || !int.TryParse(tokens[1], out var slot) || slot < 0)
					{
						await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .inventory delete <slot>");
						return;
					}

					var removed = await _economyOutbound.RemoveItemFromBackpack(client, slot);
					await _agentServerDispatcher.SendSystemMessage(characterId, removed
						? $"Deleted the item in bag slot {slot}."
						: $"No item in bag slot {slot}.");
					return;
				}

				case "deleteall":
				{
					// Walks the backpack rather than issuing a bulk delete, so every removal takes the same live
					// DispatchRemoveItem path a single one does and the client's bags stay in step.
					var deleted = 0;
					for (var slot = 0; slot < (int)EconomyOutbound.BaseBackpackSlots; slot++)
					{
						if (await _economyOutbound.RemoveItemFromBackpack(client, slot))
							deleted++;
					}

					await _agentServerDispatcher.SendSystemMessage(characterId, $"Deleted {deleted} item(s) from the backpack.");
					return;
				}

				case "equipall":
				{
					var moves = await _equipment.EquipAll(client);
					await AnnounceEquipmentMoves(client, moves);
					await _agentServerDispatcher.SendSystemMessage(characterId, moves.Count == 0
						? "Nothing in the backpack fits a free equipment slot."
						: $"Equipped {moves.Count} item(s): {string.Join(", ", moves.Select(m => $"{m.Name ?? m.TemplateId.ToString()} -> {(CharacterEquipmentSlot)m.ToSlot}"))}.");
					return;
				}

				case "unequipall":
				{
					var moves = await _equipment.UnequipAll(client, await _economyOutbound.BackpackCapacity(characterId));
					await AnnounceEquipmentMoves(client, moves);
					var stillWorn = (await _database.GetCharacterItems(characterId, ContainerKind.Equipped)).Length;
					await _agentServerDispatcher.SendSystemMessage(characterId, moves.Count == 0
						? stillWorn == 0 ? "Nothing is equipped." : "The backpack is full - nothing could be taken off."
						: $"Unequipped {moves.Count} item(s){(stillWorn > 0 ? $"; {stillWorn} left on (backpack full)" : "")}.");
					return;
				}

				default:
					await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .inventory delete <slot> | deleteall | equipall | unequipall");
					return;
			}
		}

		// Tells the client about a bulk equip/unequip: each moved item leaves its old cell (`DispatchRemoveItem`) and
		// arrives in the new one (`DispatchAddItem`). Both are retail messages we already send for a server-driven
		// inventory change; there is no single "the server moved your item" packet - a client-driven equip needs none
		// at all, because the client is the one that moved it.
		private async Task AnnounceEquipmentMoves(INetworkClient client, IReadOnlyList<EquipmentMove> moves)
		{
			var characterId = client.Session.Character.Id;
			foreach (var move in moves)
			{
				await EconomyOutbound.SendRemoveItem(client, move.From, (uint)move.FromSlot);

				var landed = await _database.GetCharacterItemSlot(characterId, move.To, move.ToSlot);
				if (landed == null)
					continue;

				await EconomyOutbound.SendAddItem(client, move.To, (uint)move.ToSlot,
					SnapshotItems.LiveAddEntry(landed, FullCharacterRoots.ItemId(landed.Id), FullCharacterRoots.RecordId(characterId, move.To, (ulong)move.ToSlot)));
			}
		}

		// /gethash <templateid>  - "Does a lookup in the CategoryTree to try and match the templateid and a HASH. It
		// prints the HASH if it's found."
		//
		// No CategoryTree walk is needed: the relationship retail looks up is the one AcgHash mints, so the answer is
		// computed rather than searched. The item's name comes along when the template is a known one, which is what
		// makes the output checkable instead of a bare number.
		private async Task GetHashCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();
			if (!uint.TryParse(arg, out var templateId))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .gethash <templateid>");
				return;
			}

			var minted = AcgHash.Mint(templateId);
			var item = await _database.GetItem(templateId);
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Template {templateId}{(item == null ? " (not in our catalogue)" : $" '{item.Name}'")} -> HASH {minted} (0x{minted:X8}).");
		}

		// /quest ...  - retail's quest debug, which addresses goals and tasks by hash:
		//   clearlog | solvegoal <hash> | failgoal <hash> | solvetask <hash> | failtask <hash> | nextphase <hash>
		//   | increasecount <hash> | clearallcooldowns
		//
		// Only `solvegoal` has a server path here. The rest name real machinery - failing a goal, solving a whole task,
		// advancing a phase, bumping a repeat count - that quest progression does not expose, and each says so by name
		// rather than the whole verb reporting itself absent, because the command is partly usable.
		private async Task QuestDebugCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var tokens = (arguments ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
			var verb = tokens.Length > 0 ? tokens[0].ToLowerInvariant() : "";

			switch (verb)
			{
				case "solvegoal":
				{
					if (tokens.Length < 2 || !uint.TryParse(tokens[1], out var goalId))
					{
						await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .quest solvegoal <goal hash>");
						return;
					}

					await _questProgression.SolveGoal(client, goalId);
					await _agentServerDispatcher.SendSystemMessage(characterId, $"Solved goal {goalId} (if it was in your current phase).");
					return;
				}

				case "failgoal":
				case "solvetask":
				case "failtask":
				case "nextphase":
				case "increasecount":
				case "clearlog":
				case "clearallcooldowns":
					await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Color("orange",
						$".quest {verb} is not backed: quest progression exposes solving a GOAL and nothing else - no fail path, no task-level or phase-level advance, no journal wipe (#559)."));
					return;

				default:
					await _agentServerDispatcher.SendSystemMessage(characterId,
						"Usage: .quest solvegoal <goal hash>   (retail also has clearlog/failgoal/solvetask/failtask/nextphase/increasecount/clearallcooldowns; only solvegoal is backed)");
					return;
			}
		}

		// /settimescale <value>  - "Set time scale on the current client and server", parameter help "Float: Time
		// scale (default is 1)".
		//
		// The distinction from /gametime matters, because both drive the same field: /gametime multiplies the current
		// rate (`* 2` then `* 2` gives 4), while /settimescale assigns it. Retail ships both, and running them through
		// one stored rate keeps them consistent with each other.
		private async Task SetTimeScaleCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();
			if (arg.Length > 0)
			{
				if (!float.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) || scale <= 0f)
				{
					await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .settimescale <value>   (a positive float; retail's default is 1)");
					return;
				}

				_clock.Rate = scale;
			}

			await client.SendAsync(GameWire.BuildGameTime(client, DateTimeOffset.UtcNow, _clock.OffsetGameSeconds, _clock.Rate));
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Time scale {_clock.Rate:0.###} - a game day now takes {GameClock.GameDayRealSeconds / 3600.0 / _clock.Rate:0.##} real hours.");
		}

		// /showclassstatadjustments  - "Get the list of npc class stat adjustments for the target npc." No parameters.
		//
		// What retail calls a class stat adjustment is imported here as the class/level attribute base - the
		// per-(class, level) table the client otherwise computes locally off its own feat templates - so the answer is
		// the target's ClassBase map.
		private async Task ShowClassStatAdjustmentsCommand(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			var subject = TryResolveTarget(client, out var target, out _) ? target : _simulation.PlayerWorldEntity(client);
			if (subject == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "You're not in the world yet.");
				return;
			}

			if (subject.ClassBase.Count == 0)
			{
				// An empty table is a real, informative answer here - it means no base is decoded for this
				// (class, level), which is the shortfall AttributeScore documents rather than a zero base.
				await _agentServerDispatcher.SendSystemMessage(characterId,
					$"'{subject.Name}' ({subject.CharacterClass}, level {subject.Level}) has NO decoded class stat adjustments - attribute-driven formulas fall back to gear + buffs and read low.");
				return;
			}

			var lines = new List<string> { $"'{subject.Name}' ({subject.CharacterClass}, level {subject.Level}) class stat adjustments - {subject.ClassBase.Count} entries:" };
			lines.AddRange(subject.ClassBase
				.OrderBy(kv => (uint)kv.Key)
				.Take(24)
				.Select(kv => $"  {kv.Key} ({(uint)kv.Key}): {kv.Value} raw = {kv.Value / (int)kv.Key.GetScale()} at scale {kv.Key.GetScale()}"));
			if (subject.ClassBase.Count > 24)
				lines.Add($"  ... and {subject.ClassBase.Count - 24} more");

			await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Lines([.. lines]));
		}

		// /update  - "Updates the Attackrating for your target." No parameters.
		//
		// This recomputes the caller's own derived stats, not an arbitrary target's: the reseed runs off a connected
		// client's gear and buffs, and an NPC has no client to reseed from. Said in the reply rather than silently
		// acting on the wrong entity.
		private async Task UpdateRatingCommand(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			if (_simulation.PlayerWorldEntity(client) == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "You're not in the world yet.");
				return;
			}

			var vitals = await _combat.ReseedFromMaxStats(client);
			await GameWire.SendStatUpdate(client, vitals.ToVitalsBlock(), characterId);
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Recomputed your derived stats from gear and buffs: health {vitals.Health}/{vitals.MaxHealth}, mana {vitals.Mana}/{vitals.MaxMana}, stamina {vitals.Stamina}/{vitals.MaxStamina}."
				+ " (Retail aims this at your target; ours reseeds the caller, since the reseed runs off a connected client.)");
		}
	}
}
