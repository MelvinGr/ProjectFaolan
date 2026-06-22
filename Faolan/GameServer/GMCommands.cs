using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Data;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Faolan.GameServer.Packets.FullCharSnapshot;
using Faolan.GameServer.Scripting;
using Faolan.GameServer.World;
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
			var vitals = _combat.GetOrAddPlayerVitals(client);
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

		// .teleport <mapId>  - move the character to another playfield via the retail HANDOFF + RECONNECT.
		//
		// Retail never changes playfield in place: it sends a 0x2002 world-address advert on the current
		// connection, the client opens a FRESH connection and does a full world entry there (changemap capture
		// account-faolan-...-changemap: :7027 -> :7030, frame 4214 advertises gs013-nj4...:7030). This matters
		// because the client only loads a playfield's resources (camera paths, geometry, NPCs) during a fresh
		// world entry - confirmed via Ghidra: camera paths are an RDB resource (e_RDB_CameraPaths, type 1000047)
		// keyed by playfield id, loaded by CameraPathMap_c::LoadPlayfieldPaths only on the n3Camera init a fresh
		// entry runs (see docs/create-cutscene-camera-paths.md). A soft in-place MapChange on the live connection
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
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .teleport &lt;mapId | name&gt;  or  .teleport &lt;x&gt; &lt;y&gt; &lt;z&gt;  (current map)  or  .teleport &lt;mapId | name&gt; &lt;x&gt; &lt;y&gt; &lt;z&gt;  (see .maps)");
				return;
			}

			// .teleport <x> <y> <z>  - three numeric tokens = a position on the CURRENT map (no map change).
			// Invariant culture so a comma-decimal locale doesn't reject the dotted coordinates.
			var tokens = arg.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
			if (tokens.Length == 3
				&& float.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var px)
				&& float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var py)
				&& float.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var pz))
			{
				await DoTeleport(characterId, client.Session.Character.MapId, new Vector3(px, py, pz));
				return;
			}

			// .teleport <mapId | name> <x> <y> <z>  - jump to a precise spot on a NAMED (or numeric) map in one command
			// (#247). The map is the first token, the trailing three are the coordinates; the map name itself may contain
			// spaces, so everything before the last three tokens is the map argument.
			if (tokens.Length >= 4
				&& float.TryParse(tokens[^3], NumberStyles.Float, CultureInfo.InvariantCulture, out var mpx)
				&& float.TryParse(tokens[^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var mpy)
				&& float.TryParse(tokens[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var mpz))
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

		// Resolves a map argument (numeric id, exact case-insensitive name, or first name-contains match) to its Map row,
		// or null if nothing matches. Shared by the `.teleport <name>` and `.teleport <name> x y z` forms (#247).
		private async Task<Map> ResolveMap(string arg)
		{
			if (uint.TryParse(arg, out var id))
			{
				var maps = await Database.GetMaps();
				return maps.FirstOrDefault(m => m.Id == id);
			}

			var all = await Database.GetMaps();
			return all.FirstOrDefault(m => string.Equals(m.Name, arg, StringComparison.OrdinalIgnoreCase))
				?? all.FirstOrDefault(m => m.Name != null && m.Name.Contains(arg, StringComparison.OrdinalIgnoreCase));
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

			// Destination: an explicit point (bind/recall/coords) or the map's default spawn (the AlwaysKnown starter,
			// else any respawn point - GetDefaultSpawn). A map with no points defined can't resolve a spawn, so refuse
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

		// .godmode  - toggle invincibility on yourself: hostile NPC swings land their animation but deal no damage
		// (the flag lives on the player's live WorldEntity, so it clears on relog). Self-target only.
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
			await _agentServerDispatcher.SendSystemMessage(characterId, entity.GodMode
				? "God mode <span style=\"color: lime;\">ON</span> - you take no damage."
				: "God mode <span style=\"color: red;\">OFF</span>.");
		}

		// .invisible  - toggle aggro invisibility on yourself: hostile NPCs won't target/aggro you (skipped in the
		// sim's NearestPlayerInRange). Self-target only; clears on relog (lives on the live WorldEntity).
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
			await _agentServerDispatcher.SendSystemMessage(characterId, entity.Invisible
				? "Invisible <span style=\"color: lime;\">ON</span> - NPCs won't aggro you."
				: "Invisible <span style=\"color: red;\">OFF</span>.");
		}

		// .bind  - bind the Totem of Origin to a resurrection point. You can only bind to a point you've already
		// DISCOVERED (or the always-known starter) on this map - the real trigger is a wayfarer NPC binding you to one
		// (no capture yet), so this GM bootstrap binds to the nearest KNOWN point, mirroring the death-respawn rule
		// (RespawnResolver.ClosestKnown). The chosen point's map+position is materialized into the bind columns.
		private async Task BindCommand(INetworkClient client)
		{
			var character = client.Session.Character;
			var entity = _simulation.PlayerWorldEntity(client);
			var mapId = entity?.MapId ?? character.MapId;
			var position = entity?.Position ?? character.Position;

			var points = await Database.GetRespawnPoints(mapId);
			var discovered = new HashSet<uint>(await Database.GetDiscoveredRespawnPointIds(character.Id));
			var point = RespawnResolver.ClosestKnown(position, points, discovered);
			if (point == null)
			{
				await _agentServerDispatcher.SendSystemMessage(character.Id, "<span style=\"color: red;\">No discovered resurrection point on this map - find one first.</span>");
				return;
			}

			character.BindRespawnPointId = point.Id;
			await Database.SaveCharacterBind(character.Id, point.Id);

			await _agentServerDispatcher.SendSystemMessage(character.Id, $"Bind point set: {point.Name} ({point.MapId}).");
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

		// The change-map prep IIR bodies (begin + destination announce) the retail teleport sends before the 0x2002
		// handoff, built by the named TeleportCodec (#219, World/TeleportCodec.cs). In-client verification pending.
		private async Task SendChangeMapPrep(INetworkClient client, uint destMapId, Vector3 position, uint destDescriptor = 0x00000014u)
		{
			var instance = client.Session.Account.ClientInstance;
			// Source the destination playfield token from the destination map's burst so the announce matches the token
			// the reconnect's world-entry will stamp (#219 / #211) - it is per-playfield-instance, not a constant.
			var destToken = await _broadcaster.ResolveMapPlayfieldToken(destMapId);
			foreach (var body in new[] { TeleportCodec.BuildBegin(instance, destMapId), TeleportCodec.BuildDestination(instance, destMapId, position, destPlayfieldToken: destToken, destDescriptor: destDescriptor) })
				await new PacketStream()
					.WriteHeader(CreateEditorMorphAckSender, CreateEditorReceiver(client), GameServerCarrierOpcodes.IirCarrier)
					.WriteArrayUInt32Length(s => s.WriteArray(body))
					.SendAsync(client);
		}

		// Writes each connected player's live WorldEntity transform onto their Character and persists it. Returns the
		// number of players saved.
		// .grantability <id>  - grant an ability from the catalog to the player via the 0x2052 ability-grant. Makes the
		// ability appear so it can be dragged to the hotbar and cast, which is how the spell/mana drain is tested. The
		// grant is transient (not persisted) - it is re-sent only by login's GrantStartingAbilities. (The real spell
		// grant DispatchLearnSpell 0x200A is a separate command - see .learnspell / #73.)
		private async Task GrantAbilityCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			if (!uint.TryParse((arguments ?? "").Trim(), out var id))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .grantability &lt;abilityId&gt;  (see .abilitylist)");
				return;
			}

			var ability = await Database.GetAbility(id);
			if (ability == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, $"<span style=\"color: red;\">No grantable ability with id {id} (see .abilitylist).</span>");
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
				["abilitylist"] = (client, _) => SendGrantableAbilityList(client.Session.Character.Id),
				["grantability"] = (client, arguments) => GrantAbilityCommand(client, arguments),
				["teleport"] = (client, arguments) => TeleportCommand(client, arguments),
				["position"] = (client, _) => PositionCommand(client),
				["bind"] = (client, _) => BindCommand(client),
				["recall"] = (client, _) => RecallToBindCommand(client),
				["spelltest"] = (client, arguments) => ApplySpellTest(client.Session.Character.Id, uint.Parse(arguments)),
				["vitals"] = (client, arguments) => SetVitalsCommand(client, arguments),
				["setstat"] = (client, arguments) => SetStatCommand(client, arguments),
				["level"] = (client, arguments) => SetLevelCommand(client, arguments),
				["learnspell"] = (client, arguments) => LearnSpellCommand(client, arguments),
				["hurt"] = (client, arguments) => AdjustOwnHealthCommand(client, arguments, false),
				["heal"] = (client, arguments) => AdjustOwnHealthCommand(client, arguments, true),
				["godmode"] = (client, _) => ToggleGodModeCommand(client),
				["invisible"] = (client, _) => ToggleInvisibleCommand(client),
				["mount"] = (client, arguments) => MountCommand(client, arguments),
				["grantmount"] = (client, arguments) => GrantMountCommand(client, arguments),
				["spawnnpc"] = (client, arguments) => SpawnNpcCommand(client, arguments),
				["despawnnpc"] = (client, arguments) => DespawnNpcCommand(client, arguments),
				["despawnobj"] = (client, arguments) => DespawnWorldObjectCommand(client, arguments),
				["npcfollow"] = (client, arguments) => NpcFollowCommand(client, arguments),
				["delete"] = (client, arguments) => DeleteCommand(client, arguments),
				["kill"] = (client, arguments) => KillCommand(client, arguments),
				["additem"] = (client, arguments) => AddItemCommand(client, arguments),
				["removeitem"] = (client, arguments) => RemoveItemCommand(client, arguments),
				["feedback"] = (client, _) => FeedbackCommand(client),
				["syschat"] = (client, _) => SysChatCommand(client),
				["npcsay"] = (client, _) => NpcSayCommand(client),
				["world"] = (client, _) => SendWorldInfo(client),
				["quest"] = (client, arguments) => QuestCommand(client, arguments),
				["tag"] = (client, arguments) => TagCommand(client, arguments),
				["lua"] = (client, arguments) => LuaCommand(client, arguments),
				["ah"] = (client, _) => OpenAuctionHouseCommand(client),
			["shop"] = (client, arguments) => OpenShopCommand(client, arguments),
				["guild"] = (client, arguments) => GuildCommand(client, arguments),
				["money"] = (client, arguments) => MoneyCommand(client, arguments),
				["buffs"] = (client, _) => BuffsCommand(client),
				["cancelbuff"] = (client, arguments) => CancelBuffCommand(client, arguments),
				["spawnbag"] = (client, arguments) => SpawnBagCommand(client, arguments)
			};
		}

		// .buffs  - list the server-tracked active buffs on the player (the registry CancelBuff removes from).
		private async Task BuffsCommand(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			var entity = PlayerWorldEntity(client);
			if (entity == null || entity.ActiveBuffs.IsEmpty)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "No server-tracked buffs.");
				return;
			}

			var now = DateTimeOffset.UtcNow;
			var lines = entity
				.ActiveBuffs
				.Values
				.OrderBy(b => b.AppliedAt)
				.Select(b => $"0x{b.BuffId:X8} (for {(now - b.AppliedAt).TotalSeconds:F0}s{(b.DurationSeconds is double d ? $", expires after {d:F0}s" : "")})");
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Active buffs: {string.Join(", ", lines)}");
		}

		// .cancelbuff <id> | .cancelbuff all  - cancel a server-tracked buff on the player. <id> accepts hex
		// (0x000B9000 / 000B9000 / B9000) or decimal. Removes the entry from the ActiveBuffs registry (stops tick
		// stat-effects / re-assertion, makes .buffs accurate) AND pushes the decoded server->client buff-removal
		// (#253: the ApplySpell rank-1 "end" twin with an empty payload, from the mount-dismount captures) so the
		// client clears the icon LIVE - no relog needed.
		private async Task CancelBuffCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var entity = PlayerWorldEntity(client);
			if (entity == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Not in the world.");
				return;
			}

			var arg = (arguments ?? "").Trim();
			if (arg.Length == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .cancelbuff &lt;id&gt; | .cancelbuff all");
				return;
			}

			if (string.Equals(arg, "all", StringComparison.OrdinalIgnoreCase))
			{
				var ids = entity.ActiveBuffs.Keys.ToArray();
				foreach (var id in ids)
					if (entity.ActiveBuffs.TryRemove(id, out _))
						await SendBuffRemoval(client, entity.Handle, id); // #253: live icon clear
				Logger.LogInformation("CancelBuff (.cancelbuff all): removed {Count} buff(s) from {CharId:X8}.", ids.Length, characterId);
				await _agentServerDispatcher.SendSystemMessage(characterId,
					ids.Length > 0
						? $"Cancelled {ids.Length} buff(s)."
						: "No server-tracked buffs to cancel.");
				return;
			}

			var hex = arg.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? arg[2..] : arg;
			if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var buffId)
			    && !uint.TryParse(arg, out buffId))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, $"Unrecognised buff id '{arg}'. Use hex (0x000B9000) or decimal; see .buffs.");
				return;
			}

			if (entity.ActiveBuffs.TryRemove(buffId, out _))
			{
				await SendBuffRemoval(client, entity.Handle, buffId); // #253: push the live buff-removal so the icon clears
				Logger.LogInformation("CancelBuff (.cancelbuff): buff 0x{Buff:X8} removed from {CharId:X8}.", buffId, characterId);
				await _agentServerDispatcher.SendSystemMessage(characterId,
					$"Cancelled buff 0x{buffId:X8}.");
			}
			else
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, $"Buff 0x{buffId:X8} is not tracked (see .buffs).");
			}
		}

		// .money            - show the wallet balance.
		// .money <tin>      - set the balance (and push the 0x2005 wallet update so the client HUD reflects it).
		// .money +<tin>     - add to the balance.
		//
		// The wallet wire unit is TIN, the game's smallest coin (100 tin = 1 copper, 100 copper = 1 silver,
		// 100 silver = 1 gold) - verified in-client 2026-06-12: .money 100 displays as 1 copper.
		private async Task MoneyCommand(INetworkClient client, string arguments)
		{
			var character = client.Session.Character;
			var characterId = character.Id;
			var arg = (arguments ?? "").Trim();

			if (arg.Length > 0)
			{
				var add = arg[0] == '+';
				if (!ulong.TryParse(add
					    ? arg[1..]
					    : arg, out var amount))
				{
					await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .money [&lt;tin&gt; | +&lt;tin&gt;]");
					return;
				}

				character.Money = add
					? character.Money + amount
					: amount;
				await Database.SetCharacterMoney(character.Id, character.Money);
				await SendMoneyUpdate(client, character.Money, add
					? MoneyUpdateReason.Gain
					: MoneyUpdateReason.Spend);
			}

			var tin = character.Money;
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Wallet: {tin / 1000000} gold {tin / 10000 % 100} silver {tin / 100 % 100} copper {tin % 100} tin ({tin} tin total).");
		}

		// .guild create <name>  - create a guild with the player as leader (test bootstrap for the guild flow;
		// the capture-true path is the CreateGuild RPC on the CsPlayerAgent connection, which a guild registrar
		// NPC window triggers). The in-world tag (0x2550C99A) shows immediately; the guild WINDOW state (0x203F)
		// arrives on the CsPlayerAgent connection at the next login.
		private async Task GuildCommand(INetworkClient client, string arguments)
		{
			var character = client.Session.Character;
			var characterId = character.Id;
			var parts = (arguments ?? "")
				.Trim()
				.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length < 2 || !parts[0]
				    .Equals("create", StringComparison.OrdinalIgnoreCase))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .guild create <name>");
				return;
			}

			var name = parts[1]
				.Trim();
			if (character.GuildId != null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "<span style=\"color: red;\">You are already in a guild.</span>");
				return;
			}

			var guild = await Database.CreateGuild(name, characterId);
			if (guild == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, $"<span style=\"color: red;\">Guild name \"{name}\" is taken.</span>");
				return;
			}

			character.GuildId = guild.Id;
			character.GuildRank = Character.GuildLeaderRank;
			await client.SendAsync(BuildGuildTag(client, client.Session.Account.ClientInstance, guild.Name));
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Guild \"{guild.Name}\" created (id {guild.Id}). Relog to load the guild window.");
		}

		// .ah  - open the auction house at the player's position (PoC bootstrap for the Tradepost; the real trigger
		// is interacting with an auctioneer NPC). Sends DispatchOpenTradepost so the client opens the AH window and
		// then sends TradepostOpen + searches, which the TradepostHandler answers with listings.
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
			var bankRows = rows
				.Where(r => r.Container == (int)FullCharSnapshotBuilder.BankKind)
				.ToList();
			var bank = FullCharSnapshotBuilder.BuildContainer(FullCharSnapshotBuilder.BankKind, 50, 30, characterId, bankRows);

			// ORDER MATTERS (fixes the old "first open shows an empty bank" quirk): retail sends the bank content
			// (the 0x201c slot registrations + the BankIIR) BEFORE DispatchOpenTradepost, so the window OPENS already
			// populated - account-faolan-...-Has-inventory-at-trader-bank F6208-6210 = [bank slot regs][BankIIR]
			// [DispOpenTradepost]. We previously sent the open FIRST, so the BankIIR landed before the window existed
			// and the first open rendered empty (you had to close + reopen). Now: bank first, open last.
			await _bank.SendBank(client, bank);
			await Economy.Tradepost.TradepostResponses.SendOpenTradepost(client, entity.Position);

			await _agentServerDispatcher.SendSystemMessage(characterId, $"Opened the auction house (PoC) at your position; bank tab has {bank.Items.Count} item(s).");
		}

		// .shop [vendorName]  - serve a vendor window for testing the SELL/BUY round-trip (PoC bootstrap; the real
		// trigger is the client's own NPC trade-initiation, which the shop window is gated on - a cold push sets up
		// the server state but doesn't pop the window in-client). Records the open shop on the session so a BuyItem
		// resolves; without an argument it picks the first imported shop. Buy a slot to exercise
		// DispatchAddItem + the cash debit.
		private async Task OpenShopCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var vendor = (arguments ?? "").Trim();

			var shop = string.IsNullOrEmpty(vendor)
				? await Database.GetFirstShop()
				: await Database.GetShopByVendorName(vendor);
			if (shop == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId,
					string.IsNullOrEmpty(vendor) ? "No shops imported." : $"No shop for vendor \"{vendor}\".");
				return;
			}

			// The window renders against the vendor NPC's handle; for the bootstrap we don't have the vendor's
			// entity, so address it to the player's own world handle (the window is cold-push-gated anyway).
			var entity = PlayerWorldEntity(client);
			await _shop.SendShopData(client, shop, entity?.Handle ?? 0);

			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Served shop \"{shop.VendorName}\" ({shop.Items.Count} wares). Buy a slot to test the grant+debit (window render is client-gated).");
		}

		// .lua run <scriptId>  - run an RDB LuaArchive script against the player (the conversation/quest VM engine):
		// reads the player's tags/quests, runs the script, persists any tags it adds, and reports its effects. Proves
		// the Lua VM end-to-end on a real character (see protocol_conversation_routing).
		private async Task LuaCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var parts = (arguments ?? "")
				.Trim()
				.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length < 2 || !parts[0]
				    .Equals("run", StringComparison.OrdinalIgnoreCase) || !uint.TryParse(parts[1], out var scriptId))
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
				.Where(q => q.State == QuestState.Completed)
				.Select(q => q.QuestId);
			var player = new GameLuaPlayer(tags, completed);

			var ok = _luaVm.RunEffect(script.Source, player);
			foreach (var t in player.AddedTags)
				await Database.AddCharacterTag(characterId, (uint)t);

			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Lua {scriptId}: ran={ok}, +tags [{string.Join(",", player.AddedTags)}], spawned {player.SpawnedItems.Count}, solvedGoals [{string.Join(",", player.SolvedGoals)}], cast [{string.Join(",", player.CastSpells)}].");
		}

		// .mount [mountIdHex]  - server-force a mount summon, the same path the client-cast SummonMount now drives
		// (#27): cast the summon spell (channeled ApplySpell 0x6B87E316 / 0xF508F4C1) AND spawn the mount creature at
		// the player so it appears. <mountId> selects the catalog mount (default 0x3E34F1 "Fading Black Horse"); the
		// player actually RIDING (the MountedRun movement transition) is the client's part and in-client-gated.
		private async Task MountCommand(INetworkClient client, string arguments)
		{
			var mount = Faolan.GameServer.World.MountCatalog.Default;
			if (!string.IsNullOrWhiteSpace(arguments)
				&& uint.TryParse(arguments.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase),
					System.Globalization.NumberStyles.HexNumber, null, out var id))
				mount = Faolan.GameServer.World.MountCatalog.ForId(id) ?? mount;

			var self = client.Session.Account.ClientInstance;
			await ApplySpell(client, self, self, mount.SummonSpellId); // the channeled summon cast (broadcast)
			await _broadcaster.SpawnMountForPlayer(client, mount.CreatureName);
		}

		// .grantmount [mountIdHex]  - put a catalog mount on the player's action bar so they can SUMMON it themselves
		// (#27), the client-initiated flow. Persists a CharacterShortcut (mount shortcut type 0x69, decoded from the
		// UpdateShortcut 0x206E the client sends after mounting) on the special bar so it survives relog and restores
		// via ShortcutBarReply; then the player clicks it and the SummonMount handler casts + spawns the mount. Default
		// 0x3E34F1 ("Fading Black Horse").
		private const uint MountShortcutType = 0x69; // UpdateShortcut 0x206E type word for a mount bar entry (capture-decoded)
		private const uint MountShortcutSlot = 0x4E2C; // a free special-bar slot (the 0x4E20+ special bar EnsureStartingLoadout uses)

		private async Task GrantMountCommand(INetworkClient client, string arguments)
		{
			var character = client.Session.Character;
			var mount = Faolan.GameServer.World.MountCatalog.Default;
			if (!string.IsNullOrWhiteSpace(arguments)
				&& uint.TryParse(arguments.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase),
					System.Globalization.NumberStyles.HexNumber, null, out var id))
				mount = Faolan.GameServer.World.MountCatalog.ForId(id) ?? mount;

			await Database.SetCharacterShortcut(character.Id, MountShortcutSlot, MountShortcutType, mount.Id);
			await _agentServerDispatcher.SendSystemMessage(character.Id,
				$"Granted mount '{mount.CreatureName}' (0x{mount.Id:X}) to the action bar. Relog or it appears on the bar; click it to summon.");
		}

		// .spawnnpc <npc name>  - spawn a captured NPC (any NpcSpawns row with spawn Data, matched by name,
		// case-insensitive, e.g. ".spawnnpc crocodile") at the player. Killable + hostile per its row; the test
		// bootstrap for combat/AI work and the seam the MCP spawn_npc tool routes through.
		private async Task SpawnNpcCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var name = (arguments ?? "").Trim();
			if (name.Length == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .spawnnpc <npc name> (captured name, e.g. .spawnnpc Crocodile)");
				return;
			}

			var entity = await _broadcaster.SpawnNpcForPlayer(client, name);
			await _agentServerDispatcher.SendSystemMessage(characterId, entity == null
				? $"No spawnable NPC named '{name}' (needs a captured NpcSpawns row with spawn data; any map)."
				: $"Spawned {entity.Name} (handle 0x{entity.Handle:X8}, level {entity.Level}{(entity.Hostile ? ", hostile" : "")}).");
		}

		// .spawnbag  - drop a loot bag at your feet with a couple of random items + some coins (the same bag a kill
		// can drop). Open it and "loot all" to grant the items live (DispatchAddItem) and the coins (TryLootCash).
		private async Task SpawnBagCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;

			var items = await Database.GetRandomBuildableItems(Random.Shared.Next(2, 4)); // 2-3 items
			if (items.Length == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "No snapshot-buildable items in the DB to fill a bag with.");
				return;
			}

			var templates = items
				.Select(i => i.Id)
				.ToList();
			var cash = (uint)Random.Shared.Next(50, 500);

			var bag = await _broadcaster.SpawnLootBagForPlayer(client, templates, cash);
			if (bag == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Couldn't spawn a loot bag here (this map has no captured loot-bag template).");
				return;
			}

			var names = string.Join(", ", items.Select(i => i.Name));
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Dropped {bag.Name} (0x{bag.Handle:X8}) with {items.Length} items [{names}] + {cash} tin. Open it and loot all.");
		}

		// .despawnnpc <handle>  - despawn a non-player entity by its wire handle (hex with 0x prefix, or decimal)
		// in the caller's zone: n3ToClientQuit to players in range + removal from the world model. The cleanup
		// complement to .spawnnpc; handles come from list_zone_entities or the spawn message.
		private async Task DespawnNpcCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();
			if (arg.Length == 0 || !TryParseHandle(arg, out var handle))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .despawnnpc <handle> (0x-hex or decimal)");
				return;
			}

			var entity = await _broadcaster.DespawnEntityForPlayer(client, handle);
			await _agentServerDispatcher.SendSystemMessage(characterId, entity == null
				? $"No despawnable entity 0x{handle:X8} in this zone (unknown handle, or a player)."
				: $"Despawned {entity.Name} (0x{handle:X8}).");
		}

		// .despawnobj <handle>  - despawn a STATIC world object by its wire handle (the Casilda chain decor is
		// 0x00015DD8). Unlike .despawnnpc, world objects aren't in the live world model, so this sends n3ToClientQuit
		// straight to the caller with the live playfield token. Live-only: the object returns on relog (the world
		// burst re-sends it). Used to "free" Casilda from the chains when the intro chain completes (#266/#267).
		private async Task DespawnWorldObjectCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();
			if (arg.Length == 0 || !TryParseHandle(arg, out var handle))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .despawnobj <handle> (0x-hex or decimal; the Casilda chain is 0x15DD8)");
				return;
			}

			var sent = await _broadcaster.DespawnWorldObjectForPlayer(client, handle);
			await _agentServerDispatcher.SendSystemMessage(characterId, sent
				? $"Despawned world object 0x{handle:X8} (live only - returns on relog)."
				: $"Can't despawn 0x{handle:X8} - your zone has no playfield token yet.");
		}

		// .npcfollow <handle> [stop]  - make a non-player entity follow you (companion-follow, #262), or 'stop' to
		// release it. The world tick steps it toward you and streams MoveTasks (the same server-driven follow retail
		// uses for Casilda after her rescue). Test bootstrap for the quest-driven trigger.
		private async Task NpcFollowCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var parts = (arguments ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			var stop = parts.Length > 0 && parts[^1].Equals("stop", StringComparison.OrdinalIgnoreCase);
			// Handle = first non-"stop" arg, else the currently SELECTED target (click Casilda, then .npcfollow).
			var handleArg = parts.FirstOrDefault(p => !p.Equals("stop", StringComparison.OrdinalIgnoreCase));
			var handle = handleArg != null && TryParseHandle(handleArg, out var parsed) ? parsed : client.Session.SelectedTargetHandle;
			if (handle == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .npcfollow [&lt;handle&gt;] [stop]  (no handle = your selected target)");
				return;
			}

			var npc = _broadcaster.SetNpcFollow(client, handle, !stop);
			await _agentServerDispatcher.SendSystemMessage(characterId, npc == null
				? $"No followable entity 0x{handle:X8} in this zone."
				: stop ? $"{npc.Name} (0x{handle:X8}) stopped following." : $"{npc.Name} (0x{handle:X8}) now follows you.");
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

			var deleted = await Database.DeleteNpcSpawns(entity.MapId, entity.Name,
				entity.Hostile ? entity.SpawnPosition.X : null,
				entity.Hostile ? entity.SpawnPosition.Y : null,
				entity.Hostile ? entity.SpawnPosition.Z : null, 10f);
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Deleted {entity.Name} (0x{handle:X8}) - removed live + {deleted} spawn row(s) (gone on relog too).");
		}

		// .kill [<handle> | self]  - kill the player's currently SELECTED target (last LookAt/SelectDeselect), or the
		// given handle, or self. An NPC runs the full combat death lifecycle (vitals push, despawn/corpse-loot, XP,
		// kill-quest advance); self drops the player's vitals to 0 and the world tick respawns them at the nearest
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

		// Parses a wire handle from a GM-command argument: "0x"-prefixed hex or plain decimal. Returns false on a
		// malformed/overflowing value.
		private static bool TryParseHandle(string arg, out uint handle)
		{
			handle = 0;
			try
			{
				handle = arg.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
					? Convert.ToUInt32(arg[2..], 16)
					: uint.Parse(arg);
				return true;
			}
			catch (FormatException)
			{
				return false;
			}
			catch (OverflowException)
			{
				return false;
			}
		}

		// .additem <itemId> [count]  - add an item (by faolan.db Items id) to the calling character's bag: persist
		// a CharacterInventory row in the first free bag slot, then re-send the rebuilt 0x737A6DF9 inventory
		// snapshot so it appears live (the snapshot is the ONLY item-delivery packet - there is no incremental
		// server add-item; see protocol_containeradditem). Without a stashed snapshot template (pre-world or a
		// non-replay session) the item still persists and renders on next login.
		private async Task AddItemCommand(INetworkClient client, string arguments)
		{
			var character = client.Session.Character;
			var characterId = character.Id;
			var parts = (arguments ?? "")
				.Trim()
				.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			uint count = 1;
			if (parts.Length is < 1 or > 2 || !uint.TryParse(parts[0], out var itemId) || (parts.Length == 2 && !uint.TryParse(parts[1], out count)))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .additem &lt;itemId&gt; [count]");
				return;
			}

			var item = await Database.GetItem(itemId);
			if (item is not { Archetype: not 0 })
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, item == null
					? $"No item with id {itemId}."
					: $"Item {itemId} ('{item.Name}') has no archetype - not snapshot-buildable.");
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

			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Added {count}x '{item.Name}' (id {itemId}) to bag slot {slot} (live DispatchAddItem).");
		}

		// .removeitem <slot>  - the inverse of .additem: clear the calling character's bag cell `slot` (persisted
		// CharacterInventory row) and push the live DispatchRemoveItem (0x201F) so it disappears without a relog.
		private async Task RemoveItemCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var parts = (arguments ?? "")
				.Trim()
				.Split(' ', StringSplitOptions.RemoveEmptyEntries);
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

		// .feedback  - pops the on-screen FIFO notification banner (top of screen, NOT chat) via FormatFeedbackIIR
		// (0x8D5A8B6B / showfifomessagefromserver). Replays the proven GenericDisplayAreaName banner addressed to the
		// live session, to demonstrate the overlay channel. (Arbitrary literal text is a follow-up - needs a
		// literal-text FIFO template or an RDB string id.)
		private async Task FeedbackCommand(INetworkClient client)
		{
			await SendAreaNameBanner(client);
			await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id,
				"Sent an on-screen FIFO banner (FormatFeedbackIIR / showfifomessagefromserver).");
		}

		// .syschat - push a world/system chat line into the chat window via ChatTextIIR (#103). Sends the captured
		// localized CriteriaYou line (arbitrary literal text needs a literal-text remoteformat template, like the
		// FIFO banner). The test bootstrap for the ChatTextIIR path.
		private async Task SysChatCommand(INetworkClient client)
		{
			await SendSampleWorldChat(client);
			await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id,
				"Sent a world/system chat line (ChatTextIIR 0xE0B51102).");
		}

		// .npcsay - CONSTRUCT a Knubot dialogue line (KnubotAppendTextIIR) from the live endpoints and push it to the
		// player, addressed to its current conversation NPC (or itself as a fallback handle): opens the chat window then
		// appends the captured greeting, rebuilt byte-for-byte by KnubotCodec. The test bootstrap for the #188 construct
		// path (vs ConversationService's verbatim ConversationPacket replay). NEEDS IN-CLIENT VERIFICATION.
		private async Task NpcSayCommand(INetworkClient client)
		{
			var npcHandle = client.Session.ConversationNpcHandle != 0
				? client.Session.ConversationNpcHandle
				: client.Session.Account.ClientInstance;

			await _broadcaster.SendKnubotOpenChatWindow(client, npcHandle);
			await _broadcaster.SendKnubotAppendText(client, npcHandle, Dialogue.KnubotSampleLine.RemoteFormatXml);
			await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id,
				"Sent a constructed Knubot dialogue line (KnubotAppendTextIIR 0x4A3D899A).");
		}

		// .quest add|remove <id> | .quest list  - manage the player's quest journal. `add` records the quest +
		// sends QuestIIR so it appears (the client renders the quest text from its RDB by id); `remove` drops it;
		// `list` shows the player's active quests. Quest IDs are RDB quest ids (e.g. 13479). The full accept/branch
		// logic is the pinned RDB Lua quest VM - this is the state foundation (docs/archive/quests-and-combat.md).
		private async Task QuestCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var parts = (arguments ?? "")
				.Trim()
				.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			var sub = parts.Length > 0
				? parts[0]
					.ToLowerInvariant()
				: "list";

			if (sub == "list")
			{
				var quests = await Database.GetCharacterQuests(characterId);
				var active = quests
					.Where(q => q.State == QuestState.Active)
					.Select(q => q.QuestId)
					.ToList();
				await _agentServerDispatcher.SendSystemMessage(characterId, active.Count == 0
					? "No active quests."
					: $"Active quests: {string.Join(", ", active)}");
				return;
			}

			// .quest journal [<id>] - (re)send the seeded journal CONTENT (title/description/objective) for one quest
			// or every active quest with a definition. The J-panel renders this; QuestIIR/.quest add only drive state.
			if (sub == "journal")
			{
				var defs = new List<QuestDefinition>();
				if (parts.Length > 1 && uint.TryParse(parts[1], out var only))
				{
					var def = await Database.GetQuestDefinition(only);
					if (def != null)
						defs.Add(def);
				}
				else
				{
					foreach (var q in (await Database.GetCharacterQuests(characterId)).Where(q => q.State == QuestState.Active))
					{
						var def = await Database.GetQuestDefinition(q.QuestId);
						if (def != null)
							defs.Add(def);
					}
				}

				if (defs.Count == 0)
				{
					await _agentServerDispatcher.SendSystemMessage(characterId, "No seeded quest definition for the requested quest(s).");
					return;
				}

				foreach (var def in defs)
					await _broadcaster.SendQuestFullUpdate(client, def);
				await _broadcaster.SendQuestJournal(client, defs);
				await _agentServerDispatcher.SendSystemMessage(characterId, $"Sent journal content for {defs.Count} quest(s): {string.Join(", ", defs.Select(d => d.QuestId))}.");
				return;
			}

			if (parts.Length < 2 || !uint.TryParse(parts[1], out var questId))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .quest add|remove|complete <questId> | .quest journal [<id>] | .quest list");
				return;
			}

			switch (sub)
			{
				case "add":
				{
					// .quest add <id> [count] [target name...] - optional kill objective: kill <count> of <target>.
					var required = parts.Length > 2 && uint.TryParse(parts[2], out var c)
						? c
						: 0;
					var target = parts.Length > 3
						? string.Join(' ', parts.Skip(3))
						: null;
					await Database.SetCharacterQuest(characterId, questId, QuestState.Active, 0, required, target);
					await _broadcaster.SendQuestIir(client, questId, WorldQuest.StateActive);
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
				case "complete":
					// Mark a quest complete (test bootstrap for quest-gated dialogue, #37): persists State=Completed so
					// HasCompletedQuest(id) flips, which the conversation VM reads (e.g. Casilda's post-quest greeting).
					// (The wire "quest complete" journal update isn't decoded - only the persisted state, which the VM
					// reads, is needed for the dialogue flip; the kill-quest path persists the same way via CombatService.)
					await Database.SetCharacterQuest(characterId, questId, QuestState.Completed, 0, 0);
					await _agentServerDispatcher.SendSystemMessage(characterId, $"Quest {questId} marked complete (HasCompletedQuest flips; talk to the quest NPC to see post-quest dialogue).");
					break;
				default:
					await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .quest add|remove|complete <questId> | .quest list");
					break;
			}
		}

		// .save  - flush the live world state to the DB. The WorldEntity is the source of truth for live transform
		// (docs/archive/server-architecture-roadmap.md) and movement is not otherwise persisted, so this snapshots every
		// connected player's authoritative position/map back onto their persisted Character and saves it. (Extends to
		// more live state - vitals, buffs, … - as those become persisted.)
		private async Task SaveChanges(uint characterId)
		{
			var saved = await _persistence.FlushAllAsync();
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Flushed {saved} player(s)' live world state to the database.");
		}

		private Task SendGmHelpText(uint characterId)
		{
			return _agentServerDispatcher.SendSystemMessage(characterId, $"Available Commands:<br />{_gmCommandsDict.Keys.JoinAsString("<br />")}");
		}

		// .abilitylist  - list the grantable ability catalog (id / name / slot) for use with .grantability. These are
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
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Grantable abilities (.grantability &lt;id&gt;):<br />{lines}");
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

		// .world  - report the live world model around the caller: their map, the players/NPCs in that zone, and how
		// many entities are within interest range. A window into the shared simulation (handy for the 2-player test).
		private Task SendWorldInfo(INetworkClient client)
		{
			var entity = PlayerWorldEntity(client);
			if (entity == null)
				return _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, "World: you are not registered in the world model yet.");

			var zone = _simulation.World.ZoneForEntity(entity);
			var players = zone.Entities.Count(e => e.IsPlayer);
			var npcs = zone.Entities.Count(e => !e.IsPlayer);
			var nearby = zone
				.EntitiesInRange(entity.Position, WorldSimulation.PlayerInterestRange, entity.Handle)
				.Count();
			return _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id,
				$"World map {entity.MapId}: {players} player(s), {npcs} NPC(s) in zone; {nearby} entity(ies) within {WorldSimulation.PlayerInterestRange:0} of you.");
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

		// .level <n>  - set the player's character level, regenerate its MaxStats from the new class/level, persist,
		// and push the Level stat + refreshed vitals so the change shows without a relog (the persisted level also
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
			character.RegenerateMaxStats();
			await Database.SaveCharacter(character);

			// Push Level AND a refreshed XP bar (Exp/MaxExp) so a GM `.level` ding shows exactly like an XP-driven ding
			// (#209): the kill path (CombatService.PushLevelUp) sets the same three stats, so both level-up paths emit an
			// identical Level + AttributeChanged-burst + vitals max push.
			await SendStatUpdate(client, new CharacterStatBlock()
				.Set(LegacyCharacterStatId.Level, level)
				.Set(LegacyCharacterStatId.Exp, character.Experience)
				.Set(LegacyCharacterStatId.MaxExp, Faolan.GameServer.Combat.LevelingResolver.XpForNextLevel(level)));
			await SendVitals(client);
			// On an increase, emit the retail level-up attribute feedback (one AttributeChanged line per primary) so
			// the change shows the same as an XP-driven ding (#209); the vitals push above already raised the maxima.
			if (level > previousLevel)
				await SendLevelUpFeedback(client, level, (uint)(level - previousLevel));
			await _agentServerDispatcher.SendSystemMessage(character.Id, $"Level set to {level} (relog to refresh the full character sheet).");
		}

		// .learnspell <id>  - live-grant a real spell to the player via DispatchLearnSpell (0x200A), the decoded
		// spell-grant path (#73), without a relog. Distinct from .grantability, which grants a catalog ABILITY via the
		// 0x2052 ability-grant. <id> is the spell id (hex with 0x or decimal); e.g. ".learnspell 0x3006ff" - any
		// captured DispatchLearnSpell id (see Captures/scripts/decode_learn_spell.py).
		private async Task LearnSpellCommand(INetworkClient client, string arguments)
		{
			var character = client.Session.Character;
			var arg = (arguments ?? "").Trim();
			var ok = arg.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
				? uint.TryParse(arg.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var spellId)
				: uint.TryParse(arg, out spellId);
			if (!ok || spellId == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(character.Id, "Usage: .learnspell <id>  (hex 0x.. or decimal)");
				return;
			}

			await SendLearnSpell(client, spellId);
			// Persist so the spell rides GrantStartingSpells on the next login (#74), like a real learn.
			var added = await Database.AddCharacterSpell(character.Id, spellId);
			await _agentServerDispatcher.SendSystemMessage(character.Id, $"Learned spell 0x{spellId:X}.{(added ? "" : " (already known)")}");
		}

		// .tag add <id> | .tag list  - manage the player's quest-state tags (the flags RDB Lua scripts read/write).
		private async Task TagCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var parts = (arguments ?? "")
				.Trim()
				.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length >= 2 && parts[0]
				    .Equals("add", StringComparison.OrdinalIgnoreCase) && uint.TryParse(parts[1], out var tagId))
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

		private delegate Task GmCommandDelegate(INetworkClient client, string arguments);
	}
}
