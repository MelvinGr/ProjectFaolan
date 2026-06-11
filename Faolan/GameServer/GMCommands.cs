using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets;
using Faolan.GameServer.Packets._0x737A6DF9_Test;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	partial class GameServerListener
	{
		private static Dictionary<string, GmCommandDelegate> _gmCommandsDict;

		// .hurt [amount] / .heal [amount]  - apply a health delta to the player's own live vitals and push the
		// 0x96B8DC59 update so the health bar moves (combat-loop test without needing a target). Default 100.
		private Task AdjustOwnHealthCommand(INetworkClient client, string arguments, bool heal)
		{
			var amount = int.TryParse(arguments, out var parsed)
				? parsed
				: 100;
			var vitals = GetOrAddPlayerVitals(client);
			vitals.ApplyHealthDelta(heal
				? amount
				: -amount);

			return SendStatUpdate(client, vitals.ToVitalsBlock());
		}

		private Task ApplySpellTest(uint characterId, uint id)
		{
			var client = ClientForCharacter(characterId);
			return ApplySpell(client, client.Session.Character.Id, client.Session.Character.Id, id);
		}

		// .teleport <mapId>  - move the character to a map and rebuild the world there.
		//
		// Re-runs the proven world-entry render path for the target map - the same world-burst replay (or
		// hand-built fallback) that login uses, which is the only sequence confirmed to build the playfield and
		// render NPCs. NOTE: a retail teleport actually hands the client off to a (possibly different) world
		// server - the old server sends teleport-prep packets (0x10D27BC0 / 0x5A32F0D7 / vitals) and a 0x2002
		// world-address advert, then the client reconnects and does a full world entry (capture
		// account-faolan-...-changemap, idx 6091-6103, ports 7027 -> 7030). Our single-host server can't do that
		// handoff, so we soft-change the map on the current connection instead.
		private async Task DoTeleport(uint characterId, uint mapId)
		{
			var client = ClientForCharacter(characterId);
			if (client?.Session.Character == null)
				return;

			var map = await Database.GetMap(mapId);
			if (map == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, $"<span style=\"color: red;\">Unknown map id: {mapId}</span>");
				return;
			}

			// Move the character to the map's spawn point (importer-added maps carry the real captured spawn;
			// older seeded maps carry a placeholder) and persist so a relog lands on the new map.
			client.Session.Character.MapId = map.Id;
			client.Session.Character.Position = map.Position;
			client.Session.Character.Rotation = map.Rotation;
			await Database.SaveCharacter(client.Session.Character);

			await SendWorldEntry(client);

			var mapLabel = string.IsNullOrEmpty(map.Name)
				? map.Id.ToString()
				: $"{map.Name} ({map.Id})";
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Teleported to {mapLabel}.");
		}

		// .grantspell <id>  - grant an ability from the catalog to the player via the 0x2052 ability-grant (the only
		// proven grant packet - real spell grants aren't decoded, and the Spell table has no grant identity quad).
		// Makes the ability appear so it can be dragged to the hotbar and cast, which is how the spell/mana drain is
		// tested. The grant is transient (not persisted) - it is re-sent only by login's GrantStartingAbilities.
		private async Task GrantSpellCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			if (!uint.TryParse((arguments ?? "").Trim(), out var id))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .grantspell &lt;abilityId&gt;  (see .spelllist)");
				return;
			}

			var ability = await Database.GetAbility(id);
			if (ability == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, $"<span style=\"color: red;\">No grantable ability with id {id} (see .spelllist).</span>");
				return;
			}

			await SendAbilityGrant(client, ability);
			var label = string.IsNullOrEmpty(ability.Name)
				? id.ToString()
				: ability.Name;
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Granted {label} (id {id}, slot {ability.Slot}). Drag it to your hotbar to use it.");
		}

		private async Task HandleGmCommand(INetworkClient client, string commandText)
		{
			string command;
			string arguments = null;

			if (!commandText.Contains(' '))
				command = commandText[1..];
			else
			{
				var spaceIndex = commandText.IndexOf(' ');
				command = commandText[1..spaceIndex];
				arguments = commandText[(spaceIndex + 1)..];
			}

			if (_gmCommandsDict.ContainsKey(command))
			{
				try
				{
					await _gmCommandsDict[command]
						.Invoke(client, arguments);
				}
				catch (Exception e)
				{
					await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, $"<span style=\"color: red;\">{e.Message}</span>");
				}
			}
			else
				await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, $"Unknown command: '{command}' type .help for info");
		}

		private void InitGmCommands()
		{
			_gmCommandsDict = new Dictionary<string, GmCommandDelegate>(StringComparer.OrdinalIgnoreCase)
			{
				["save"] = (client, _) => SaveChanges(client.Session.Character.Id),
				["help"] = (client, _) => SendGmHelpText(client.Session.Character.Id),
				["maps"] = (client, _) => SendListAllMaps(client.Session.Character.Id),
				["spells"] = (client, _) => SendListAllSpells(client.Session.Character.Id),
				["spelllist"] = (client, _) => SendGrantableAbilityList(client.Session.Character.Id),
				["grantspell"] = (client, arguments) => GrantSpellCommand(client, arguments),
				["teleport"] = (client, arguments) => DoTeleport(client.Session.Character.Id, uint.Parse(arguments)),
				["spelltest"] = (client, arguments) => ApplySpellTest(client.Session.Character.Id, uint.Parse(arguments)),
				["vitals"] = (client, arguments) => SetVitalsCommand(client, arguments),
				["setstat"] = (client, arguments) => SetStatCommand(client, arguments),
				["hurt"] = (client, arguments) => AdjustOwnHealthCommand(client, arguments, false),
				["heal"] = (client, arguments) => AdjustOwnHealthCommand(client, arguments, true),
				["mount"] = (client, arguments) => MountCommand(client, arguments),
				["world"] = (client, _) => SendWorldInfo(client),
				["quest"] = (client, arguments) => QuestCommand(client, arguments),
				["tag"] = (client, arguments) => TagCommand(client, arguments),
				["lua"] = (client, arguments) => LuaCommand(client, arguments)
			};
		}

		// .quest add|remove <id> | .quest list  - manage the player's quest journal. `add` records the quest +
		// sends QuestIIR so it appears (the client renders the quest text from its RDB by id); `remove` drops it;
		// `list` shows the player's active quests. Quest IDs are RDB quest ids (e.g. 13479). The full accept/branch
		// logic is the pinned RDB Lua quest VM - this is the state foundation (docs/quests-and-combat.md).
		private async Task QuestCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var parts = (arguments ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "list";

			if (sub == "list")
			{
				var quests = await Database.GetCharacterQuests(characterId);
				var active = quests.Where(q => q.State == Core.Database.Tables.QuestState.Active).Select(q => q.QuestId).ToList();
				await _agentServerDispatcher.SendSystemMessage(characterId, active.Count == 0
					? "No active quests."
					: $"Active quests: {string.Join(", ", active)}");
				return;
			}

			if (parts.Length < 2 || !uint.TryParse(parts[1], out var questId))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .quest add|remove <questId> | .quest list");
				return;
			}

			switch (sub)
			{
				case "add":
				{
					// .quest add <id> [count] [target name...] - optional kill objective: kill <count> of <target>.
					var required = parts.Length > 2 && uint.TryParse(parts[2], out var c) ? c : 0;
					var target = parts.Length > 3 ? string.Join(' ', parts.Skip(3)) : null;
					await Database.SetCharacterQuest(characterId, questId, Core.Database.Tables.QuestState.Active, 0, required, target);
					await _broadcaster.SendQuestIir(client, questId, World.WorldQuest.StateActive);
					if (required > 0)
						await _broadcaster.SendQuestGoal(client, questId, 0, 0);
					await _agentServerDispatcher.SendSystemMessage(characterId, target != null
						? $"Quest {questId} added: kill {required} {target}."
						: $"Quest {questId} added to journal.");
					break;
				}
				case "remove":
					await Database.RemoveCharacterQuest(characterId, questId);
					await _agentServerDispatcher.SendSystemMessage(characterId, $"Quest {questId} removed.");
					break;
				default:
					await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .quest add|remove <questId> | .quest list");
					break;
			}
		}

		// .tag add <id> | .tag list  - manage the player's quest-state tags (the flags RDB Lua scripts read/write).
		private async Task TagCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var parts = (arguments ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length >= 2 && parts[0].Equals("add", StringComparison.OrdinalIgnoreCase) && uint.TryParse(parts[1], out var tagId))
			{
				await Database.AddCharacterTag(characterId, tagId);
				await _agentServerDispatcher.SendSystemMessage(characterId, $"Tag {tagId} set.");
				return;
			}

			var tags = await Database.GetCharacterTags(characterId);
			await _agentServerDispatcher.SendSystemMessage(characterId, tags.Length == 0
				? "No tags."
				: $"Tags: {string.Join(", ", tags)}");
		}

		// .lua run <scriptId>  - run an RDB LuaArchive script against the player (the conversation/quest VM engine):
		// reads the player's tags/quests, runs the script, persists any tags it adds, and reports its effects. Proves
		// the Lua VM end-to-end on a real character (see protocol_conversation_routing).
		private async Task LuaCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var parts = (arguments ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length < 2 || !parts[0].Equals("run", StringComparison.OrdinalIgnoreCase) || !uint.TryParse(parts[1], out var scriptId))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .lua run <scriptId>");
				return;
			}

			var script = await Database.GetLuaScript(scriptId);
			if (script?.Source == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, $"<span style=\"color: red;\">No Lua script {scriptId} (run --rdb-lua to import).</span>");
				return;
			}

			var tags = await Database.GetCharacterTags(characterId);
			var completed = (await Database.GetCharacterQuests(characterId))
				.Where(q => q.State == Core.Database.Tables.QuestState.Completed)
				.Select(q => q.QuestId);
			var player = new Scripting.GameLuaPlayer(tags, completed);

			var ok = _luaVm.RunEffect(script.Source, player);
			foreach (var t in player.AddedTags)
				await Database.AddCharacterTag(characterId, (uint)t);

			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Lua {scriptId}: ran={ok}, +tags [{string.Join(",", player.AddedTags)}], spawned {player.SpawnedItems.Count}, solvedGoals [{string.Join(",", player.SolvedGoals)}], cast [{string.Join(",", player.CastSpells)}].");
		}

		// .mount [spellIdHex]  - cast the mount-summon spell on the player. Default 0x3BAD7D, the mount spell
		// observed in demol80's "very_long_loading" capture: a channeled ApplySpell 0x6B87E316 (spell 0x3BAD7D,
		// repeated ~11x with 0xF508F4C1) immediately precedes the "Fading Black Horse" entity registering
		// (0x96C46740) and the player switching to MountedRun (MovingType 0x010A). This broadcasts the cast; full
		// mounting: broadcasts the summon cast AND spawns the mount entity at the player (it appears). The player
		// actually RIDING (the MountedRun movement transition) is the client's part and in-client-gated.
		private async Task MountCommand(INetworkClient client, string arguments)
		{
			var spellId = string.IsNullOrWhiteSpace(arguments)
				? 0x3BAD7Du
				: Convert.ToUInt32(arguments
					.Trim()
					.Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16);

			var self = client.Session.Account.ClientInstance;
			await ApplySpell(client, self, self, spellId); // the channeled summon cast (broadcast)
			await _broadcaster.SpawnMountForPlayer(client, "Fading Black Horse"); // the decoded mount for the default summon 0x3BAD7D
		}

		// .save  - flush the live world state to the DB. The WorldEntity is the source of truth for live transform
		// (docs/server-architecture-roadmap.md) and movement is not otherwise persisted, so this snapshots every
		// connected player's authoritative position/map back onto their persisted Character and saves it. (Extends to
		// more live state - vitals, buffs, … - as those become persisted.)
		private async Task SaveChanges(uint characterId)
		{
			var saved = await FlushWorldState();
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Flushed {saved} player(s)' live world state to the database.");
		}

		// Writes each connected player's live WorldEntity transform onto their Character and persists it. Returns the
		// number of players saved.
		private async Task<int> FlushWorldState()
		{
			var saved = 0;
			foreach (var pair in _simulation.Players)
			{
				if (await SavePlayerState(pair.Key, pair.Value))
					saved++;
			}

			return saved;
		}

		// Flushes one player's authoritative live transform (the WorldEntity) onto their persisted Character and
		// saves it. Used by the autosave loop, .save, and disconnect. Errors are logged (callers may not await).
		private async Task<bool> SavePlayerState(INetworkClient client, World.WorldEntity entity)
		{
			var character = client.Session?.Character;
			if (character == null)
				return false;

			try
			{
				character.MapId = entity.MapId;
				character.Position = entity.Position;
				await Database.SaveCharacter(character);
				return true;
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex, "Failed to flush world state for {Name} (0x{Handle:X8}).", entity.Name, entity.Handle);
				return false;
			}
		}

		// .world  - report the live world model around the caller: their map, the players/NPCs in that zone, and how
		// many entities are within interest range. A window into the shared simulation (handy for the 2-player test).
		private Task SendWorldInfo(INetworkClient client)
		{
			var entity = PlayerWorldEntity(client);
			if (entity == null)
				return _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, "World: you are not registered in the world model yet.");

			var zone = _simulation.World.ZoneFor(entity.MapId);
			var players = zone.Entities.Count(e => e.IsPlayer);
			var npcs = zone.Entities.Count(e => !e.IsPlayer);
			var nearby = zone.EntitiesInRange(entity.Position, World.WorldSimulation.PlayerInterestRange, entity.Handle).Count();
			return _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id,
				$"World map {entity.MapId}: {players} player(s), {npcs} NPC(s) in zone; {nearby} entity(ies) within {World.WorldSimulation.PlayerInterestRange:0} of you.");
		}

		private Task SendGmHelpText(uint characterId)
		{
			return _agentServerDispatcher.SendSystemMessage(characterId, $"Available Commands:<br />{_gmCommandsDict.Keys.JoinAsString("<br />")}");
		}

		// .spelllist  - list the grantable ability catalog (id / name / slot) for use with .grantspell. These are
		// what can actually be handed out via the proven 0x2052 grant; the Spell table (.spells) has no grant
		// identity quad, so its rows can be cast but not granted. A trailing * marks the universal starting kit.
		private async Task SendGrantableAbilityList(uint characterId)
		{
			var abilities = await Database.GetAbilities();
			if (abilities.Length == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "No abilities in the catalog.");
				return;
			}

			var lines = abilities
				.Select(a => $"{a.Id}: {(string.IsNullOrEmpty(a.Name) ? "(unnamed)" : a.Name)} [slot {a.Slot}]{(a.IsStartingAbility ? " *" : "")}")
				.JoinAsString("<br />");
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Grantable abilities (.grantspell &lt;id&gt;):<br />{lines}");
		}

		private async Task SendListAllMaps(uint characterId)
		{
			var maps = await Database.GetMaps();
			await _agentServerDispatcher.SendSystemMessage(characterId, maps
				.Select(m => $"{m.Id}: {m.Name}")
				.JoinAsString("<br />"));
		}

		private async Task SendListAllSpells(uint characterId)
		{
			var spells = await Database.GetSpells();
			await _agentServerDispatcher.SendSystemMessage(characterId, spells
				.Select(s => $"{s.Id}: {s.Name}")
				.JoinAsString("<br />"));
		}

		// .setstat <statIdHex> <displayValue>  - push a single 0x96B8DC59 stat record (e.g. ".setstat 36 25" sets
		// Level to 25). The stat's wire scale is applied automatically.
		private Task SetStatCommand(INetworkClient client, string arguments)
		{
			var parts = (arguments ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
			var statId = (LegacyCharacterStatId)Convert.ToUInt32(parts[0], 16);
			var value = uint.Parse(parts[1]);

			return SendStatUpdate(client, new CharacterStatBlock().Set(statId, value));
		}

		// .vitals [health] [stamina] [mana]  - push a 0x96B8DC59 vitals update. With no args, sends the character's
		// own MaxStats (or class/level defaults). With args (player-facing values; the ×100 scale is applied),
		// missing stamina/mana default to the health value.
		private Task SetVitalsCommand(INetworkClient client, string arguments)
		{
			var parts = (arguments ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length == 0)
				return SendVitals(client);

			var health = uint.Parse(parts[0]);
			var stamina = parts.Length > 1
				? uint.Parse(parts[1])
				: health;
			var mana = parts.Length > 2
				? uint.Parse(parts[2])
				: health;

			return SendVitals(client, health, stamina, mana);
		}

		private delegate Task GmCommandDelegate(INetworkClient client, string arguments);
	}
}
