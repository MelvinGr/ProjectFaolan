using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Guild;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Progression;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Economy;
using Faolan.GameServer.Inventory;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;
using ProtoBuf;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// The backpack's slot count: base 24 + the equipped bag's Item.BagExtraSlots (equip slot 3 / Bag) - the same
		// sizing the snapshot uses (FullCharacterRoots), so a grown bag can actually be filled. #12.
		private const uint BaseBackpackSlots = 24;

		Task<int?> IEconomyOutbound.GrantItemToBackpack(INetworkClient client, uint itemTemplateId, uint count, bool allowHashless, int? preferredSlot)
		{
			return GrantItemToBackpack(client, itemTemplateId, count, allowHashless: allowHashless, preferredSlot: preferredSlot);
		}

		Task IEconomyOutbound.SendAddItem(INetworkClient client, ContainerKind containerType, uint itemPos, ProtoInventoryGameEntry entry, uint feedback)
		{
			return SendAddItem(client, containerType, itemPos, entry, feedback);
		}

		// --- IEconomyOutbound: the wallet + item-grant ops the economy/inventory services reach back to (forwarding
		// to the private Outbound builders, which the listener also calls directly from loot/GM paths) ---------------
		Task IEconomyOutbound.SendUpdateCash(INetworkClient client, ulong balanceTin, UpdateCashReason reason)
		{
			return SendUpdateCash(client, balanceTin, reason);
		}

		Task<uint> IEconomyOutbound.TakeItemsFromInventory(INetworkClient client, uint itemTemplateId, uint count)
		{
			return TakeItemsFromInventory(client, itemTemplateId, count);
		}

		// IGameServerProvider seam: pushes the guild tag onto the character's live world connection, if it is
		// connected to this listener. Used by the CsPlayerAgent guild flow (accept/create/leave) so the in-world
		// tag updates without a relog.
		public async Task<bool> SendGuildTag(uint characterId, string guildName)
		{
			var client = ClientForCharacter(characterId);
			if (client?.Session.Account == null)
				return false;

			await client.SendAsync(BuildGuildTag(client, characterId, guildName ?? ""));
			Logger.LogInformation("SendGuildTag: set in-world guild tag of {CharacterId:X8} to \"{GuildName}\".", characterId, guildName);
			return true;
		}

		// IGameServerProvider seam: the world-connection half of a team change. TeamService owns the roster (it spans
		// the CS tier and this one); this only puts the record on the wire for a member connected HERE.
		public async Task<bool> SendGroupMembership(uint recipientId, uint memberId, uint teamId, uint leaderId, bool joined)
		{
			var client = ClientForCharacter(recipientId);
			if (client?.Session.Account == null)
				return false;

			await WorldIir.Send(client, GroupMembershipCodec.Build(joined
				? new AddToGroupIIR(memberId, teamId, leaderId)
				: new RemoveFromGroupIIR(memberId, teamId)));
			return true;
		}

		// --- ITradeOutbound: the wire side of a player trade. TradeService owns the state machine and reaches the wire
		// only through this port, addressed by character id because a trade always spans two connections. ---------------
		Task ITradeOutbound.SendTradeRequest(uint targetId, uint requesterId)
		{
			return SendTradeRpc(targetId, GameCharInterfaceOpcodes.DispatchRequestTrade, EntityIdCodec.Build(EntityIds.Character(requesterId)));
		}

		Task ITradeOutbound.SendTradeStarted(uint recipientId, uint partnerId)
		{
			return SendTradeRpc(recipientId, GameCharInterfaceOpcodes.DispatchStartTrade, EntityIdCodec.Build(EntityIds.Character(partnerId)));
		}

		Task ITradeOutbound.SendPartnerCash(uint recipientId, ulong tin)
		{
			return SendTradeRpc(recipientId, GameCharInterfaceOpcodes.DispatchPartnerUpdatedCash, TradeCashCodec.Build(new TradeCash(tin)));
		}

		// The same body either way; only the opcode says whose side of the table the item is on.
		Task ITradeOutbound.SendTradeItem(uint recipientId, Item item, uint slot, uint count, bool mine)
		{
			var entry = SnapshotItems.InventoryEntry(FullCharacterRoots.ItemId(recipientId, ContainerKind.Bag, slot), item, 0,
			[
				new SnapshotItems.ItemStat(0, 1), new SnapshotItems.ItemStat(23, (uint)FullCharacterRoots.RecordId(recipientId, ContainerKind.Bag, slot)),
				new SnapshotItems.ItemStat(26, count)
			], item.Level);

			return SendTradeRpc(recipientId, mine
				? GameCharInterfaceOpcodes.DispatchTradeAddedItem
				: GameCharInterfaceOpcodes.DispatchTradePartnerAddedItem, TradeAddedItemCodec.Build(new TradeAddedItem(entry, count)));
		}

		Task ITradeOutbound.SendAcceptAck(uint recipientId)
		{
			// Unnamed in the client's recovered catalog, so it keeps the honest Reserved name; the capture shows its role
			// unambiguously - it lands on the accepter's own connection, empty, right after their AcceptTrade.
			return SendTradeRpc(recipientId, GameCharInterfaceOpcodes.Reserved0x206B, []);
		}

		Task ITradeOutbound.SendPartnerAccepted(uint recipientId, uint partnerId)
		{
			return SendTradeRpc(recipientId, GameCharInterfaceOpcodes.DispatchPartnerAcceptedTrade, EntityIdCodec.Build(EntityIds.Character(partnerId)));
		}

		// The empty 0x206D then the toast - retail sends both, in that order, to each party.
		async Task ITradeOutbound.SendTradeCompleted(uint recipientId)
		{
			// Also unnamed in the catalog. It lands on BOTH connections, empty, as the trade commits.
			await SendTradeRpc(recipientId, GameCharInterfaceOpcodes.Reserved0x206D, []);
			if (ClientForCharacter(recipientId) is { } client)
				await SendFeedback(client, new FeedbackLiteral(FeedbackXml.TradeCompletedSuccessfully));
		}

		Task ITradeOutbound.SendWalletUpdate(uint characterId, ulong balanceTin)
		{
			return ClientForCharacter(characterId) is { } client
				? SendUpdateCash(client, balanceTin, UpdateCashReason.Spend) // a sync, not a celebrated gain - either side may be down money
				
				: Task.CompletedTask;
		}

		Task ITradeOutbound.SendItemRemoved(uint characterId, uint slot)
		{
			return ClientForCharacter(characterId) is { } client
				? SendRemoveItem(client, ContainerKind.Bag, slot)
				: Task.CompletedTask;
		}

		Task ITradeOutbound.SendItemGranted(uint characterId, Item item, uint slot, uint count)
		{
			if (ClientForCharacter(characterId) is not { } client)
				return Task.CompletedTask;

			var entry = SnapshotItems.InventoryEntry(FullCharacterRoots.ItemId(characterId, ContainerKind.Bag, slot), item, 0,
			[
				new SnapshotItems.ItemStat(0, 1), new SnapshotItems.ItemStat(23, (uint)FullCharacterRoots.RecordId(characterId, ContainerKind.Bag, slot)),
				new SnapshotItems.ItemStat(26, count)
			], item.Level);

			return SendAddItem(client, ContainerKind.Bag, slot, entry);
		}

		// One place the trade replies are addressed from, so the endpoints cannot drift between them. Silently no-ops for
		// a character who is not connected here - a partner who vanished mid-trade is TradeService's problem, not the
		// wire's.
		private Task SendTradeRpc(uint characterId, GameCharInterfaceOpcodes opcode, byte[] body)
		{
			return ClientForCharacter(characterId) is { } client
				? client.SendAsync(new Rpc(Endpoints.Endpoint(Endpoints.GameCharAgentType, 0x10), client.Endpoint(Endpoints.GameCharInterfaceType), opcode, (byte[])[0x01], body))
				: Task.CompletedTask;
		}

		private async Task<int> BackpackCapacity(uint characterId)
		{
			var equipment = await Database.GetCharacterEquipment(characterId);
			return (int)(BaseBackpackSlots + (equipment?.Bag?.BagExtraSlots ?? 0));
		}

		// The first free backpack cell, skipping persisted rows + the legacy starter-bag cell while it renders. The
		// backpack size is 24 base + the equipped bag's extra slots (#12), so a grown bag can be filled. Null = full.
		// Read-only DB helper (mirrors AddItemBuilder.FindFreeBagSlot - kept local to avoid a 4th seam).
		private async Task<int?> FindFreeBagSlot(uint characterId)
		{
			var equipment = await Database.GetCharacterEquipment(characterId);
			var rows = await Database.GetCharacterInventory(characterId);
			var taken = rows.Where(r => r.Container == ContainerKind.Bag).Select(r => r.Slot).ToHashSet();
			if (equipment?.FirstPosBag is { Hash: not 0 })
				taken.Add(1);
			var capacity = (int)(BaseBackpackSlots + (equipment?.Bag?.BagExtraSlots ?? 0));
			return Enumerable.Range(0, capacity).Where(s => !taken.Contains(s)).Cast<int?>().FirstOrDefault();
		}

		// The first free cell (0..capacity-1) in `container`, skipping persisted rows.
		// `preferred` is the slot the player DROPPED the item on (the loot window's drag target, #462).
		//
		// An OCCUPIED preferred slot falls through to the first free one, and that is a game rule rather than a
		// fallback: the player really did drop it there, the bag has already handed the item over, and neither losing
		// the item nor overwriting what is in the slot is a defensible answer.
		//
		// An OUT-OF-RANGE one is a different thing entirely - the client cannot drop an item outside the backpack, so
		// it means we misread the wire - and it THROWS. Quietly treating it as "first free" would make a
		// mis-parsed body look like a working loot.
		private async Task<int?> FindFreeContainerSlot(uint characterId, ContainerKind container, int capacity, int? preferred = null)
		{
			if (preferred is { } slot && (slot < 0 || slot >= capacity))
				throw new ArgumentOutOfRangeException(nameof(preferred), slot, $"a requested {container} slot outside 0..{capacity - 1} cannot come from the client - the body was misread.");

			var rows = await Database.GetCharacterInventory(characterId);
			var taken = rows.Where(r => r.Container == container).Select(r => r.Slot).ToHashSet();
			if (preferred is { } wanted && !taken.Contains(wanted))
				return wanted;

			return Enumerable.Range(0, capacity).Where(s => !taken.Contains(s)).Cast<int?>().FirstOrDefault();
		}

		// Grants an item to the player's backpack: persists it to a free CharacterInventory bag cell (so it
		// survives relog via the world-entry snapshot) AND pushes the live DispatchAddItem so it appears
		// immediately. The entry is the proven snapshot entry (SnapshotItems.ListEntry().Item - the
		// 3-stat form that renders in-client), and itemId/recordId use FullCharacterRoots's deterministic
		// formula so the live add and the next login's snapshot agree on the entry's identity. Returns the bag
		// slot it landed in, or null when the item isn't snapshot-buildable or the bag is full.
		// The live add is UNCONDITIONAL, loot included. This used to carry a `pushLiveAdd` flag that loot passed false,
		// on the claim that "retail sends NO inventory-add for normal loot - the client moves it from the bag locally".
		// The captures say the opposite: both a loot-all (Hoxxling gameplay, c2s 0x2043 at fr1487) and a single drag
		// (loot-drag-1538, c2s 0x2046 at fr619) are answered with three 0x201c DispatchSetItemStat and a 0x2020
		// DispatchAddItem. Our own client agrees - persist-only left the items un-takeable (see LootService). The flag
		// went dead when loot moved onto the IEconomyOutbound port, which has no such parameter; removing it here makes
		// the one live behaviour explicit instead of leaving a false claim next to an unused knob.
		// `preferredSlot` is honoured only for the backpack: a quest item routes to its own container, where a bag
		// slot index means nothing.
		private async Task<int?> GrantItemToBackpack(INetworkClient client, uint itemTemplateId, uint count = 1, bool allowHashless = false, int? preferredSlot = null)
		{
			var item = await Database.GetItem(itemTemplateId);
			if (item == null)
				return null;

			var characterId = client.Session.Character.Id;

			// Quest items (Type 113, e.g. the Scavenger's Key) route to the QUEST container, not the backpack, so they
			// land in the player's quest-items tab (#261). They are kept even with hash 0 (quest items often have
			// none); normal bag items still require an hash so the snapshot can render them - EXCEPT when allowHashless
			// (claim redeems): a claim list carries the item's key but no hash, so the item delivers live (the 0x2020
			// path renders by template without a hash) but won't survive a relog until we harvest its hash. See #claims.
			var isQuestItem = item.Type == ItemType.Quest;
			if (!isQuestItem && item.Hash == 0 && !allowHashless)
				return null;

			var kind = isQuestItem
				? ContainerKind.Quest
				: ContainerKind.Bag;
			var capacity = isQuestItem
				? 48 // quest tab holds 48 (NewContainer(QuestKind, 48))
				: await BackpackCapacity(characterId); // backpack = 24 base + the equipped bag's extra slots
			var slot = await FindFreeContainerSlot(characterId, kind, capacity, isQuestItem
				? null
				: preferredSlot);
			if (slot == null)
				return null;

			await Database.SetCharacterInventorySlot(characterId, kind, slot.Value, itemTemplateId, count);

			// Live DispatchAddItem so the item appears without a relog. Quest items go to the QUEST container (0x71), 			// matching retail's chain-capture delivery; everything else to the backpack (0x68).
			var containerType = isQuestItem
				? ContainerKind.Quest
				: ContainerKind.Bag;
			var entry = SnapshotItems.ListEntry((ulong)slot.Value, FullCharacterRoots.ItemId(characterId, kind, (ulong)slot.Value),
					FullCharacterRoots.RecordId(characterId, kind, (ulong)slot.Value), count, item, item.Hash)
				.Item;
			await SendAddItem(client, containerType, (uint)slot.Value, entry);

			// The item is now in the player's inventory, so any collect objective asking for it ticks - HOWEVER it got
			// here (quest-pickup node, corpse bag, vendor, harvest, claim, .additem). This is the one choke point every
			// grant passes through, which is why the hook lives here rather than in each caller.
			await _questProgression.AdvanceCollectGoals(client, itemTemplateId, count);
			return slot;
		}

		// Pushes the s2c UpdateInventoryIIR (0xA74D2814) for the player's OWN Equipped(101) and Bag(104) containers,
		// in full - retail's "here is your inventory again" after the SERVER changes it behind the player's back (#380).
		//
		// Decoded from Hoxxling_mage-heraldofxotli_login_gameplay2, where the Slave Manacles come off:
		//   f5109 c2s ScryScriptIIR "ShacklesOff"
		//   f5115 s2c DispatchRemoveItem   [container 101][char][pos 0x12][amount 0]
		//   f5118 s2c UpdateInventoryIIR   kind=101 Equipped cap=1028   (522 B)
		//   f5118 s2c UpdateInventoryIIR   kind=104 Bag      cap=24     (1141 B)
		// i.e. a server-initiated removal followed by BOTH containers whole, in the same frame. Note it is NOT the
		// answer to the c2s probe of the same opcode - retail answers those never (34 requests, 0 replies).
		//
		// The payload is plain ProtoInventoryGame, the same schema the login snapshot carries, so it is built by the
		// SAME BuildContainers call rather than a second inventory serialiser - capacities included (Equipped 1028,
		// Bag 24 + the equipped bag's extra slots), which is what the capture shows.
		private async Task SendUpdateInventory(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			var equipment = await Database.GetCharacterEquipment(characterId);
			if (equipment == null)
				return;

			var equipped = new Dictionary<ulong, (Item Item, uint Hash)>();
			foreach (var slot in InventoryEquippedSlots)
			{
				if (equipment[slot] is { Hash: not 0 } item)
					equipped[(ulong)slot] = (item, item.Hash);
			}

			(Item, uint)? bagFirst = equipment.FirstPosBag is { Hash: not 0 }
				? (equipment.FirstPosBag, equipment.FirstPosBag.Hash)
				: null;

			var containers = FullCharacterRoots.Containers(characterId, equipped, bagFirst, await Database.GetCharacterInventory(characterId));

			// containers[0] = Equipped(101), [1] = Bag(104) - the two retail pushes. The Resource and Quest containers
			// it also builds are NOT pushed: the capture carries exactly these two.
			foreach (var container in containers.Take(2))
			{
				using var ms = new MemoryStream();
				Serializer.Serialize(ms, container);
				var payload = ms.ToArray();

				await client.SendAsync(new Rpc(WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob,
					s => s.WriteArrayUInt32Length(UpdateInventoryCodec.Build(new UpdateInventoryIIR(characterId, 0x00, payload)))));
			}

			Logger.LogDebug("Pushed UpdateInventoryIIR for {Character}: Equipped + Bag (#380).", client.Session.Character.Name);
		}

		// Removes an item from the player's backpack: clears the persisted CharacterInventory bag cell (so it stays
		// gone across relog) AND pushes the live DispatchRemoveItem so it disappears immediately. The mirror of
		// GrantItemToBackpack. Returns true if a row was cleared.
		private async Task<bool> RemoveItemFromBackpack(INetworkClient client, int slot, uint amountToRemove = 0)
		{
			var characterId = client.Session.Character.Id;
			var rows = await Database.GetCharacterInventory(characterId);
			var present = false;
			foreach (var r in rows)
			{
				if (r.Container == ContainerKind.Bag && r.Slot == slot)
					present = true;
			}

			if (!present)
				return false;

			await Database.SetCharacterInventorySlot(characterId, ContainerKind.Bag, slot, null);
			await SendRemoveItem(client, ContainerKind.Bag, (uint)slot, amountToRemove);

			// Retail follows a SERVER-initiated removal with both containers in full (#380). This path is exactly that
			// case - the server took something out of the bag, the player did not move it - which is the shape the
			// ShacklesOff capture shows. A player-initiated move is NOT followed by one: the client already knows.
			await SendUpdateInventory(client);
			return true;
		}

		// Takes `count` of an item template OUT of the player's inventory and returns how many were actually removed
		// (IEconomyOutbound). The inverse of GrantItemToBackpack, used by a quest HAND-IN: delivering three stone bricks
		// to Turach takes the three bricks.
		//
		// Deliberately NOT backpack-only, unlike RemoveItemFromBackpack. A quest item lives in the Quest container
		// (0x71), so a bag-only walk would report success while removing nothing - and the delivery check that gates
		// this sums the same way, across every container.
		//
		// Removes EXACTLY `count`: a slot holding more than is still wanted is DECREMENTED, never cleared, so handing
		// in 3 of 5 bricks leaves 2. Slots are taken in order and a partial take is reported rather than silently
		// tolerated - the caller has already verified the player holds enough, so a short result means the inventory
		// changed underneath us and is worth a log line, not a shrug.
		private async Task<uint> TakeItemsFromInventory(INetworkClient client, uint itemTemplateId, uint count)
		{
			if (count == 0)
				return 0;

			var characterId = client.Session.Character.Id;
			var steps = InventoryTake.Plan(await Database.GetCharacterInventory(characterId), itemTemplateId, count);

			foreach (var step in steps)
			{
				if (step.Remaining == 0)
					await Database.SetCharacterInventorySlot(characterId, step.Container, step.Slot, null);
				else
					await Database.SetCharacterInventorySlot(characterId, step.Container, step.Slot, itemTemplateId, step.Remaining);

				await SendRemoveItem(client, step.Container, (uint)step.Slot, step.Taken);
			}

			var removed = InventoryTake.Total(steps);
			if (removed > 0)
			{
				// Same follow-up as RemoveItemFromBackpack, and for the same reason: this is a SERVER-initiated removal,
				// which retail follows with both containers in full (#380).
				await SendUpdateInventory(client);
			}

			return removed;
		}

		// Answers a TradeskillRequestIIR (recipe-book open) with the TradeskillReplyIIR (0x9062D559) recipe list built
		// from the Recipe catalog, so the Crafting -> Recipes window populates. The carrier is the standard world IIR
		// framing (GameAgent 0x58 -> GameInterface <instance>), body = [subop][0xC350][handle][reply], byte-exact vs
		// the capture (docs/subsystems/tradeskill-recipes.md). Empty catalog -> an empty (count=0x3F1) list, which opens the book
		// cleanly with no recipes.
		private async Task SendCraftingRecipeBook(INetworkClient client)
		{
			var handle = client.Session.Character.Id;
			var rows = await Database.GetRecipes();
			var records = rows.Select(RecipeRecords.FromRow).ToList();

			await client.SendAsync(new Rpc(WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(TradeskillReplyCodec.Build(handle, records))));

			Logger.LogInformation("Sent crafting recipe book: {Count} recipe(s) to client {Handle} (TradeskillReplyIIR).", records.Count, handle);
		}

		// Lists maps for `.maps`, optionally filtered to those whose name CONTAINS `search` (case-insensitive) -
		// e.g. `.maps castle`. A blank search lists all maps.
		private async Task SendListAllMaps(uint characterId, string? search = null)
		{
			var maps = (await Database.GetMaps()).OrderBy(m => m.Name).AsEnumerable();
			if (!string.IsNullOrWhiteSpace(search))
				maps = maps.Where(m => m.Name != null && m.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));

			var matches = maps.ToList();
			if (matches.Count == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Color("red", $"No map matching '{search?.Trim()}'."));
				return;
			}

			await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Lines([.. matches.Select(ChatMarkup (m) => $"{m.Id}: {m.Name}")]));
		}

		// Pushes the character's wallet balance, framed on the same login-world conversation as the 0x201C
		// record stream (the retail money updates ride that conversation).
		private async Task SendUpdateCash(INetworkClient client, ulong balanceTin, UpdateCashReason reason)
		{
			await client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(Endpoints.GameCharInterfaceType), GameCharInterfaceOpcodes.DispatchUpdateCash,
				UpdateCashCodec.Build(new UpdateCash(balanceTin, reason))));

			Logger.LogInformation("UpdateCash: {Balance} tin ({Reason}) -> {Character}.", balanceTin, reason, client.Session.Character?.Name);
		}

		// 0x4F57DC08 GameTime - the live world clock (#351). Both fields are generated: the resync anchor from the wall
		// clock and the time of day from it at 5 game seconds per real second, wrapping every 86400 (see GameTimeCodec
		// for where those come from). `_gameTimeOffset` shifts the phase for the `.time` GM command.
		internal Rpc BuildGameTime(INetworkClient client)
		{
			return BuildGameTime(client, DateTimeOffset.UtcNow, _gameTimeOffset);
		}

		// `now` and the offset are parameters so a golden test can feed the capturing instant and the captured dayTime
		// and get the captured bytes back.
		internal static Rpc BuildGameTime(INetworkClient client, DateTimeOffset now, long offsetGameSeconds)
		{
			return BuildGameTime(client, GameClock.TimeOfDay(now, offsetGameSeconds), now);
		}

		internal static Rpc BuildGameTime(INetworkClient client, float dayTimeSeconds, DateTimeOffset now)
		{
			return new Rpc(WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob, s => s.WriteArrayUInt32Length(GameTimeCodec.Build(dayTimeSeconds, now)));
		}

		// Frames the GuildUpdateIIR_c (GuildUpdateCodec) on the world conversation: [RpcHeader][DispatchPassBlob]
		// [u32 len][GuildUpdateIIR]. Sent on login for guilded characters and pushed live by the CsPlayerAgent
		// guild-accept flow through the dispatcher seam. The body grammar + round-trip live on GuildUpdateCodec.
		internal static Rpc BuildGuildTag(INetworkClient client, uint characterId, string guildName)
		{
			return new Rpc(WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(GuildUpdateCodec.Build(characterId, guildName)));
		}

		// 0x5D85BFC7 MapChange = the PlayfieldFIIR: tells the client which playfield/instance the player is entering.
		// This is now a thin wrapper over the shared CreateEditorRecordBuilder.BuildPlayfieldFiir codec (the create-flow
		// builds the SAME packet) - it just prepends the MapChange subop and sources the PER-MAP rule fields from the Map
		// row (#308). The client keys per-area rules on this packet (mount permission = PlayfieldFlags); those fields are
		// NOT universal (beach vs mainland differ), so they come from the DB (Map.PlayfieldCount/FieldB/Flags/Instanced), 		// defaulting to the beach values for un-sourced maps. Position is the character's entry transform (per-session);
		// nearClip 15.0 + the farClip float are camera params (left constant). `map` may be null (fresh/unmapped -> beach
		// defaults).
		// The word at body [55:59] (client obj+0x54). We send 1 for every map, which is what 97 of the 151 captured
		// records carry; the other 54 carry 0, and the split is not per-map (4000 and 4010 carry both). Was written
		// as 0x01000000 while the builder's framing was a byte out - see BuildPlayfieldFiir (#451). [V0]
		private const uint SceneWord = 1u;

		internal static Rpc BuildMapChange(INetworkClient client, Map map, uint playfieldToken)
		{
			var character = client.Session.Character;
			var charInstance = map?.PlayfieldInstanced ?? true
				? character.Id
				: 0u;
			var count = map?.PlayfieldCount ?? 2u;
			var fieldB = map?.PlayfieldFieldB ?? 3u;
			var flags = map?.PlayfieldFlags ?? 0u;
			// #451: the two per-map words that used to be builder constants. The `??` defaults are those constants, so
			// a map with no imported MapChange behaves exactly as before rather than silently getting a zero.
			var fieldA = map?.PlayfieldFieldA ?? 0u;
			var fieldC = map?.PlayfieldFieldC ?? 7u;

			return new Rpc(WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(PlayfieldFCodec.Build(new Vector3(character.PositionX, character.PositionY, character.PositionZ), character.MapId,
					charInstance, count, playfieldToken, 15.0f, BitConverter.UInt32BitsToSingle(0x40E3944Fu), flags, SceneWord, fieldB, fieldA, fieldC)));
		}

		// 0x96B8DC59 - entity vitals / stat-update packet. The wire builder now lives on World.VitalsPacket (shared
		// with the CombatService); this delegates so the existing SendStatUpdate/SendVitals/BuildVitals callers
		// (login replay, GM, the regen tick) are unchanged.
		internal static Rpc BuildStatUpdate(INetworkClient client, CharacterStatBlock stats, uint entityHandle)
		{
			return VitalsPacket.BuildStatUpdate(client, stats, entityHandle);
		}

		// Builds the player's own vitals (0x96B8DC59) from the live character's MaxStats - the world-entry replay
		// emits this instead of replaying the captured player's vitals.
		internal static Rpc BuildVitals(INetworkClient client)
		{
			var character = client.Session.Character;
			var stats = character.MaxStats ?? CharacterMaxStats.CreateDefaults(character.Class, character.Level);
			// Emit the full value under the CURRENT ids (0x001B/0x01F8/0x01FA) - the retail full-spawn announce sends
			// current (== max at full health); the max ids ride the snapshot/tails. (in-client proof: CapturedCharacterStats.)
			// Also carry Level + Exp/MaxExp so the XP bar reflects the character's REAL progress: the snapshot tails
			// carry the captured template's stale to-next-level max, so overriding it here (the same Exp/MaxExp the
			// combat XP path sends) scales the bar correctly for this character's level.
			// #214: a dead character must NOT get its current health refilled here - the world-entry snapshot just set
			// HealthCurrent = 0 to latch the client's dead state, and this post-snapshot vitals update would otherwise
			// refill the bar (and un-latch the release window). Send 0 current health while dead.
			var currentHealth = character.IsDead
				? 0u
				: stats.Health;
			var block = new CharacterStatBlock()
				.Set(CharacterStatId.HealthCurrent, currentHealth)
				.Set(CharacterStatId.StaminaCurrent, stats.Stamina)
				.Set(CharacterStatId.ManaCurrent, stats.Mana)
				.Set(CharacterStatId.Level, character.Level)
				// The client DIVIDES BY this when it normalises armour mitigation by level, so leaving it unset (it
				// defaults to 1) makes `2.0 - Level/ApprenticeLevel` = -78 for a level-80 character and the whole
				// computation goes negative - measured in the client's own trace as
				// `Mitigation Modification:-1532.57` and a table lookup that missed every row. An un-mentored
				// character's apprentice level IS its level, which makes the term 1.0. See CharacterStatId.
				.Set(CharacterStatId.ApprenticeLevel, character.Level)
				.Set(CharacterStatId.Exp, character.Experience)
				.Set(CharacterStatId.MaxExp, LevelingResolver.XpForNextLevel(character.Level));

			return BuildStatUpdate(client, block, client.Session.Character.Id);
		}

		// Assembles the complete world-entry snapshot packet for `client` from a pre-built root + tails. The
		// RECORD ([IirType=0x737A6DF9][TypeTag][Handle=instance][blob frame][root][tails]) is built by
		// FullPlayerCharacterCodec, which owns the blob frame next to the reader that parses it - it used to be
		// stated here as well, and the two drifted (see that type's header). The carrier's u32 length is written
		// HERE, because it belongs to the sender (#530 rule 2), together with the RpcHeader + outer length/CRC. No
		// captured packet is consulted.
		internal static Rpc BuildWorldEntrySnapshot(INetworkClient client, ProtoFullCharacterIIR root, FullCharSnapshotSubClass tails)
		{
			var record = FullPlayerCharacterCodec.Build(new FullPlayerCharacterIIR(client.Session.Character.Id, root, tails));

			using var body = new FaolanStream();
			body.WriteArrayUInt32Length(record);
			return FrameWorldEntryRecord(client, body.ToArray());
		}

		// Assembles the complete world-entry player/NPC FA015807 spawn packet for `client` from a pre-built spawn
		// RECORD ([IirType=0xFA015807][TypeTag][Handle][FieldA][transform + pre-IIR + CharacterIIR + post-IIR]) - the
		// form SimpleCharFullUpdateCodec builds. The carrier's u32 length is written HERE, because it belongs to the
		// sender (#530 rule 2), which is what lets a from-scratch record reach the wire without a captured header.
		internal static Rpc BuildWorldEntrySpawn(INetworkClient client, byte[] spawnRecord)
		{
			ArgumentNullException.ThrowIfNull(spawnRecord);
			return new Rpc(WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob, s => s.WriteArrayUInt32Length(spawnRecord));
		}

		// Prepends the shared world-entry RpcHeader + outer length/CRC (the WriteHeader/WriteLengthHash path) to a
		// record body that ALREADY carries its own [u32 bodyLen] prefix - i.e. the `packet[bodyStart..]` form that
		// SpawnPacketBuilder and WriteArrayUInt32Length produce. This is the keystone that lets a
		// from-scratch record body reach the wire without a captured packet header.
		internal static Rpc FrameWorldEntryRecord(INetworkClient client, params ReadOnlySpan<byte> bodyWithLenPrefix)
		{
			return new Rpc(WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob, bodyWithLenPrefix.ToArray());
		}

		// Pushes one item into the client's inventory live, on the same GameCharAgent conversation the wallet
		// updates (0x2005) ride.
		private static async Task SendAddItem(INetworkClient client, ContainerKind containerType, uint itemPos, ProtoInventoryGameEntry entry, uint feedback = 0)
		{
			var charId = client.Session.Character.Id;

			await client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(Endpoints.GameCharInterfaceType), GameCharInterfaceOpcodes.DispatchAddItem,
				AddItemCodec.Build(new AddItem(containerType, charId, itemPos, entry, feedback))));
		}

		// DispatchDisplayItemLootChatMessage (0x2012) - the "you receive N x <item>" chat line. Body is
		// [u32 itemId][u32 itemId][u32 0][u32 count] followed by the two 24-byte identity halves the capture repeats,
		// which we send as zero: the client renders the item from the id, and the halves it echoes are the same value
		// twice, so there is nothing per-session to reproduce.
		//
		// Decoded from the harvesting span of the chat-annotated capture (#494), where the loot line lands beside its
		// annotation each time - Granite 161206 x40 (`just mined`), Oak 161213 x60 (`gethering wood`), Iron 161196 x40
		// (`mining iron`). The item id is repeated in the first two words in every observed record.
		private static Task SendItemLootChatMessage(INetworkClient client, uint itemId, uint count)
		{
			return client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(Endpoints.GameCharInterfaceType), GameCharInterfaceOpcodes.DispatchDisplayItemLootChatMessage,
				b => b.WriteUInt32(itemId).WriteUInt32(itemId).WriteUInt32(0u).WriteUInt32(count).WriteArray(new byte[48])));
		}

		// #348: the top-of-screen FIFO banner naming the SUB-AREA the player just walked into. Retail fires one on
		// every sub-area crossing, not once per playfield - Old Tarantia alone has six (Bridge of Vilerus I, The East
		// Gate, The West Gate, ...) - so the world tick drives this off MapArea, and the entry banner is simply the
		// first crossing. `areaNameId` is the client's localized-string id; the client renders the text itself.
		//
		// This used to take a mapId and read the single `Map.AreaNameId`, which could only ever name one area per
		// playfield (it had picked "The West Gate" for Old Tarantia, an arbitrary one of the six).
		private static Task SendAreaNameBanner(INetworkClient client, uint areaNameId)
		{
			return SendFeedback(client, new AreaNameBanner(areaNameId));
		}

		// Pushes one bank-slot stat update (0x201c on container 105): [kind=105][charInstance][slot][statId][value], 		// byte-exact to the capture (F376/F377/F364/F398). Rides the same GameCharAgent conversation as the bank
		// slot registrations (BankHandler), so the client applies it to the live bank cell.
		private static Task SendBankSlotStat(INetworkClient client, uint slot, uint statId, uint value)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(Endpoints.GameCharAgentType, 0x58), client.Endpoint(Endpoints.GameCharInterfaceType), GameCharInterfaceOpcodes.DispatchSetItemStat,
				SetItemStatCodec.Build(SetItemStats.BankSlot(client.Session.Character.Id, slot, statId, value))));
		}

		// Pushes a FormatFeedback message to the client's on-screen feedback, addressed to their GameInterface
		// instance. The PAYLOAD carries its own typeFlags/footer (#539) - this used to take the child's text plus those
		// two words, which is how the same message came to be framed differently in two services.
		//
		// The carrier's [u32 len] is the SENDER's (#530 rule 2), which is what WorldIir.Send writes - the codec emits
		// the record alone.
		private static Task SendFeedback(INetworkClient client, FeedbackPayload payload)
		{
			return WorldIir.Send(client, FormatFeedbackCodec.Build(client.Session.Character.Id, payload));
		}

		// Pushes a live item removal to the client (the inverse of SendAddItem).
		private static async Task SendRemoveItem(INetworkClient client, ContainerKind containerType, uint itemPos, uint amountToRemove = 0)
		{
			var charId = client.Session.Character.Id;

			await client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(Endpoints.GameCharInterfaceType), GameCharInterfaceOpcodes.DispatchRemoveItem,
				RemoveItemCodec.Build(new RemoveItem(containerType, charId, itemPos, amountToRemove))));
		}

		// #372: the "resurrection point discovered" FIFO banner. Retail sends it in the SAME frame as the
		// resurrection-spots re-send that adds the point to the player's list, and it is what the player actually
		// sees; only the point id goes on the wire (the client resolves the name from LDB category 270).
		private static Task SendRespawnPointDiscoveredBanner(INetworkClient client, uint pointId, string? nameKey)
		{
			return SendFeedback(client, new RespawnPointDiscovered(pointId, nameKey));
		}

		// Sends the proven captured system line - the test bootstrap for the ChatTextIIR path until a literal-text
		// remoteformat template lands.

		private static Task SendStatUpdate(INetworkClient client, CharacterStatBlock stats, uint entityHandle)
		{
			return client.SendAsync(BuildStatUpdate(client, stats, entityHandle));
		}

		// Pushes the death/release screen: the player's death position + the resurrection spots they can pick.
		private static Task SendValidResurrectionSpots(INetworkClient client, Vector3 deathPosition, IReadOnlyList<RespawnPoint> spots)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(Endpoints.GameCharAgentType, 0x59), client.Endpoint(Endpoints.GameCharInterfaceType),
				GameCharInterfaceOpcodes.DispatchValidResurrectionSpots, ValidResurrectionSpotsCodec.Build(ValidResurrectionSpotLists.Known(deathPosition, spots))));
		}

		// A vitals push emits the whole EntityVitals.ToVitalsBlock (current AND max) - there is deliberately no
		// currents-only sender here any more. There used to be a `SendVitals` pair that emitted the three CURRENT ids
		// alone, justified as "matches the retail full-spawn announce; max rides the snapshot". That holds for the
		// SPAWN ANNOUNCE and nowhere else: the max ids reach the client only when something sends them, so every
		// later max change was invisible and the bar kept its world-entry seed (#450). Use
		// CombatService.ReseedFromMaxStats(client).ToVitalsBlock() when a max changes.

		// Pushes a ChatTextIIR (raw remoteformat XML) to the client's chat window. Addressed to the player's
		// GameInterface instance, on the world conversation.
		private static Task SendWorldChat(INetworkClient client, string xml)
		{
			// As SendFeedback: the [u32 len] is the sender's (#530 rule 2), written by WorldIir.Send.
			return WorldIir.Send(client, ChatTextCodec.Build(client.Session.Character.Id, Encoding.Latin1.GetBytes(xml)));
		}

		// Sends a pre-framed world IIR body ([len]-prefixed) on the world conversation (opcode 0x2000).
	}
}
