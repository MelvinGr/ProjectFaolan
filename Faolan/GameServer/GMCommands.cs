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
				["lua"] = (client, arguments) => LuaCommand(client, arguments),
				["test"] = (client, _) => Test(client.Session.Character.Id)
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
					await SendQuestIir(client, questId, World.WorldQuest.StateActive);
					if (required > 0)
						await SendQuestGoal(client, questId, 0, 0);
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
			await SpawnMountForPlayer(client, "Fading Black Horse"); // the decoded mount for the default summon 0x3BAD7D
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
			foreach (var pair in _playerEntities)
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

			var zone = _world.ZoneFor(entity.MapId);
			var players = zone.Entities.Count(e => e.IsPlayer);
			var npcs = zone.Entities.Count(e => !e.IsPlayer);
			var nearby = zone.EntitiesInRange(entity.Position, PlayerInterestRange, entity.Handle).Count();
			return _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id,
				$"World map {entity.MapId}: {players} player(s), {npcs} NPC(s) in zone; {nearby} entity(ies) within {PlayerInterestRange:0} of you.");
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

		private async Task Test(uint characterId)
		{
			var client = ClientForCharacter(characterId);

			//

			return;

			//

			// Spawn Kalanthes
			await new PacketStream()
				.WriteHeader(LoginWorldSender, LoginWorldReceiver(client), 0x2000)
				.WriteArray(0x00, 0x00, 0x03, 0x92, 0xFA, 0x01, 0x58, 0x07, 0x00, 0x00, 0xC3, 0x50, 0x00, 0x04, 0xDE, 0x8A, 0x00, 0x00, 0x00, 0x00, 0x2C, 0x0A, 0x0F, 0x0D, 0x9A, 0x99, 0xA6, 0x43, 0x15, 0xF2, 0x4B, 0x12, 0x43,
					0x1D, 0xAA, 0x81, 0x30, 0x44, 0x12, 0x14, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x15, 0x3A, 0x42, 0x26, 0x3F, 0x1D, 0x00, 0x00, 0x00, 0x00, 0x25, 0xF7, 0xA9, 0x42, 0xBF, 0x18, 0xCD, 0xEC, 0xBF, 0x02, 0x05, 0x00, 0x00,
					0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x65, 0x00, 0x04, 0xDA, 0xE1, 0x11, 0x34, 0x10, 0x15, 0x00, 0x02, 0x68, 0x5F,
					0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x54, 0x50, 0x39, 0x56, 0x00, 0x00, 0x00, 0x00, 0x02, 0x18, 0x00,
					0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x62, 0x7B, 0xFB, 0x50, 0x00, 0x00, 0x57, 0x4D, 0x1B, 0xE0, 0x00, 0x00, 0x00, 0x00, 0x42, 0x62, 0xD3, 0xD0, 0x00, 0x00, 0x00, 0x00, 0x00,
					0x00, 0x00, 0x03, 0x00, 0x00, 0x03, 0xF1, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x20, 0xD8, 0x2B, 0x63, 0x00, 0x02, 0x68, 0x5F, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
					0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01, 0xC4, 0x8A, 0xD8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
					0x00, 0x00, 0x00, 0x00, 0x64, 0x00, 0x00, 0x00, 0x00, 0x01, 0x57, 0xA5, 0x90, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x62, 0x7B, 0xFB, 0x20, 0x00, 0x00,
					0x00, 0x00, 0x62, 0x7B, 0xFB, 0x20, 0x00, 0x00, 0x00, 0x00, 0x41, 0x08, 0x00, 0x82, 0x03, 0x00, 0x00, 0x02, 0x44, 0x0A, 0x09, 0x4B, 0x61, 0x6C, 0x61, 0x6E, 0x74, 0x68, 0x65, 0x73, 0x22, 0x53, 0x08, 0x02, 0x10,
					0x00, 0x18, 0x0B, 0x20, 0x69, 0x28, 0xD8, 0xA1, 0x0A, 0x30, 0xD4, 0x8D, 0x1C, 0x38, 0xD4, 0x8D, 0x1C, 0x40, 0xD4, 0x8D, 0x1C, 0x48, 0xD4, 0x8D, 0x1C, 0x50, 0xD4, 0x8D, 0x1C, 0x58, 0xD4, 0x8D, 0x1C, 0x60, 0x00,
					0x70, 0x64, 0x78, 0x00, 0x80, 0x01, 0x81, 0x80, 0x80, 0x80, 0x01, 0x88, 0x01, 0x8F, 0xE7, 0x09, 0x90, 0x01, 0x50, 0x98, 0x01, 0x50, 0xA0, 0x01, 0x64, 0xA8, 0x01, 0xC8, 0x61, 0xB0, 0x01, 0x01, 0xE8, 0x01, 0x01,
					0x90, 0x02, 0x01, 0xA0, 0x02, 0x01, 0xA8, 0x02, 0x84, 0x03, 0x2A, 0x0B, 0x25, 0x90, 0x0A, 0x00, 0x00, 0x28, 0x01, 0x48, 0x01, 0x70, 0x04, 0x3A, 0x0D, 0x08, 0xA8, 0xC8, 0x02, 0x15, 0x00, 0x00, 0x00, 0x00, 0x18,
					0x01, 0x50, 0x00, 0xA0, 0x01, 0x01, 0xA8, 0x01, 0x02, 0xBA, 0x01, 0x57, 0x08, 0x05, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0xE1, 0x13, 0x34, 0x20, 0x15, 0xBF, 0x90, 0x7F, 0x0C, 0x1D, 0x54, 0x6F, 0x6A, 0x4F, 0x25, 0x4C,
					0x7E, 0xF9, 0xCE, 0x12, 0x14, 0x0D, 0xE1, 0x13, 0x34, 0x20, 0x15, 0xBF, 0x90, 0x7F, 0x0C, 0x1D, 0x54, 0x6F, 0x6A, 0x4F, 0x25, 0x4C, 0x7E, 0xF9, 0xCE, 0x1A, 0x14, 0x0D, 0xE1, 0x13, 0x34, 0x20, 0x15, 0xBF, 0x90,
					0x7F, 0x0C, 0x1D, 0x54, 0x6F, 0x6A, 0x4F, 0x25, 0x4C, 0x7E, 0xF9, 0xCE, 0x25, 0xD1, 0xE5, 0x02, 0x00, 0x2D, 0xD1, 0xE5, 0x02, 0x00, 0x38, 0x01, 0x45, 0x53, 0x50, 0x34, 0x56, 0xBA, 0x01, 0x57, 0x08, 0x06, 0x12,
					0x53, 0x0A, 0x14, 0x0D, 0xC3, 0x74, 0x31, 0x7C, 0x15, 0x0E, 0x4E, 0x2F, 0x1E, 0x1D, 0xB1, 0x12, 0x58, 0xAC, 0x25, 0x4E, 0xB6, 0xE5, 0x7C, 0x12, 0x14, 0x0D, 0xC3, 0x74, 0x31, 0x7C, 0x15, 0x0E, 0x4E, 0x2F, 0x1E,
					0x1D, 0xB1, 0x12, 0x58, 0xAC, 0x25, 0x4E, 0xB6, 0xE5, 0x7C, 0x1A, 0x14, 0x0D, 0xC3, 0x74, 0x31, 0x7C, 0x15, 0x0E, 0x4E, 0x2F, 0x1E, 0x1D, 0xB1, 0x12, 0x58, 0xAC, 0x25, 0x4E, 0xB6, 0xE5, 0x7C, 0x25, 0xFD, 0xFA,
					0x02, 0x00, 0x2D, 0xFD, 0xFA, 0x02, 0x00, 0x38, 0x05, 0x45, 0x47, 0x4B, 0x34, 0x59, 0xBA, 0x01, 0x57, 0x08, 0x0C, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0xFB, 0xC5, 0x3F, 0xFC, 0x15, 0x58, 0xF1, 0xEC, 0xCE, 0x1D, 0x48,
					0x5D, 0xDF, 0x2A, 0x25, 0xBF, 0x6C, 0x77, 0xED, 0x12, 0x14, 0x0D, 0xFB, 0xC5, 0x3F, 0xFC, 0x15, 0x58, 0xF1, 0xEC, 0xCE, 0x1D, 0x48, 0x5D, 0xDF, 0x2A, 0x25, 0xBF, 0x6C, 0x77, 0xED, 0x1A, 0x14, 0x0D, 0xFB, 0xC5,
					0x3F, 0xFC, 0x15, 0x58, 0xF1, 0xEC, 0xCE, 0x1D, 0x48, 0x5D, 0xDF, 0x2A, 0x25, 0xBF, 0x6C, 0x77, 0xED, 0x25, 0xCB, 0xE7, 0x02, 0x00, 0x2D, 0xCB, 0xE7, 0x02, 0x00, 0x38, 0x01, 0x45, 0x59, 0x4C, 0x4A, 0x35, 0xBA,
					0x01, 0x57, 0x08, 0x10, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0x86, 0x92, 0x1B, 0xA5, 0x15, 0xD1, 0xB1, 0xE7, 0xC2, 0x1D, 0xE3, 0x22, 0x7B, 0xCA, 0x25, 0x8F, 0x3F, 0x24, 0x03, 0x12, 0x14, 0x0D, 0x86, 0x92, 0x1B, 0xA5,
					0x15, 0xD1, 0xB1, 0xE7, 0xC2, 0x1D, 0xE3, 0x22, 0x7B, 0xCA, 0x25, 0x8F, 0x3F, 0x24, 0x03, 0x1A, 0x14, 0x0D, 0x86, 0x92, 0x1B, 0xA5, 0x15, 0xD1, 0xB1, 0xE7, 0xC2, 0x1D, 0xE3, 0x22, 0x7B, 0xCA, 0x25, 0x8F, 0x3F,
					0x24, 0x03, 0x25, 0xCD, 0xE5, 0x02, 0x00, 0x2D, 0xCD, 0xE5, 0x02, 0x00, 0x38, 0x01, 0x45, 0x52, 0x58, 0x32, 0x48, 0xBA, 0x01, 0x57, 0x08, 0x12, 0x12, 0x53, 0x0A, 0x14, 0x0D, 0xA7, 0x2E, 0x2B, 0x0D, 0x15, 0x37,
					0xEF, 0xC5, 0xED, 0x1D, 0x2C, 0x80, 0xE8, 0x3F, 0x25, 0x56, 0x65, 0x12, 0x01, 0x12, 0x14, 0x0D, 0xA7, 0x2E, 0x2B, 0x0D, 0x15, 0x37, 0xEF, 0xC5, 0xED, 0x1D, 0x2C, 0x80, 0xE8, 0x3F, 0x25, 0x56, 0x65, 0x12, 0x01,
					0x1A, 0x14, 0x0D, 0xA7, 0x2E, 0x2B, 0x0D, 0x15, 0x37, 0xEF, 0xC5, 0xED, 0x1D, 0x2C, 0x80, 0xE8, 0x3F, 0x25, 0x56, 0x65, 0x12, 0x01, 0x25, 0xCA, 0xE7, 0x02, 0x00, 0x2D, 0xCA, 0xE7, 0x02, 0x00, 0x38, 0x01, 0x45,
					0x49, 0x47, 0x33, 0x45, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x03, 0xF1, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xF1, 0x00, 0x00, 0x03, 0xF1, 0x00, 0x00,
					0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0xF1)
				.SendAsync(client);
		}

		private delegate Task GmCommandDelegate(INetworkClient client, string arguments);
	}
}
