using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Conan;
using Faolan.GameServer.Combat;
using Faolan.GameServer.Scripting;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// .additem <itemId> [count]  - add an item (by faolan.db Items id) to the calling character's bag: persist
		// a CharacterInventory row in the first free bag slot, then re-send the rebuilt 0x737A6DF9 inventory
		// snapshot so it appears live (the snapshot is the ONLY item-delivery packet - there is no incremental
		// server add-item; see protocol_containeradditem). Without a stashed snapshot template (pre-world or a
		// non-replay session) the item still persists and renders on next login.
		private async Task AddItemCommand(INetworkClient client, string arguments)
		{
			var character = client.Session.Character;
			var characterId = character.Id;
			var parts = (arguments ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			uint count = 1;
			if (parts.Length is < 1 or > 2 || !uint.TryParse(parts[0], out var itemId) || (parts.Length == 2 && !uint.TryParse(parts[1], out count)))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .additem &lt;itemId&gt; [count]");
				return;
			}

			var item = await Database.GetItem(itemId);
			if (item is not { Hash: not 0 })
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, item == null
					? $"No item with id {itemId}."
					: $"Item {itemId} ('{item.Name}') has no hash - not snapshot-buildable.");
				return;
			}

			// Persist + deliver live via DispatchAddItem (0x2020) - it appears without a relog now that the live
			// add path exists (the old "no live add packet exists" caveat is gone).
			var slot = await GrantItemToBackpack(client, itemId, count);
			if (slot == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Bag is full (24 cells).");
				return;
			}

			await _agentServerDispatcher.SendSystemMessage(characterId, $"Added {count}x '{item.Name}' (id {itemId}) to bag slot {slot} (live DispatchAddItem).");
		}

		// .ban &lt;account|character name&gt;   - ban an account (sets State = Banned; login rejects non-Active accounts).
		// .unban &lt;account|character name&gt; - lift a ban (State = Active). Accepts either the account username OR one of
		// its character names. Banning also kicks the account's live world connections (the State change alone only
		// blocks the NEXT login). You can't ban your own account.
		private async Task BanCommand(INetworkClient client, string arguments, bool ban)
		{
			var gmId = client.Session.Character.Id;
			var name = (arguments ?? "").Trim();
			if (string.IsNullOrEmpty(name))
			{
				await _agentServerDispatcher.SendSystemMessage(gmId, $"Usage: .{(ban ? "ban" : "unban")} &lt;account or character name&gt;");
				return;
			}

			// Resolve the target account: by username first, else by a character name -> its owning account.
			var account = await Database.GetAccount(name);
			if (account == null && await Database.GetCharacterByName(name) is { } ch)
				account = await Database.GetAccount(ch.AccountId);

			if (account == null)
			{
				await _agentServerDispatcher.SendSystemMessage(gmId, $"<span style=\"color: red;\">No account or character named '{name}'.</span>");
				return;
			}

			if (ban && account.Id == client.Session.Account?.Id)
			{
				await _agentServerDispatcher.SendSystemMessage(gmId, "<span style=\"color: red;\">You can't ban your own account.</span>");
				return;
			}

			if (!await Database.SetAccountState(account.Id, ban
				    ? AccountState.Banned
				    : AccountState.Active))
			{
				await _agentServerDispatcher.SendSystemMessage(gmId, $"<span style=\"color: red;\">Account '{account.UserName}' not found.</span>");
				return;
			}

			var kicked = 0;
			if (ban)
			{
				// Snapshot before iterating: Stop() -> Disconnected mutates the client set.
				foreach (var c in NetworkClients.Where(nc => nc.Session?.Account?.Id == account.Id).ToList())
				{
					if (c.Session?.Character?.Id is { } cid)
						await _agentServerDispatcher.SendSystemMessage(cid, "<span style=\"color: red;\">You have been banned by a GM.</span>");
					c.Stop();
					kicked++;
				}
			}

			await _agentServerDispatcher.SendSystemMessage(gmId, ban
				? $"Banned account '{account.UserName}'{(kicked > 0 ? $" and kicked {kicked} live session(s)" : "")}."
				: $"Unbanned account '{account.UserName}'.");
		}

		// .delete [<handle>]  - PERMANENTLY remove the player's currently SELECTED target (or the given handle): despawn
		// it live for nearby players AND delete its NpcSpawn row(s) so it never renders again. The tool for scrubbing
		// captured import artifacts (phantom players that happened to be in the pcap when the world was captured). A
		// faction mob (hostile) deletes only the rows at the target's spot (don't wipe every same-name mob); a neutral
		// named entity deletes every same-name row on the map (its cross-session captures collapse to one render).
		// Unlike .despawnnpc (live-only, comes back on relog), .delete is durable.
		private async Task DeleteCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();
			var handle = arg.Length == 0
				? client.Session.SelectedTargetHandle
				: TryParseHandle(arg, out var parsed)
					? parsed
					: 0;
			if (handle == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .delete [&lt;handle&gt;]  (no arg = your selected target). Removes it live AND deletes its spawn row.");
				return;
			}

			var entity = await _broadcaster.DespawnEntityForPlayer(client, handle);
			if (entity == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, $"No deletable entity 0x{handle:X8} in this zone (target an NPC first; players can't be deleted).");
				return;
			}

			var deleted = await Database.DeleteNpcSpawns(entity.MapId, entity.Name, entity.Hostile
				? entity.SpawnPosition.X
				: null, entity.Hostile
				? entity.SpawnPosition.Y
				: null, entity.Hostile
				? entity.SpawnPosition.Z
				: null, 10f);
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Deleted {entity.Name} (0x{handle:X8}) - removed live + {deleted} spawn row(s) (gone on relog too).");
		}

		// toPosition: an explicit destination within the map (e.g. a bind/recall point); null = the map's spawn point.
		private async Task DoTeleport(uint characterId, uint mapId, Vector3? toPosition = null)
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

			// Destination: an explicit point (bind/recall/coords) or the map's default spawn (the AlwaysKnown starter, 			// else any respawn point - GetDefaultSpawn). A map with no points defined can't resolve a spawn, so refuse
			// rather than dump the player at the origin. Persist BEFORE the handoff so the reconnect's world entry
			// (existing-character Hello -> SendWorldEntry, which loads Character.MapId) lands at the right spot.
			Vector3 position;
			Quaternion rotation;
			if (toPosition.HasValue)
			{
				position = toPosition.Value;
				rotation = Quaternion.Identity;
			}
			else
			{
				var spawn = await Database.GetDefaultSpawn(map.Id);
				if (spawn == null)
				{
					await _agentServerDispatcher.SendSystemMessage(characterId,
						$"<span style=\"color: red;\">No spawn point defined for map {(string.IsNullOrEmpty(map.Name) ? map.Id.ToString() : $"{map.Name} ({map.Id})")}. Give explicit coordinates: .teleport &lt;x&gt; &lt;y&gt; &lt;z&gt;.</span>");
					return;
				}

				position = spawn.Position;
				rotation = spawn.Rotation;
			}

			client.Session.Character.MapId = map.Id;
			client.Session.Character.Position = position;
			client.Session.Character.Rotation = rotation;
			await Database.SaveCharacterTransform(client.Session.Character.Id, position, rotation, map.Id);

			var mapLabel = string.IsNullOrEmpty(map.Name)
				? map.Id.ToString()
				: $"{map.Name} ({map.Id})";
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Teleporting to {mapLabel}...");

			// Reproduce the retail change-map sequence (decoded from both changemap captures, consistent):
			// on the live connection, announce the destination - 0x10D27BC0 ("begin teleport to map N") then
			// 0x5A32F0D7 (destination map + spawn position) - THEN the 0x2002 handoff advert, after which the
			// client reconnects and does a fresh world entry on the new map. All three ride the handoff
			// conversation (0xb171ce13 -> 0x6c67c147 = SendWorldHandoff's sender/receiver).
			// Mark this client as a teleport re-entry so the reconnect Hello arms the post-0x205F follow-up (the
			// changemap reconnect carries no 0x205F, so 0x2024 ReadyForPlayScreen would otherwise never fire and the
			// loading screen would hang - #28). Consumed in the GameServer Hello existing-character branch.
			PendingTeleportReentry.TryAdd(client.Session.Account.ClientInstance, 0);

			// We've just persisted the destination position above; stop THIS (old) connection's imminent disconnect
			// from saving its stale pre-teleport live position back over it (ClientDisconnected does a fire-and-forget
			// SavePlayerState that would otherwise clobber the bind/destination - #28).
			client.Session.IsTeleportingOut = true;

			await SendChangeMapPrep(client, map.Id, position);
			await SendWorldHandoff(client);
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
					await _gmCommandsDict[command].Invoke(client, arguments);
				}
				catch (Exception e)
				{
					await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, $"<span style=\"color: red;\">{e.Message}</span>");
				}
			}
			else
				await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, $"Unknown command: '{command}' type .help for info");
		}

		// QueuePetCommand (0x205B, GameCharAgent): the pet command bar. Body = [u32 type=0xC350][u32 petHandle]
		// [u32 command]. Decoded commands (#307, docs/subsystems/summon-pet-decode.md): 0x1F8 = dismiss, 0x1F6 = defend.
		// Dismiss = retail's PetIIR mode-1 remove + the n3ToClientQuit despawn. Other commands are logged for now
		// (attack/follow/stay AI is a follow-up; a .pet-summoned minion already follows via SetNpcFollow).
		private async Task HandlePetCommand(INetworkClient client, ConanPacket packet)
		{
			packet.Body.ReadUInt32(); // type 0xC350
			var petHandle = packet.Body.ReadUInt32();
			var command = packet.Body.ReadUInt32<PetCommand>();
			var owner = client.Session.Account.ClientInstance;
			Logger.LogInformation("QueuePetCommand from {Owner:X8}: pet 0x{Pet:X8}, command {Command}.", owner, petHandle, command);

			if (command == PetCommand.Dismiss)
			{
				await new PacketStream()
					.WriteHeader(WorldFraming.Sender, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceDispatchOpcodes.DispatchPassBlob)
					.WriteArrayUInt32Length(s => s.WriteArray(PetIirCodec.BuildRemove(owner, petHandle)))
					.SendAsync(client);

				var removed = await _broadcaster.DespawnEntityForPlayer(client, petHandle);
				Logger.LogInformation(removed != null
					? "Pet 0x{Pet:X8} dismissed (despawned)."
					: "Pet dismiss: no entity 0x{Pet:X8} in the player's zone.", petHandle);
			}
		}

		private void InitGmCommands()
		{
			_gmCommandsDict = new Dictionary<string, GmCommandDelegate>(StringComparer.OrdinalIgnoreCase)
			{
				["save"] = (client, _) => SaveChanges(client.Session.Character.Id),
				["help"] = (client, _) => SendGmHelpText(client.Session.Character.Id),
				["maps"] = (client, _) => SendListAllMaps(client.Session.Character.Id),
				["teleport"] = TeleportCommand,
				["position"] = (client, _) => PositionCommand(client),
				["recall"] = (client, _) => RecallToBindCommand(client),
				["setstat"] = SetStatCommand,
				["setstatrange"] = SetStatRangeCommand,
				["level"] = SetLevelCommand,
				["godmode"] = (client, _) => ToggleGodModeCommand(client),
				["invisible"] = (client, _) => ToggleInvisibleCommand(client),
				["delete"] = DeleteCommand,
				["kill"] = KillCommand,
				["additem"] = AddItemCommand,
				["removeitem"] = RemoveItemCommand,
				["syschat"] = (client, _) => SysChatCommand(client),
				["lua"] = LuaCommand,
				["ah"] = (client, _) => OpenAuctionHouseCommand(client),
				["ban"] = (client, arguments) => BanCommand(client, arguments, true),
				["unban"] = (client, arguments) => BanCommand(client, arguments, false)
			};
		}

		// .kill [<handle> | self]  - kill the player's currently SELECTED target (last LookAt/SelectDeselect), or the
		// given handle, or self. An NPC runs the full combat death lifecycle (vitals push, despawn/corpse-loot, XP, 		// kill-quest advance); self drops the player's vitals to 0 and the world tick respawns them at the nearest
		// resurrection point. No arg + nothing selected -> a system message rather than a no-op.
		private async Task KillCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var selfHandle = client.Session.Account.ClientInstance;
			var arg = (arguments ?? "").Trim();

			uint targetHandle;
			if (arg.Length == 0)
				targetHandle = client.Session.SelectedTargetHandle;
			else if (arg.Equals("self", StringComparison.OrdinalIgnoreCase) || arg.Equals("me", StringComparison.OrdinalIgnoreCase))
				targetHandle = selfHandle;
			else if (!TryParseHandle(arg, out targetHandle))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .kill [&lt;handle&gt; | self]  (no arg = your selected target)");
				return;
			}

			if (targetHandle == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "No target selected.");
				return;
			}

			if (targetHandle == selfHandle)
			{
				var vitals = _combat.GetOrAddPlayerVitals(client);
				vitals.Health = 0; // the world tick detects the death and respawns at the nearest resurrection point
				await SendStatUpdate(client, vitals.ToVitalsBlock(), selfHandle);
				await _agentServerDispatcher.SendSystemMessage(characterId, "You kill yourself.");
				return;
			}

			await _combat.KillNpc(client, targetHandle);
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Killed target 0x{targetHandle:X8}.");
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
			var completed = (await Database.GetCharacterQuests(characterId)).Where(q => q.State == QuestState.Completed).Select(q => q.QuestId);
			var player = new GameLuaPlayer(tags, completed);

			var ok = LuaScriptVm.RunEffect(script.Source, player);
			foreach (var t in player.AddedTags)
				await Database.AddCharacterTag(characterId, (uint)t);

			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Lua {scriptId}: ran={ok}, +tags [{string.Join(",", player.AddedTags)}], spawned {player.SpawnedItems.Count}, solvedGoals [{string.Join(",", player.SolvedGoals)}], cast [{string.Join(",", player.CastSpells)}].");
		}

		// .ah  - open the auction house at the player's position (PoC bootstrap for the Tradepost; the real trigger
		// is interacting with an auctioneer NPC). Sends DispatchOpenTradepost so the client opens the AH window and
		// then sends TradepostOpen + searches, which HandleTradepostPacket answers with listings.
		private async Task OpenAuctionHouseCommand(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			var entity = PlayerWorldEntity(client);
			if (entity == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "No world entity for the player yet.");
				return;
			}

			// The window's first/default tab is the BANK (personal storage), a separate channel: container kind 105
			// delivered via a BankIIR (decoded, protocol_bank_storage). Populate it from the character's Container=105 rows.
			var rows = await Database.GetCharacterInventory(characterId);
			var bankRows = rows.Where(r => r.Container == (int)FullCharSnapshotBuilder.BankKind).ToList();
			var bank = FullCharSnapshotBuilder.BuildContainer(FullCharSnapshotBuilder.BankKind, 50, 30, characterId, bankRows);

			// ORDER MATTERS (fixes the old "first open shows an empty bank" quirk): retail sends the bank content
			// (the 0x201c slot registrations + the BankIIR) BEFORE DispatchOpenTradepost, so the window OPENS already
			// populated - account-faolan-...-Has-inventory-at-trader-bank F6208-6210 = [bank slot regs][BankIIR]
			// [DispOpenTradepost]. We previously sent the open FIRST, so the BankIIR landed before the window existed
			// and the first open rendered empty (you had to close + reopen). Now: bank first, open last.
			await _bank.SendBank(client, bank);
			await TradepostResponses.SendOpenTradepost(client, entity.Position);

			await _agentServerDispatcher.SendSystemMessage(characterId, $"Opened the auction house (PoC) at your position; bank tab has {bank.Items.Count} item(s).");
		}

		// .position  - print the caller's current map + live position (the in-world entity transform when
		// connected, else the persisted Character position). Formatted so the x y z can be pasted into .teleport.
		private Task PositionCommand(INetworkClient client)
		{
			var character = client.Session.Character;
			var entity = _simulation.PlayerWorldEntity(client);
			var mapId = entity?.MapId ?? character.MapId;
			var pos = entity?.Position ?? character.Position;
			return _agentServerDispatcher.SendSystemMessage(character.Id,
				$"Map {mapId} - position {pos.X.ToString("0.###", CultureInfo.InvariantCulture)} {pos.Y.ToString("0.###", CultureInfo.InvariantCulture)} {pos.Z.ToString("0.###", CultureInfo.InvariantCulture)}");
		}

		// .recall  - use the Totem of Origin: recall to the bound location (the persisted bind point). The real
		// trigger is using the totem ITEM (use_totem_of_origin capture); this GM command is the test bootstrap.
		private async Task RecallToBindCommand(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character.BindRespawnPointId is not uint pointId)
			{
				await _agentServerDispatcher.SendSystemMessage(character.Id, "You have no bind point. Use .bind to set one.");
				return;
			}

			var point = await Database.GetRespawnPoint(pointId);
			if (point == null)
			{
				await _agentServerDispatcher.SendSystemMessage(character.Id, "<span style=\"color: red;\">Your bind point no longer exists. Re-bind with .bind.</span>");
				return;
			}

			await _agentServerDispatcher.SendSystemMessage(character.Id, "The Totem of Origin pulls you home...");
			await DoTeleport(character.Id, point.MapId, point.Position);
		}

		// .removeitem <slot>  - the inverse of .additem: clear the calling character's bag cell `slot` (persisted
		// CharacterInventory row) and push the live DispatchRemoveItem (0x201F) so it disappears without a relog.
		private async Task RemoveItemCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var parts = (arguments ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length != 1 || !int.TryParse(parts[0], out var slot) || slot < 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .removeitem &lt;bagSlot&gt;");
				return;
			}

			var removed = await RemoveItemFromBackpack(client, slot);
			await _agentServerDispatcher.SendSystemMessage(characterId, removed
				? $"Removed the item in bag slot {slot} (live DispatchRemoveItem)."
				: $"No item in bag slot {slot}.");
		}

		private Task SendGmHelpText(uint characterId)
		{
			return _agentServerDispatcher.SendSystemMessage(characterId, $"Available Commands:<br />{_gmCommandsDict.Keys.JoinAsString("<br />")}");
		}

		// .level <n>  - set the player's character level, regenerate its MaxStats from the new class/level, persist, 		// and push the Level stat + refreshed vitals so the change shows without a relog (the persisted level also
		// rides the next login snapshot). Test-bootstrap convenience for level-gated features (e.g. #206 Totem of
		// Origin's min-20). Mirrors .setstat/.vitals; the stat's wire scale is applied at send.
		private async Task SetLevelCommand(INetworkClient client, string arguments)
		{
			var character = client.Session.Character;
			var parts = (arguments ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length == 0 || !ushort.TryParse(parts[0], out var level) || level == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(character.Id, "Usage: .level <n>  (1..65535)");
				return;
			}

			var previousLevel = character.Level;
			character.Level = level;
			// A GM `.level` set jumps to the START of the target level, so zero the within-level XP: otherwise stale
			// progress from a higher level shows against the (smaller) new MaxExp and the XP bar renders NEGATIVE on a
			// level-DOWN (the bar's fill = Exp/MaxExp with Exp > MaxExp, or Exp for a level whose threshold is now below
			// the carried total). Fresh level = empty bar.
			character.Experience = 0;
			character.RegenerateMaxStats();
			await Database.SaveCharacter(character);

			// Push Level AND a refreshed XP bar (Exp/MaxExp) so a GM `.level` ding shows exactly like an XP-driven ding
			// (#209): the kill path (CombatService.PushLevelUp) sets the same three stats, so both level-up paths emit an
			// identical Level + AttributeChanged-burst + vitals max push.
			await SendStatUpdate(client,
				new CharacterStatBlock().Set(CharacterStatId.Level, level).Set(CharacterStatId.Exp, character.Experience).Set(CharacterStatId.MaxExp, LevelingResolver.XpForNextLevel(level)));
			await SendVitals(client);
			// On an increase, emit the retail level-up attribute feedback (one AttributeChanged line per primary) so
			// the change shows the same as an XP-driven ding (#209); the vitals push above already raised the maxima.
			if (level > previousLevel)
			{
				// One NewLevelIIR ding per level gained first (fires the client's "reached level N" banner + effect), 				// THEN the per-attribute AttributeChanged lines - retail's order (combat-levelup capture 9937->9939).
				await SendLevelUpDings(client, previousLevel, level, character.Experience);
				await SendLevelUpFeedback(client, level, (uint)(level - previousLevel));

				// Level-gated ability unlocks, announced - same as the XP-driven ding (CombatService.PushLevelUp):
				// retail grants e.g. Rest as an announced 0x2052 at the L1->L2 ding (#209/#73).
				foreach (var ability in await Database.GetClassAbilitiesUnlockedBetween(character.Class, previousLevel, level))
					await SendAddSpecialAbility(client, ability);

				// Level-gated SPELL unlocks (#209/#74): the class's spells whose Spells.Level is in the crossed range, 				// learned + persisted like .learnspell (so they ride every relog). Same as the XP-driven ding.
				foreach (var spellId in await Database.GetClassSpellsUnlockedBetween(character.Class, previousLevel, level))
				{
					await SendLearnSpell(client, spellId);
					await Database.AddCharacterSpell(character.Id, spellId);
				}

				// Feat scaffold (#298/#251): a learned spell only becomes CASTABLE once the char carries the feat that
				// unlocks it. That spell->feat map is server-GDB-only (never on the wire), so we grant the whole
				// harvested class scaffold instead of the one exact feat. Rides the scaffold on the next spawn (relog).
				var fullScaffold = PlayerSpawnScaffoldRows.FullSkillIdsFor(character.Class);
				if (fullScaffold != null)
					await Database.EnsureCharacterSkills(character.Id, fullScaffold);
			}

			await _agentServerDispatcher.SendSystemMessage(character.Id, $"Level set to {level} (relog to refresh the full character sheet).");
		}

		// .syschat - push a world/system chat line into the chat window via ChatTextIIR (#103). Sends the captured
		// localized CriteriaYou line (arbitrary literal text needs a literal-text remoteformat template, like the
		// FIFO banner). The test bootstrap for the ChatTextIIR path.
		private async Task SysChatCommand(INetworkClient client)
		{
			await SendSampleWorldChat(client);
			await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, "Sent a world/system chat line (ChatTextIIR 0xE0B51102).");
		}

		// .teleport <mapId>  - move the character to another playfield via the retail HANDOFF + RECONNECT.
		//
		// Retail never changes playfield in place: it sends a 0x2002 world-address advert on the current
		// connection, the client opens a FRESH connection and does a full world entry there (changemap capture
		// account-faolan-...-changemap: :7027 -> :7030, frame 4214 advertises gs013-nj4...:7030). This matters
		// because the client only loads a playfield's resources (camera paths, geometry, NPCs) during a fresh
		// world entry - confirmed via Ghidra: camera paths are an RDB resource (e_RDB_CameraPaths, type 1000047)
		// keyed by playfield id, loaded by CameraPathMap_c::LoadPlayfieldPaths only on the n3Camera init a fresh
		// entry runs (see docs/subsystems/create-cutscene-camera-paths.md). A soft in-place MapChange on the live connection
		// does NOT trigger that reload, so the old playfield's resources stay loaded - that was the teleport bug.
		//
		// The handoff is the SAME packet (same 0xb171ce13 -> 0x6c67c147 conversation, byte-identical) the create
		// enter-world flow already sends (SendWorldHandoff), proving our single-host server CAN do it: it
		// advertises our own GameServer, the client reconnects, and the existing-character Hello path runs
		// SendWorldEntry for the (now persisted) target map - identical to logging in on that map, which works.
		// .teleport <mapId | name>  - resolves the argument to a map (numeric id, or a case-insensitive name match
		// so `.teleport Khemi` works) then hands off to DoTeleport. Also accepts `.teleport <x> <y> <z>` (three numeric
		// tokens) to move to a position on the CURRENT map. Gives a usage/echo message instead of throwing on a
		// missing/garbled argument (the old `uint.Parse(arguments)` surfaced a raw FormatException to the player).
		private async Task TeleportCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? string.Empty).Trim();
			if (arg.Length == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId,
					"Usage: .teleport &lt;mapId | name&gt;  or  .teleport &lt;x&gt; &lt;y&gt; &lt;z&gt;  (current map)  or  .teleport &lt;mapId | name&gt; &lt;x&gt; &lt;y&gt; &lt;z&gt;  (see .maps)");
				return;
			}

			// .teleport <x> <y> <z>  - three numeric tokens = a position on the CURRENT map (no map change).
			// Invariant culture so a comma-decimal locale doesn't reject the dotted coordinates.
			var tokens = arg.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
			if (tokens.Length == 3 && float.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var px) &&
			    float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var py) && float.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var pz))
			{
				await DoTeleport(characterId, client.Session.Character.MapId, new Vector3(px, py, pz));
				return;
			}

			// .teleport <mapId | name> <x> <y> <z>  - jump to a precise spot on a NAMED (or numeric) map in one command
			// (#247). The map is the first token, the trailing three are the coordinates; the map name itself may contain
			// spaces, so everything before the last three tokens is the map argument.
			if (tokens.Length >= 4 && float.TryParse(tokens[^3], NumberStyles.Float, CultureInfo.InvariantCulture, out var mpx) &&
			    float.TryParse(tokens[^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var mpy) && float.TryParse(tokens[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var mpz))
			{
				var mapArg = string.Join(' ', tokens[..^3]);
				var resolved = await ResolveMap(mapArg);
				if (resolved == null)
				{
					await _agentServerDispatcher.SendSystemMessage(characterId, $"<span style=\"color: red;\">No map matching '{mapArg}'. Try .maps.</span>");
					return;
				}

				await DoTeleport(characterId, resolved.Id, new Vector3(mpx, mpy, mpz));
				return;
			}

			if (uint.TryParse(arg, out var mapId))
			{
				await DoTeleport(characterId, mapId);
				return;
			}

			var match = await ResolveMap(arg);
			if (match == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, $"<span style=\"color: red;\">No map matching '{arg}'. Try .maps.</span>");
				return;
			}

			await DoTeleport(characterId, match.Id);
		}

		// .godmode  - toggle invincibility on yourself: hostile NPC swings land their animation but deal no damage.
		// The toggle is persisted on the account (#279) and re-applied to the live WorldEntity on every world entry, 		// so it survives relog AND map changes (a playfield change is a fresh world connection / entity). Self-target only.
		private async Task ToggleGodModeCommand(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			var entity = _simulation.PlayerWorldEntity(client);
			if (entity == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "You're not in the world yet.");
				return;
			}

			entity.GodMode = !entity.GodMode;
			client.Session.Account.GmGodMode = entity.GodMode;
			await Database.SaveAccount(client.Session.Account);
			await _agentServerDispatcher.SendSystemMessage(characterId, entity.GodMode
				? "God mode <span style=\"color: lime;\">ON</span> - you take no damage."
				: "God mode <span style=\"color: red;\">OFF</span>.");
		}

		// .invisible  - toggle aggro invisibility on yourself: hostile NPCs won't target/aggro you (skipped in the
		// sim's NearestPlayerInRange). Persisted on the account (#279) and re-applied on each world entry, so it
		// survives relog and map changes. Self-target only.
		private async Task ToggleInvisibleCommand(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			var entity = _simulation.PlayerWorldEntity(client);
			if (entity == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "You're not in the world yet.");
				return;
			}

			entity.Invisible = !entity.Invisible;
			client.Session.Account.GmInvisible = entity.Invisible;
			await Database.SaveAccount(client.Session.Account);
			await _agentServerDispatcher.SendSystemMessage(characterId, entity.Invisible
				? "Invisible <span style=\"color: lime;\">ON</span> - NPCs won't aggro you."
				: "Invisible <span style=\"color: red;\">OFF</span>.");
		}

		// .setstat <statIdHex> <displayValue>  - push a single 0x96B8DC59 stat record (e.g. ".setstat 36 25" sets
		// Level to 25). The stat's wire scale is applied automatically.
		private static Task SetStatCommand(INetworkClient client, string arguments)
		{
			var parts = (arguments ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
			var statId = (CharacterStatId)Convert.ToUInt32(parts[0], 16);
			var value = uint.Parse(parts[1]);

			return SendStatUpdate(client, new CharacterStatBlock().Set(statId, value));
		}

		// .setstatrange <startHex> <endHex> <value>  - DEBUG sweep: push every stat id in [start, end] set to `value`
		// in one 0x96B8DC59 update, to bisect which stat a client behaviour keys off (e.g. the spell-usability level).
		private static Task SetStatRangeCommand(INetworkClient client, string arguments)
		{
			var parts = (arguments ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
			var start = Convert.ToUInt32(parts[0], 16);
			var end = Convert.ToUInt32(parts[1], 16);
			var value = uint.Parse(parts[2]);
			var block = new CharacterStatBlock();
			for (var id = start; id <= end; id++)
				block.Set((CharacterStatId)id, value);
			return SendStatUpdate(client, block);
		}

		private static Task SetTimeOfDay(INetworkClient client)
		{
			var sender0 = WorldFraming.Sender;
			var receiver0 = client.Endpoint(Endpoints.GameInterfaceType);

			return new PacketStream()
				.WriteHeader(sender0, receiver0, GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s
					.WriteUInt32(GameTimeCodec.Opcode)
					.WriteArray(0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x47, 0x59, 0x92, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x6C, 0xD7, 0x60, 0x32, 0xDC, 0x20, 0x3F, 0x80, 0x00,
						0x00))
				.SendAsync(client);
		}

		private delegate Task GmCommandDelegate(INetworkClient client, string arguments);
	}
}
