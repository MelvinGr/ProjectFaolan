using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
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
				["teleport"] = (client, arguments) => DoTeleport(client.Session.Character.Id, uint.Parse(arguments)),
				["spelltest"] = (client, arguments) => ApplySpellTest(client.Session.Character.Id, uint.Parse(arguments)),
				["vitals"] = (client, arguments) => SetVitalsCommand(client, arguments),
				["setstat"] = (client, arguments) => SetStatCommand(client, arguments),
				["hurt"] = (client, arguments) => AdjustOwnHealthCommand(client, arguments, false),
				["heal"] = (client, arguments) => AdjustOwnHealthCommand(client, arguments, true),
				["mount"] = (client, arguments) => MountCommand(client, arguments),
				["spawnnpc"] = (client, arguments) => SpawnNpcCommand(client, arguments),
				["despawnnpc"] = (client, arguments) => DespawnNpcCommand(client, arguments),
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
				await Database.SaveCharacter(character);
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

			await Tradepost.TradepostResponses.SendOpenTradepost(client, entity.Position);

			// The window's first/default tab is the BANK (personal storage), a separate channel: container kind 105
			// delivered via a BankIIR (decoded, protocol_bank_storage). Populate it from the character's
			// Container=105 rows so the default tab isn't blank.
			var rows = await Database.GetCharacterInventory(characterId);
			var bankRows = rows
				.Where(r => r.Container == (int)FullCharSnapshotBuilder.BankKind)
				.ToList();
			var bank = FullCharSnapshotBuilder.BuildContainer(FullCharSnapshotBuilder.BankKind, 50, 30, characterId, bankRows);
			await SendBank(client, bank);

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
			await SendShopData(client, shop, entity?.Handle ?? 0);

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
			uint handle = 0;
			var parsed = arg.Length > 0;
			try
			{
				if (parsed)
				{
					handle = arg.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
						? Convert.ToUInt32(arg[2..], 16)
						: uint.Parse(arg);
				}
			}
			catch (FormatException)
			{
				parsed = false;
			}
			catch (OverflowException)
			{
				parsed = false;
			}

			if (!parsed)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .despawnnpc <handle> (0x-hex or decimal)");
				return;
			}

			var entity = await _broadcaster.DespawnEntityForPlayer(client, handle);
			await _agentServerDispatcher.SendSystemMessage(characterId, entity == null
				? $"No despawnable entity 0x{handle:X8} in this zone (unknown handle, or a player)."
				: $"Despawned {entity.Name} (0x{handle:X8}).");
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
				default:
					await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .quest add|remove <questId> | .quest list");
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
