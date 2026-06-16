using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
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
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .teleport &lt;mapId | name&gt;  or  .teleport &lt;x&gt; &lt;y&gt; &lt;z&gt;  (current map; see .maps)");
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

			if (uint.TryParse(arg, out var mapId))
			{
				await DoTeleport(characterId, mapId);
				return;
			}

			// Name match: exact (case-insensitive) wins; otherwise the first map whose name contains the argument.
			var maps = await Database.GetMaps();
			var match = maps.FirstOrDefault(m => string.Equals(m.Name, arg, StringComparison.OrdinalIgnoreCase))
				?? maps.FirstOrDefault(m => m.Name != null && m.Name.Contains(arg, StringComparison.OrdinalIgnoreCase));
			if (match == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, $"<span style=\"color: red;\">No map matching '{arg}'. Try .maps.</span>");
				return;
			}

			await DoTeleport(characterId, match.Id);
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

		// The change-map destination-announce IIR bodies, captured from the changemap dumps (frames 4200/4212)
		// with the session values substituted: client instance (@8), destination map (@18 / @46), and the spawn
		// position (@91/95/99, big-endian floats). The remaining bytes - constants plus one undecoded instance
		// word (0x00503570 in the capture) - are kept verbatim; in-client verification pending.
		private async Task SendChangeMapPrep(INetworkClient client, uint destMapId, Vector3 position)
		{
			var instance = client.Session.Account.ClientInstance;
			foreach (var body in new[] { BuildTeleportBeginBody(instance, destMapId), BuildTeleportDestBody(instance, destMapId, position) })
				await new PacketStream()
					.WriteHeader(CreateEditorMorphAckSender, CreateEditorReceiver(client), GameServerCarrierOpcodes.IirCarrier)
					.WriteArrayUInt32Length(s => s.WriteArray(body))
					.SendAsync(client);
		}

		// 0x10D27BC0 "begin teleport to map N": [subop][ack][clientInst@8][01][62][0000c79c][destMap@18][zeros].
		// Template = the changemap capture (frame 4200) IIR body; only the session values are substituted.
		internal static byte[] BuildTeleportBeginBody(uint clientInstance, uint destMapId)
		{
			var body = Convert.FromHexString(
				"10d27bc00000c35008006f3101620000c79c000007da0000000000000000000000000000000000000000000000000000");
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8, 4), clientInstance);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(18, 4), destMapId);
			return body;
		}

		// 0x5A32F0D7 destination announce: destination map (@46) + spawn position (@91/95/99, big-endian floats).
		// Template = the changemap capture (frame 4212) IIR body; the constants + the undecoded instance word
		// (0x00503570) are kept verbatim. In-client verification pending.
		internal static byte[] BuildTeleportDestBody(uint clientInstance, uint destMapId, Vector3 position)
		{
			var body = Convert.FromHexString(
				"5a32f0d70000c35008006f31000000000000000000000000000000000000000000000000003f800000620000c79c000007da0000000000000000000000000000000100009c500050357000000000000000000000000014000187044468000042e80000445980000000001400");
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8, 4), clientInstance);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(46, 4), destMapId);
			BinaryPrimitives.WriteSingleBigEndian(body.AsSpan(91, 4), position.X);
			BinaryPrimitives.WriteSingleBigEndian(body.AsSpan(95, 4), position.Y);
			BinaryPrimitives.WriteSingleBigEndian(body.AsSpan(99, 4), position.Z);
			return body;
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
				["teleport"] = (client, arguments) => TeleportCommand(client, arguments),
				["position"] = (client, _) => PositionCommand(client),
				["bind"] = (client, _) => BindCommand(client),
				["recall"] = (client, _) => RecallToBindCommand(client),
				["spelltest"] = (client, arguments) => ApplySpellTest(client.Session.Character.Id, uint.Parse(arguments)),
				["vitals"] = (client, arguments) => SetVitalsCommand(client, arguments),
				["setstat"] = (client, arguments) => SetStatCommand(client, arguments),
				["level"] = (client, arguments) => SetLevelCommand(client, arguments),
				["hurt"] = (client, arguments) => AdjustOwnHealthCommand(client, arguments, false),
				["heal"] = (client, arguments) => AdjustOwnHealthCommand(client, arguments, true),
				["mount"] = (client, arguments) => MountCommand(client, arguments),
				["spawnnpc"] = (client, arguments) => SpawnNpcCommand(client, arguments),
				["despawnnpc"] = (client, arguments) => DespawnNpcCommand(client, arguments),
				["kill"] = (client, arguments) => KillCommand(client, arguments),
				["additem"] = (client, arguments) => AddItemCommand(client, arguments),
				["removeitem"] = (client, arguments) => RemoveItemCommand(client, arguments),
				["feedback"] = (client, _) => FeedbackCommand(client),
				["world"] = (client, _) => SendWorldInfo(client),
				["quest"] = (client, arguments) => QuestCommand(client, arguments),
				["tag"] = (client, arguments) => TagCommand(client, arguments),
				["lua"] = (client, arguments) => LuaCommand(client, arguments),
				["ah"] = (client, _) => OpenAuctionHouseCommand(client),
			["shop"] = (client, arguments) => OpenShopCommand(client, arguments),
				["guild"] = (client, arguments) => GuildCommand(client, arguments),
				["money"] = (client, arguments) => MoneyCommand(client, arguments),
				["buffs"] = (client, _) => BuffsCommand(client),
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
			var saved = await FlushWorldState();
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Flushed {saved} player(s)' live world state to the database.");
		}

		// Flushes one player's authoritative live transform (the WorldEntity) onto their persisted Character and
		// saves it. Used by the autosave loop, .save, and disconnect. Errors are logged (callers may not await).
		private async Task<bool> SavePlayerState(INetworkClient client, WorldEntity entity)
		{
			var character = client.Session?.Character;
			if (character == null)
				return false;

			try
			{
				// entity is the live WorldEntity (Vector3 Euler rotation); the DB Character stores a quaternion.
				character.MapId = entity.MapId;
				character.Position = entity.Position;
				character.Rotation = entity.Rotation.ToQuaternion();
				await Database.SaveCharacterTransform(character.Id, entity.Position, entity.Rotation.ToQuaternion(), entity.MapId);
				return true;
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex, "Failed to flush world state for {Name} (0x{Handle:X8}).", entity.Name, entity.Handle);
				return false;
			}
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

			character.Level = level;
			character.RegenerateMaxStats();
			await Database.SaveCharacter(character);

			await SendStatUpdate(client, new CharacterStatBlock().Set(LegacyCharacterStatId.Level, level));
			await SendVitals(client);
			await _agentServerDispatcher.SendSystemMessage(character.Id, $"Level set to {level} (relog to refresh the full character sheet).");
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
