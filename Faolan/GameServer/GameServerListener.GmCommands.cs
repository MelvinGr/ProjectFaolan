using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Progression;
using Faolan.GameServer.Economy;
using Faolan.GameServer.World;
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
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .additem <itemId> [count]");
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

		// .ban <account|character name>   - ban an account (sets State = Banned; login rejects non-Active accounts).
		// .unban <account|character name> - lift a ban (State = Active). Accepts either the account username OR one of
		// its character names. Banning also kicks the account's live world connections (the State change alone only
		// blocks the NEXT login). You can't ban your own account.
		private async Task BanCommand(INetworkClient client, string arguments, bool ban)
		{
			var gmId = client.Session.Character.Id;
			var name = arguments.Trim();
			if (string.IsNullOrEmpty(name))
			{
				await _agentServerDispatcher.SendSystemMessage(gmId, $"Usage: .{(ban ? "ban" : "unban")} <account or character name>");
				return;
			}

			// Resolve the target account: by username first, else by a character name -> its owning account.
			var account = await Database.GetAccount(name);
			if (account == null && await Database.GetCharacterByName(name, client.Session.Character.RealmId) is { } ch)
				account = await Database.GetAccount(ch.AccountId);

			if (account == null)
			{
				await _agentServerDispatcher.SendSystemMessage(gmId, ChatMarkup.Color("red", $"No account or character named '{name}'."));
				return;
			}

			if (ban && account.Id == client.Session.Account.Id)
			{
				await _agentServerDispatcher.SendSystemMessage(gmId, ChatMarkup.Color("red", "You can't ban your own account."));
				return;
			}

			if (account.Type > client.Session.Account.Type)
			{
				await _agentServerDispatcher.SendSystemMessage(gmId, ChatMarkup.Color("red", "You can't ban an account with a higher rank than yours."));
				return;
			}

			if (!await Database.SetAccountState(account.Id, ban
				    ? AccountState.Banned
				    : AccountState.Active))
			{
				await _agentServerDispatcher.SendSystemMessage(gmId, ChatMarkup.Color("red", "Error changing account state."));
				return;
			}

			var kicked = 0;
			if (ban)
			{
				// Snapshot before iterating: Stop() -> Disconnected mutates the client set.
				foreach (var c in NetworkClients.Where(nc => nc.Session.Account?.Id == account.Id).ToList())
				{
					if (c.Session.Character?.Id is { } cid)
						await _agentServerDispatcher.SendSystemMessage(cid, ChatMarkup.Color("red", "You have been banned by a GM."));
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
		// Unlike a plain live despawn (comes back on relog), .delete is durable.
		private async Task DeleteCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();
			var resolved = arg.Length == 0
				? client.Session.SelectedTargetHandle
				: TryParseHandle(arg, out var parsed)
					? parsed
					: null;
			if (resolved is not { } handle)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .delete [<handle>]  (no arg = your selected target). Removes it live AND deletes its spawn row.");
				return;
			}

			var entity = await _broadcaster.DespawnEntityForPlayer(client, handle);
			if (entity == null)
			{
				// Not a live entity - it may be a static WORLD OBJECT (chest/door/dynel), which lives only in the DB.
				var obj = await _broadcaster.DeleteWorldObjectForPlayer(client, handle);
				if (obj != null)
				{
					await _agentServerDispatcher.SendSystemMessage(characterId,
						$"Deleted world object {WorldObjectDisplay.ToWire(obj) ?? obj.Name ?? "(unnamed)"} (0x{handle:X8}) - removed live + DB row (gone on relog too).");
					return;
				}

				await _agentServerDispatcher.SendSystemMessage(characterId, $"No deletable entity or world object 0x{handle:X8} in this zone (players can't be deleted).");
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
				await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Color("red", $"Unknown map id: {mapId}"));
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
						ChatMarkup.Color("red", $"No spawn point defined for map {(string.IsNullOrEmpty(map.Name) ? map.Id.ToString() : $"{map.Name} ({map.Id})")}. Give explicit coordinates: .teleport <x> <y> <z>."));
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
			PendingTeleportReentry.TryAdd(client.Session.Character.Id, 0);

			// We've just persisted the destination position above; stop THIS (old) connection's imminent disconnect
			// from saving its stale pre-teleport live position back over it (ClientDisconnected does a fire-and-forget
			// SavePlayerState that would otherwise clobber the bind/destination - #28).
			client.Session.IsTeleportingOut = true;

			await SendChangeMapPrep(client, map.Id, position);
			await SendWorldHandoff(client);
		}

		// .graduatenpcgroup <group name>  - GRADUATE a resolved spawn area (#363): delete the CAPTURED NpcSpawn rows
		// LINKED to it (materialized by the resolver's auto-link + `.setnpcgroup`) so the ambient POOL becomes the sole
		// source of that population - removing the captured/ambient density overlap. A clean FK delete on the membership
		// link, so a named/quest NPC in the area (never linked) is safe. Durable; takes effect on the next zone reload.
		// NB re-running the `npcs` importer resurrects the captured rows.
		private async Task GraduateNpcGroupCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var name = (arguments ?? "").Trim();
			if (name.Length == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .graduatenpcgroup <group name>  (removes the captured spawns the area's ambient pool replaces).");
				return;
			}

			var mapId = _simulation.PlayerWorldEntity(client)?.MapId ?? client.Session.Character.MapId;
			var matches = (await Database.GetNpcSpawnAreas(mapId)).Where(a => string.Equals(a.GroupName, name, StringComparison.OrdinalIgnoreCase)).ToList();
			if (matches.Count == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, $"No spawn area named '{name}' on map {mapId}.");
				return;
			}

			var resolved = matches.Where(a => a.NpcTemplateId != null).ToList();
			if (resolved.Count == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, $"Area '{name}' has no creature assigned yet - resolve it with .setnpcgroup first (nothing to graduate).");
				return;
			}

			var removed = 0;
			foreach (var area in resolved)
			{
				var n = await Database.GraduateSpawnArea(area.Id);
				if (n > 0)
					removed += n;
			}

			var creature = resolved[0].NpcTemplate?.Name ?? "?";
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Graduated '{name}' ({creature}) on map {mapId}: removed {removed} captured spawn row(s); the ambient pool now owns this population. Takes effect on zone reload (relog).");
		}

		private async Task HandleGmCommand(INetworkClient client, string commandText)
		{
			string command;
			string? arguments = null;

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
					await _gmCommandsDict[command].Invoke(client, arguments!);
				}
				catch (Exception e)
				{
					await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, ChatMarkup.Color("red", $"{e.Message}"));
				}
			}
			else
				// Echo the prefix the caller actually used: the same table serves our `.cmd` chat commands and the
				// client's own `/cmd` GM commands (FanaticCommandIIR), and telling a `/teleport` user to "type .help"
				// with the wrong prefix reads as a different tool.
				await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id,
					$"Unknown command: '{command}' - type {commandText[0]}help for info");
		}

		// QueuePetCommand (0x205B, GameCharAgent): the pet command bar. Body = [u32 type=0xC350][u32 petHandle]
		// [u32 command]. Decoded commands (#307, docs/subsystems/summon-pet-decode.md): 0x1F8 = dismiss, 0x1F6 = defend.
		// Dismiss = retail's PetIIR mode-1 remove + the n3ToClientQuit despawn. Other commands are logged for now
		// (attack/follow/stay AI is a follow-up; a .pet-summoned minion already follows via SetNpcFollow).
		private async Task HandlePetCommand(INetworkClient client, Rpc packet, FaolanStream body)
		{
			body.ReadUInt32(); // type 0xC350
			var petHandle = body.ReadUInt32();
			var command = body.ReadUInt32<PetCommand>();
			var owner = client.Session.Character.Id;
			Logger.LogInformation("QueuePetCommand from {Owner:X8}: pet 0x{Pet:X8}, command {Command}.", owner, petHandle, command);

			if (command == PetCommand.Dismiss)
			{
				await client.SendAsync(new Rpc(WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob,
					b => b.WriteArrayUInt32Length(PetCodec.Build(PetBindings.Remove(owner, petHandle)))));

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
				["maps"] = (client, arguments) => SendListAllMaps(client.Session.Character.Id, arguments),
				["teleport"] = TeleportCommand,
				// The retail client's own verbs for the same thing, so a GM's muscle memory works: the client
				// registers /teleport, /teleportself and /tp all under the TeleportSelf privilege, and its
				// FanaticCommandIIR routes through this same table (#448).
				["teleportself"] = TeleportCommand,
				["tp"] = TeleportCommand,
				["position"] = (client, _) => PositionCommand(client),
				["recall"] = (client, _) => RecallToBindCommand(client),
				["recallhome"] = (client, _) => RecallToRacialHomeCommand(client),
				["criminal"] = CriminalCommand,
				["travel"] = TravelCommand,
				["knockback"] = KnockbackCommand,
				["level"] = SetLevelCommand,
				["feat"] = _feats.FeatCommand,
				["godmode"] = (client, _) => ToggleGodModeCommand(client),
				["invisible"] = (client, _) => ToggleInvisibleCommand(client),
				["delete"] = DeleteCommand,
				["kill"] = KillCommand,
				["hurt"] = HurtCommand,
				["additem"] = AddItemCommand,
				["removeitem"] = RemoveItemCommand,
				["claim"] = _claims.ClaimCommand,
				["syschat"] = (client, _) => SysChatCommand(client),
				["ah"] = (client, _) => OpenAuctionHouseCommand(client),
				["ban"] = (client, arguments) => BanCommand(client, arguments, true),
				["unban"] = (client, arguments) => BanCommand(client, arguments, false),
				["setnpcgroup"] = SetNpcGroupCommand,
				["target"] = (client, _) => TargetInfoCommand(client),
				["togglenpcgroup"] = (client, _) => ToggleNpcGroupCommand(client),
				["graduatenpcgroup"] = GraduateNpcGroupCommand,
				["weaponset"] = WeaponSetCommand,
				["time"] = TimeCommand,
				["setstat"] = SetStatCommand
			};
		}

		// .time [<hh:mm> | reset]  - report or set the world time of day, then resend the clock so the change shows
		// without a relog. The clock runs live off the wall clock at 5 game seconds per real second (#351), so a game day
		// is 4.8 real hours; this shifts its PHASE rather than stopping it. Verified in-client 2026-07-25: the sky follows
		// the time sent, so game-midnight is 0 and the client's own 06:00-18:00 day window (GameTime_t holds 21600.0 /
		// 64800.0) lines up with it. No argument reports the current time.
		// .setstat <statId> <rawValue> [<statId> <rawValue> ...]  - push arbitrary stats at their RAW WIRE value, with
		// no scaling applied by us. The measuring instrument for CharacterStatIdExtensions.GetScale: the client keeps
		// its stat table in wire units and divides only when it DRAWS, so a scale cannot be read off the wire or the
		// debug log - it needs a rendered number. Send a known raw value, read what the character sheet shows, and
		// `raw / displayed` IS the scale. Ids are decimal or 0x-hex, and several pairs ride one block so a whole sheet
		// tab can be probed in one shot.
		//
		// ⚠️ Deliberately NOT persisted and NOT reflected into the world model - this writes to the client's view
		// only, so a relog or the next vitals push undoes it. That keeps a probe from corrupting a character.
		private async Task SetStatCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var parts = (arguments ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length == 0 || parts.Length % 2 != 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .setstat <statId> <rawValue> [<statId> <rawValue> ...]   (ids decimal or 0x-hex; value is the RAW wire value)");
				return;
			}

			var block = new CharacterStatBlock();
			var applied = new List<string>();
			for (var i = 0; i < parts.Length; i += 2)
			{
				if (!TryParseStatId(parts[i], out var statId) || !uint.TryParse(parts[i + 1], out var raw))
				{
					await _agentServerDispatcher.SendSystemMessage(characterId, $"Not a stat id / raw value: '{parts[i]}' '{parts[i + 1]}'.");
					return;
				}

				block.SetRaw(statId, raw);
				applied.Add($"{(uint)statId} (0x{(uint)statId:X}) = {raw} raw");
			}

			await SendStatUpdate(client, block, characterId);
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Pushed {applied.Count} stat(s): {string.Join(", ", applied)}. Read the sheet - raw/displayed is the scale.");
		}

		private static bool TryParseStatId(string text, out CharacterStatId statId)
		{
			var ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
				? uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw)
				: uint.TryParse(text, out raw);

			statId = (CharacterStatId)raw;
			return ok;
		}

		private async Task TimeCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();

			if (arg.Equals("reset", StringComparison.OrdinalIgnoreCase))
				_gameTimeOffset = 0;
			else if (arg.Length > 0)
			{
				var parts = arg.Split(':', StringSplitOptions.TrimEntries);
				if (parts.Length != 2 || !uint.TryParse(parts[0], out var hours) || hours > 23 || !uint.TryParse(parts[1], out var minutes) || minutes > 59)
				{
					await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .time [<hh:mm> | reset]  (no argument reports the current time)");
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

		// .kill [<handle> | self]  - kill the player's currently SELECTED target (last LookAt/SelectDeselect), or the
		// given handle, or self. An NPC runs the full combat death lifecycle (vitals push, despawn/corpse-loot, XP, 		// kill-quest advance); self drops the player's vitals to 0 and the world tick respawns them at the nearest
		// resurrection point. No arg + nothing selected -> a system message rather than a no-op.
		private async Task KillCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var selfHandle = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();

			uint? resolved;
			if (arg.Length == 0)
				resolved = client.Session.SelectedTargetHandle;
			else if (arg.Equals("self", StringComparison.OrdinalIgnoreCase) || arg.Equals("me", StringComparison.OrdinalIgnoreCase))
				resolved = selfHandle;
			else if (TryParseHandle(arg, out var parsedHandle))
				resolved = parsedHandle;
			else
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .kill [<handle> | self]  (no arg = your selected target)");
				return;
			}

			if (resolved is not { } targetHandle)
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

		// .hurt [<percent>]  - drop the caller's three pools to `percent` of their max (default 50) and push the
		// authoritative vitals. The test bootstrap for the regen tick (Gitea #2): losing HP/mana to a mob or to casting
		// is unreliable to stage, and the pools have to end up BELOW max for the tick to emit anything at all. Never
		// kills - health floors at 1 - so this exercises regen rather than the death/release path (.kill self does that).
		private async Task HurtCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();
			var percent = 50u; // default: half of every pool
			if (arg.Length > 0 && (!uint.TryParse(arg, out percent) || percent > 100))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .hurt [<percent 0-100>]  (default 50)");
				return;
			}

			// Only ever LOWER a pool. This used to SET each one to `percent` of max, which topped UP anything already
			// below that - .hurt 50 on a character sitting at 0/74 mana handed it 37 mana. Min() keeps it a damage
			// command whatever state the pools are in.
			var vitals = _combat.GetOrAddPlayerVitals(client);
			vitals.Health = Math.Max(1, Math.Min(vitals.Health, vitals.MaxHealth * percent / 100));
			vitals.Mana = Math.Min(vitals.Mana, vitals.MaxMana * percent / 100);
			vitals.Stamina = Math.Min(vitals.Stamina, vitals.MaxStamina * percent / 100);
			await SendStatUpdate(client, vitals.ToVitalsBlock(), characterId);
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Hurt to {percent}%: health {vitals.Health}/{vitals.MaxHealth}, mana {vitals.Mana}/{vitals.MaxMana}, stamina {vitals.Stamina}/{vitals.MaxStamina}.");
		}

		// .ah  - open the auction house at the player's position (PoC bootstrap for the Tradepost; the real trigger
		// is interacting with an auctioneer NPC). Sends DispatchOpenTradepost so the client opens the AH window and
		// then sends TradepostOpen + searches, which HandleTradepostPacket answers with listings.
		private async Task OpenAuctionHouseCommand(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			var entity = _simulation.PlayerWorldEntity(client);
			if (entity == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "No world entity for the player yet.");
				return;
			}

			var bankCount = await SendAuctionHouseOpen(client, entity.Position);
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Opened the auction house (PoC) at your position; bank tab has {bankCount} item(s).");
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

		// .recall  - recall to the BOUND location (the persisted bind point). The retail triggers that land here are the
		// Path of Asura spell and a bindstone recall; this GM command is the test bootstrap.
		// ⚠️ NOT the Totem of Origins - that returns to the RACIAL home city and goes through RecallToRacialHomeCommand.
		// No on-screen line before the handoff: retail sends none, and the ones this used to send were invented.
		private async Task RecallToBindCommand(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character.BindRespawnPointId is not { } pointId)
			{
				await _agentServerDispatcher.SendSystemMessage(character.Id, "You have no bind point. Use .bind to set one.");
				return;
			}

			var point = await Database.GetRespawnPoint(pointId);
			if (point == null)
			{
				await _agentServerDispatcher.SendSystemMessage(character.Id, ChatMarkup.Color("red", "Your bind point no longer exists. Re-bind with .bind."));
				return;
			}

			await DoTeleport(character.Id, point.MapId, point.Position);
		}

		// .criminal [seconds]  - flag the calling character CRIMINAL for `seconds` (default the retail 300 s), or
		// `.criminal 0` to clear it. The standard test bootstrap: the retail trigger is killing another player, and PvP
		// player-vs-player combat is not modelled, so without this the criminal branches in ShopService are
		// unreachable. Also awards the measured murder points so the stat moves as it does in the capture.
		private async Task CriminalCommand(INetworkClient client, string arguments)
		{
			var character = client.Session.Character;
			var parts = (arguments ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			var seconds = CriminalStatus.CriminalSeconds;
			if (parts.Length > 0 && !int.TryParse(parts[0], out seconds))
			{
				await _agentServerDispatcher.SendSystemMessage(character.Id, $"Usage: .criminal [seconds]  (default {CriminalStatus.CriminalSeconds}, 0 clears)");
				return;
			}

			if (seconds <= 0)
			{
				character.CriminalUntil = null;
				character.MurderPoints = 0;
				await Database.SetCharacterCriminalState(character.Id, null, 0);
				await SendBuffRemoval(client, character.Id, character.Id, CriminalStatus.CriminalSpellId << 8);
				await SendStatUpdate(client, new CharacterStatBlock().Set(CharacterStatId.MurderPoints, 0), character.Id);
				await _agentServerDispatcher.SendSystemMessage(character.Id, "Criminal status cleared.");
				return;
			}

			character.CriminalUntil = DateTimeOffset.UtcNow.AddSeconds(seconds);
			character.MurderPoints += CriminalStatus.PointsPerPlayerKill;
			await Database.SetCharacterCriminalState(character.Id, character.CriminalUntil, character.MurderPoints);

			// The wire side: the "Criminal" debuff plus the murder-point stat, the two things the capture shows moving.
			var spell = await Database.GetSpell(CriminalStatus.CriminalSpellId);
			await SendMagicBuffIir(client, character.Id, character.Id, CriminalStatus.CriminalSpellId << 8,
				MagicBuffDatas.ApplyNow(CriminalStatus.CriminalSpellId, (uint)(seconds * 100), CasterLevel(client)));
			await SendStatUpdate(client, new CharacterStatBlock().Set(CharacterStatId.MurderPoints, character.MurderPoints), character.Id);

			await _agentServerDispatcher.SendSystemMessage(character.Id,
				$"Criminal for {seconds}s ({spell?.Name ?? "Criminal"} {CriminalStatus.CriminalSpellId}); murder points {character.MurderPoints}.");
		}

		// .recallhome  - use the Totem of Origins: return to the character's RACIAL home city, which is NOT the bind
		// point (see StartingData.RacialHomeMap for the mapping and the capture that pins the Stygian row). Bound
		// location vs racial origin are two separate destinations; the Totem ignores the bind entirely, so this shares
		// no state with RecallToBindCommand beyond the DoTeleport handoff.
		// No on-screen line before the handoff, same as the bind recall - retail sends none.
		private async Task RecallToRacialHomeCommand(INetworkClient client)
		{
			var character = client.Session.Character;
			if (!StartingData.RacialHomeMap.TryGetValue(character.Race, out var homeMapId))
			{
				// Not silent: an unmapped race is a content gap in RacialHomeMap, not a corrupt character.
				Logger.LogWarning("Totem of Origins: race {Race} has no home city mapped, so {CharacterId:X8} cannot recall home.", character.Race, character.Id);
				await _agentServerDispatcher.SendSystemMessage(character.Id, ChatMarkup.Color("red", $"No home city is mapped for race {character.Race}."));
				return;
			}

			var point = await Database.GetDefaultSpawn(homeMapId);
			if (point == null)
			{
				Logger.LogWarning("Totem of Origins: home map {Map} ({Race}) has no respawn point.", homeMapId, character.Race);
				await _agentServerDispatcher.SendSystemMessage(character.Id, ChatMarkup.Color("red", $"Home map {homeMapId} has no respawn point."));
				return;
			}

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
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .removeitem <bagSlot>");
				return;
			}

			var removed = await RemoveItemFromBackpack(client, slot);
			await _agentServerDispatcher.SendSystemMessage(characterId, removed
				? $"Removed the item in bag slot {slot} (live DispatchRemoveItem)."
				: $"No item in bag slot {slot}.");
		}

		// Bootstrap the AH window at accessPosition (must be within the 15-unit client proximity check). Shared by the
		// `.ah` GM command and the real trigger - talking to the "Trader" auctioneer NPC (GameServerListener.Inbound
		// TalkToNpc). The window's first/default tab is the BANK (personal storage), a separate channel: container kind
		// 105 delivered via a BankIIR (decoded, protocol_bank_storage), populated from the character's Container=105 rows.
		// ORDER MATTERS (fixes the old "first open shows an empty bank" quirk): retail sends the bank content (the 0x201c
		// slot registrations + the BankIIR) BEFORE DispatchOpenTradepost, so the window OPENS already populated -
		// account-faolan-...-Has-inventory-at-trader-bank F6208-6210 = [bank slot regs][BankIIR][DispOpenTradepost].
		// Sending the open FIRST landed the BankIIR before the window existed and the first open rendered empty. So:
		// bank first, open last. Returns the bank item count for the caller's feedback.
		private async Task<int> SendAuctionHouseOpen(INetworkClient client, Vector3 accessPosition)
		{
			var characterId = client.Session.Character.Id;
			var bankRows = (await Database.GetCharacterInventory(characterId, ContainerKind.Bank)).ToList();
			var bank = FullCharacterRoots.Container(ContainerKind.Bank, 50, 30, characterId, bankRows);

			await _bank.SendBank(client, bank);
			await TradepostResponses.SendOpenTradepost(client, accessPosition);
			return bank.Items.Count;
		}

		private Task SendGmHelpText(uint characterId)
		{
			return _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Raw($"Available Commands:<br />{_gmCommandsDict.Keys.JoinAsString("<br />")}"));
		}

		// .level <n>  - set the player's character level, regenerate its MaxStats from the new class/level, persist, 		// and push the Level stat + refreshed vitals so the change shows without a relog (the persisted level also
		// rides the next login snapshot). Test-bootstrap convenience for level-gated features (e.g. #206 Totem of
		// Origin's min-20). The stat's wire scale is applied at send.
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
			// No vitals row to refresh: the ceiling is DERIVED from (Class, Level), so setting the level above is
			// already the new max (#578).
			await Database.SaveCharacter(character);

			// Push Level AND a refreshed XP bar (Exp/MaxExp) so a GM `.level` ding shows exactly like an XP-driven ding
			// (#209): the kill path (CombatService.PushLevelUp) now builds the same block off the same
			// ReseedFromMaxStats, so both level-up paths emit an identical Level + vitals push.
			//
			// The vitals ride that block WHOLE - current and max. This was a `SendVitals(client)` that emitted the
			// three CURRENT ids only, on the belief that "max rides the snapshot": true at world entry, wrong for
			// every later change. Measured in-client (#450): `.level 80` left the client rendering `4285 / 1270` -
			// the new current against the max it had been seeded with 57 levels earlier.
			await SendStatUpdate(client,
				(await _combat.ReseedFromMaxStats(client))
					.ToVitalsBlock()
					// Level AND ApprenticeLevel: the nameplate renders max() of the two, so pushing Level alone left
					// a level-DOWN showing the old level over the health bar while the sheet showed the new one
					// (#472). See CharacterStatBlock.SetLevel.
					.SetLevel(level)
					.Set(CharacterStatId.Exp, character.Experience)
					.Set(CharacterStatId.MaxExp, LevelingResolver.XpForNextLevel(level)),
				client.Session.Character.Id);
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

				// Ability/skill scaffold (#298/#251): a learned spell only becomes CASTABLE once the char carries the
				// matching ability/skill scaffold entry, so we grant the whole harvested class scaffold. NB this is the
				// skill list, NOT the feat tree (feats ride a separate FeatIIR 0x28da68a0). Rides the next spawn (relog).
				var fullScaffold = PlayerSpawnScaffoldRows.FullSkillIdsFor(character.Class);
				if (fullScaffold != null)
					await Database.EnsureCharacterSkills(character.Id, fullScaffold);
			}

			await _agentServerDispatcher.SendSystemMessage(character.Id, $"Level set to {level} (relog to refresh the full character sheet).");
		}

		// .setnpcgroup [<group name>] [clear]  - manual creature assignment for an RDB spawn AREA (#363). Target/select
		// an NPC standing in the area (its type IS what the area spawns), then run this:
		//   .setnpcgroup                 -> assign the SELECTED NPC's creature to the area the NPC stands in
		//   .setnpcgroup <group>         -> assign it to the named area on that map
		//   .setnpcgroup clear           -> CLEAR the area the NPC stands in (back to unresolved)
		//   .setnpcgroup <group> clear   -> clear the named area
		// A set marks the area Manual (never re-guessed); a clear reverts NpcTemplateId to null so the resolver may
		// re-guess it. The dev nameplate labels (.togglenpcgroup) show each NPC's area so you know what to type.
		private async Task SetNpcGroupCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var player = _simulation.PlayerWorldEntity(client);
			if (player == null || client.Session.SelectedTargetHandle is not { } handle || !_simulation.World.ZoneForEntity(player).TryGet(handle, out var npc) || npc.NpcTemplateId == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Select an NPC first, then .setnpcgroup [<group name>] [clear]  (no arg = the area the NPC stands in).");
				return;
			}

			var areas = await Database.GetNpcSpawnAreas(npc.MapId);
			if (areas.Length == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, $"Map {npc.MapId} has no RDB spawn areas (scripted/instanced playfield).");
				return;
			}

			// A trailing "clear"/"none"/"-" makes this a CLEAR; the rest (if any) is the target group name.
			var tokens = (arguments ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			var clearing = tokens.Length > 0 && tokens[^1] is "clear" or "none" or "-";
			var groupName = string.Join(' ', clearing
				? tokens[..^1]
				: tokens);

			var area = groupName.Length > 0
				? areas.Where(a => string.Equals(a.GroupName, groupName, StringComparison.OrdinalIgnoreCase)).OrderBy(a => AreaDistanceSq(a, npc.Position)).FirstOrDefault()
				: NearestContainingArea(areas, npc.Position);
			if (area == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, groupName.Length > 0
					? $"No spawn area named '{groupName}' on map {npc.MapId}."
					: $"'{StripAreaLabel(npc.Name)}' isn't inside any spawn area on map {npc.MapId} - pass the group name explicitly.");
				return;
			}

			if (clearing)
			{
				await Database.SetNpcSpawnAreaCreature(area.Id, null);
				var unlinked = await Database.UnlinkNpcSpawnsFromArea(area.Id);
				await _agentServerDispatcher.SendSystemMessage(characterId, $"Spawn area '{area.GroupName}' (map {npc.MapId}) cleared - creature unassigned, {unlinked} captured spawn(s) un-linked.");
				return;
			}

			await Database.SetNpcSpawnAreaCreature(area.Id, npc.NpcTemplateId);
			var linked = await Database.LinkNpcSpawnsToArea(area.Id, npc.NpcTemplateId, npc.SpawnPosition.X, npc.SpawnPosition.Z);
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Spawn area '{area.GroupName}' (map {npc.MapId}) now spawns '{StripAreaLabel(npc.Name)}' [manual]; linked {linked} captured spawn(s) (graduate to remove them).");
		}

		// .syschat - push a world/system chat line into the chat window via ChatTextIIR (#103). Sends the captured
		// localized CriteriaYou line (arbitrary literal text needs a literal-text remoteformat template, like the
		// FIFO banner). The test bootstrap for the ChatTextIIR path.
		private async Task SysChatCommand(INetworkClient client)
		{
			await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, "Sent a world/system chat line (ChatTextIIR).");
		}

		// .target  - print info about the player's currently SELECTED target (last in-game LookAt/SelectDeselect):
		// name, wire handle, kind, template id, level, vitals, hostility, position, and the spawn area/group it belongs
		// to (the #363 labeling aid - read the group without the nameplate toggle). Reported in-game via system message.
		private async Task TargetInfoCommand(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			if (client.Session.SelectedTargetHandle is not { } handle)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "No target selected - click an NPC/object in-world first, then .target.");
				return;
			}

			var player = _simulation.PlayerWorldEntity(client);
			if (player == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "You're not in the world.");
				return;
			}

			// A live ENTITY (player / NPC / loot bag) in the zone entity model.
			if (_simulation.World.ZoneForEntity(player).TryGet(handle, out var t))
			{
				var kind = t.IsPlayer
					? "player"
					: t.IsLoot
						? "loot"
						: "npc";
				var vitals = t.Vitals != null
					? $"{t.Vitals.Health}/{t.Vitals.MaxHealth} hp"
					: "no vitals";
				var group = t.OverheadNameOverride ?? (t.SpawnAreaId is { } id
					? $"area #{id} (unlabeled)"
					: "none");
				await _agentServerDispatcher.SendSystemMessage(characterId,
					ChatMarkup.Lines(ChatMarkup.Raw($"{ChatMarkup.Bold(t.Name)}  (handle 0x{handle:X8}, {kind})"),
						$"template {t.NpcTemplateId} · level {t.Level} · {vitals} · {(t.Hostile ? "hostile" : "friendly")} (disp {t.Disposition})",
						$"pos ({t.Position.X:F1}, {t.Position.Y:F1}, {t.Position.Z:F1}) · map {t.MapId} / instance 0x{t.InstanceId:X8}", $"spawn group: {group}"));
				return;
			}

			// Not a live entity - a static WORLD OBJECT (chest/door/dynel) isn't in the entity model, it hangs off the
			// loaded zone. Resolve it there so a selected chest/door still reports.
			if (_simulation.World.ZoneForEntity(player).TryGetWorldObject(handle, out var live))
			{
				var obj = live.Row;
				var oname = WorldObjectDisplay.ToWire(obj) ?? obj.Name ?? "(unnamed object)";
				var dest = obj.DestinationPosition is { } dp
					? $" · teleports to map {obj.DestinationMapId} ({dp.X:F1}, {dp.Y:F1}, {dp.Z:F1})"
					: "";
				await _agentServerDispatcher.SendSystemMessage(characterId,
					ChatMarkup.Lines(ChatMarkup.Raw($"{ChatMarkup.Bold(oname)}  (handle 0x{handle:X8}, world-object)"),
						$"objectId {obj.ObjectId} · item template {obj.TemplateItemId?.ToString() ?? "none"} · subop 0x{obj.SubOpcode:X8} · {(live.IsClosed ? "closed" : "open")}",
						$"interact {obj.InteractAction} · carsGroup {obj.CarsGroup}{dest}", $"pos ({obj.Position.X:F1}, {obj.Position.Y:F1}, {obj.Position.Z:F1}) · map {obj.MapId}"));
				return;
			}

			await _agentServerDispatcher.SendSystemMessage(characterId, $"Selected handle 0x{handle:X8} isn't a live entity or a known world object in your zone (it may have despawned).");
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
		// .knockback [distance] - shoves the player away from their current TARGET (or, with no target, away from the
		// direction they are facing) using the real wire path: the forced-move MoveTask + SetPos pair plus the
		// "Knockback immunity" buff retail applies alongside it (#496).
		//
		// This is the standard GM bootstrap for a feature whose retail trigger is not wired. Here the trigger is
		// genuinely unknown rather than merely unbuilt: the corpus has ONE knockback and the ApplySpell in that batch
		// names the immunity guard, not the ability that did the knocking, so nothing says which attacks knock back.
		private async Task KnockbackCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var entity = _simulation.PlayerWorldEntity(client);
			if (entity == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Color("red", "You are not in the world."));
				return;
			}

			var distance = Knockback.DefaultDistance;
			var arg = (arguments ?? string.Empty).Trim();
			if (arg.Length > 0 && !float.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out distance))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .knockback [distance]");
				return;
			}

			// The SOURCE is what the player is pushed away from. Their current target is the natural stand-in for the
			// attacker; with none, push away from a point just in front of them so the command still demonstrates the
			// mechanic (being shoved backwards) instead of doing nothing.
			var zone = _simulation.World.ZoneForEntity(entity);
			Vector3 source;
			if (client.Session.SelectedTargetHandle is { } target && zone != null && zone.TryGet(target, out var targetEntity))
			{
				source = targetEntity.Position;
			}
			else
			{
				var facing = Vector3.Transform(new Vector3(0f, 0f, 1f), entity.Rotation);
				source = entity.Position - facing;
			}

			if (await _knockback.Knock(entity, source, distance))
				await _agentServerDispatcher.SendSystemMessage(characterId, $"Knocked back {distance:F2} units.");
			else
				await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Color("red", "Not displaced (no direction to push along, or fully resisted)."));
		}

		// .travel <npc name> [destination map id] - drives local travel (#506) the way retail's conversation would.
		// With no map id it LISTS what that NPC offers; with one it runs the real flow - criminal gate, fee, the scry
		// confirmation box, and the trip once the player clicks through it.
		//
		// This exists because the retail TRIGGER is not wired: the pick happens inside the NPC's captured Knubot
		// conversation, and marking a conversation node as "this one sells a trip to map N" is hand-authored wiring we
		// have not done (the quest-wiring module is the precedent). So the command is the standard GM bootstrap for a
		// feature whose trigger isn't wired yet - everything downstream of the pick is the real path, not a shortcut.
		private async Task TravelCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? string.Empty).Trim();
			if (arg.Length == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .travel <npc name> [destination map id]");
				return;
			}

			// The map id, when present, is the LAST whitespace-separated token - NPC names contain spaces, so splitting
			// from the front would break on every real traveller.
			var split = arg.LastIndexOf(' ');
			var npcName = arg;
			uint? mapId = null;
			if (split > 0 && uint.TryParse(arg[(split + 1)..], out var parsed))
			{
				npcName = arg[..split].Trim();
				mapId = parsed;
			}

			var destinations = await _travel.DestinationsOf(npcName);
			if (destinations.Length == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Color("red", $"\"{npcName}\" offers no travel destinations."));
				return;
			}

			if (mapId == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, $"\"{npcName}\" travels to:");
				foreach (var destination in destinations)
				{
					var map = await Database.GetMap(destination.DestinationMapId);
					await _agentServerDispatcher.SendSystemMessage(characterId,
						$"  {destination.DestinationMapId} {(string.IsNullOrEmpty(map?.Name) ? "" : map.Name)} - {destination.Cost} tin");
				}

				return;
			}

			await _travel.BeginTravel(client, npcName, mapId.Value);
		}

		private async Task TeleportCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? string.Empty).Trim();
			if (arg.Length == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId,
					"Usage: .teleport <mapId | name>  or  .teleport <x> <y> <z>  (current map)  or  .teleport <mapId | name> <x> <y> <z>  (see .maps)");
				return;
			}

			// .teleport <x> <y> <z>  - three numeric tokens = a position on the CURRENT map (no map change).
			// Invariant culture so a comma-decimal locale doesn't reject the dotted coordinates.
			var tokens = arg.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
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
					await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Color("red", $"No map matching '{mapArg}'. Try .maps."));
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
				await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Color("red", $"No map matching '{arg}'. Try .maps."));
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
				? ChatMarkup.Raw($"God mode {ChatMarkup.Color("lime", "ON")} - you take no damage.")
				: ChatMarkup.Raw($"God mode {ChatMarkup.Color("red", "OFF")}."));
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
				? ChatMarkup.Raw($"Invisible {ChatMarkup.Color("lime", "ON")} - NPCs won't aggro you.")
				: ChatMarkup.Raw($"Invisible {ChatMarkup.Color("red", "OFF")}."));
		}

		// .togglenpcgroup  - toggle the dev spawn-area nameplate labels (#363) live for this session: each NPC that
		// resolves to an RDB spawn AREA shows that area's "group | tier xN" over its head (pushed via SetNameIIR, since
		// the client draws NPC names from the template LDB and ignores the spawn name). Pairs with .setnpcgroup: read
		// the area off the nameplate, assign with .setnpcgroup. Off by default; the toggle re-applies to nearby NPCs now.
		private async Task ToggleNpcGroupCommand(INetworkClient client)
		{
			var (on, affected) = await _broadcaster.ToggleAreaLabels(client);
			await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id,
				$"Spawn-area nameplate labels {(on ? "ON" : "OFF")} - {affected} nearby NPC(s) updated. Walk to re-stream the rest.");
		}

		// .weaponset [primary | secondary]  - the server-side test bootstrap for the SwitchWeaponsButton set switch (#400).
		// No arg (or "swap"/"toggle") flips the active loadout; "primary"/"secondary" (or 1/2) forces one. Persists the
		// set and mirrors it on the live entity so the login-time gear-stat aggregation (which counts only the active
		// set's weapons) uses it. NB this drives the MODEL only - the cross-player swap RELAY needs the real BCC payload,
		// so exercise that with the retail weapon-swap key against a live viewer.
		private async Task WeaponSetCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();

			WeaponSet set;
			if (arg.Equals("secondary", StringComparison.OrdinalIgnoreCase) || arg == "2")
				set = WeaponSet.Secondary;
			else if (arg.Equals("primary", StringComparison.OrdinalIgnoreCase) || arg == "1")
				set = WeaponSet.Primary;
			else
			{
				var toggled = await _broadcaster.TogglePlayerWeaponSet(client);
				await _agentServerDispatcher.SendSystemMessage(characterId, $"Weapon set toggled -> {toggled} (persisted).");
				return;
			}

			await _broadcaster.SetPlayerWeaponSet(client, set);
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Weapon set -> {set} (persisted).");
		}

		private static float AreaDistanceSq(NpcSpawnArea a, Vector3 pos)
		{
			var cx = (a.BoundsMinX + a.BoundsMaxX) * 0.5f;
			var cz = (a.BoundsMinZ + a.BoundsMaxZ) * 0.5f;
			return (pos.X - cx) * (pos.X - cx) + (pos.Z - cz) * (pos.Z - cz);
		}

		// The RDB spawn area a position sits in (nearest centroid; bounds padded), or null when outside every area.
		private static NpcSpawnArea? NearestContainingArea(IReadOnlyList<NpcSpawnArea> areas, Vector3 pos)
		{
			const float pad = 5f;
			NpcSpawnArea? best = null;
			var bestDistSq = float.MaxValue;
			foreach (var a in areas)
			{
				if (pos.X < a.BoundsMinX - pad || pos.X > a.BoundsMaxX + pad || pos.Z < a.BoundsMinZ - pad || pos.Z > a.BoundsMaxZ + pad)
					continue;

				var d = AreaDistanceSq(a, pos);
				if (d < bestDistSq)
				{
					bestDistSq = d;
					best = a;
				}
			}

			return best;
		}

		// Strips the " [Area]" dev-label suffix (DebugSpawnAreaLabels) from a nameplate so feedback shows the creature.
		private static string? StripAreaLabel(string? name)
		{
			var i = name?.LastIndexOf(" [", StringComparison.Ordinal) ?? -1;
			return i >= 0
				? name![..i]
				: name;
		}

		private delegate Task GmCommandDelegate(INetworkClient client, string arguments);
	}
}
