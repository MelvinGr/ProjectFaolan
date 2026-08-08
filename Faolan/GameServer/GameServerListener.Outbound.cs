using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Constants;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Apprentice;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Guild;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Network.Server.GameServer;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Apprentice;
using Faolan.GameServer.Combat;
using Faolan.GameServer.Economy;
using Faolan.GameServer.Inventory;
using Faolan.GameServer.Quest;
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

		Task<int?> IEconomyOutbound.GrantItemToBackpack(INetworkClient client, uint itemTemplateId, uint count, int? preferredSlot)
		{
			return GrantItemToBackpack(client, itemTemplateId, count, preferredSlot);
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

		// IGameServerProvider seam: the PAY-OUT half of a guild-bank withdrawal (#132). The CS tier decides it (it owns
		// Guild.Cash and the rank ladder) and this tier pays it, because the wallet lives on the live session and the
		// client only learns of a change through this tier's UpdateCash.
		//
		// Refusing when the character is not connected here is the point, not a technicality: the caller debits the
		// guild only if this returns true, so a payout that nobody could receive never happens rather than vanishing.
		public async Task<bool> CreditCharacterCash(uint characterId, ulong amountTin)
		{
			var client = ClientForCharacter(characterId);
			var character = client?.Session.Character;
			if (character == null)
				return false;

			character.Money += amountTin;
			await Database.SetCharacterMoney(character.Id, character.Money);
			await SendUpdateCash(client!, character.Money, UpdateCashReason.Gain);
			Logger.LogInformation("CreditCharacterCash: {Character} received {Amount} tin (wallet {Balance}).", character.Name, amountTin, character.Money);
			return true;
		}

		// The deposit half of the guild bank (#654), the exact inverse of CreditCharacterCash above: there the CS tier
		// debits the guild and this tier credits the wallet, here this tier debits the wallet and the CS tier credits
		// the guild. Both do the debit on the side that HOLDS the money, so a failure between the two steps loses the
		// transfer rather than minting one.
		//
		// A payer who cannot afford it gets retail's own named refusal (`DispatchGuildNotEnoughCash`) rather than
		// silence: unlike the requests this codebase deliberately leaves unanswered, the client CAN legitimately send
		// this one - it does not know the wallet moved - and the client has a message for exactly this case.
		private async Task HandleGuildDeposit(INetworkClient client, GuildTransferMoney request)
		{
			var character = client.Session.Character;
			if (character == null || request.AmountTin == 0)
				return;

			if (request.AmountTin > character.Money)
			{
				Logger.LogInformation("GuildTransferMoney refused: {Character} offered {Amount} tin of their {Balance}.", character.Name, request.AmountTin, character.Money);
				// ⚠️ THE ENDPOINTS ARE THE WEAK PART, and they are not recovered from anything. The client's method
				// table gives the METHOD and its PARAMETERS; it says nothing about the RpcHeader, whose sender is a
				// server OBJECT id we only ever learn from a capture - and no capture performs a guild-bank deposit.
				//
				// Worse, the instance is demonstrably NOT constant per interface: captured GameCharInterface replies
				// arrive from GameCharAgent{0x58} (the login/world-handshake family) AND from GameCharAgent{0x10}
				// (the trade family). Which server object owns the guild bank is unknown, so 0x10 is a COIN FLIP
				// taken from the nearest neighbour, exactly like the quest-share dispatches below.
				//
				// That is the failure mode #101's team-invite window cost a day to: a wrong sender looks like a wrong
				// body and is not one. If this refusal does not render in-client, suspect this line before the codec.
				// [V0: the guild-bank reply endpoints]
				await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x10), client.Endpoint(RpcInterfaces.GameCharInterfaceType),
					GuildNotEnoughCashCodec.Method, GuildNotEnoughCashCodec.Build(request.AmountTin)));
				return;
			}

			character.Money -= request.AmountTin;
			await Database.SetCharacterMoney(character.Id, character.Money);
			await SendUpdateCash(client, character.Money, UpdateCashReason.Spend);

			if (await _csPlayerAgentDispatcher.DepositToGuildBank(character.Id, request.AmountTin))
				return;

			// Nobody could take it - put it back rather than leave it nowhere (the withdrawal's refund, mirrored).
			character.Money += request.AmountTin;
			await Database.SetCharacterMoney(character.Id, character.Money);
			await SendUpdateCash(client, character.Money, UpdateCashReason.Gain);
			Logger.LogWarning("GuildTransferMoney rolled back: {Character}'s guild is not on this realm's CS tier.", character.Name);
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
			return SendTradeRpc(targetId, GameCharInterfaceMethods.DispatchRequestTrade, EntityIdCodec.Build(EntityIds.Character(requesterId)));
		}

		Task ITradeOutbound.SendTradeStarted(uint recipientId, uint partnerId)
		{
			return SendTradeRpc(recipientId, GameCharInterfaceMethods.DispatchStartTrade, EntityIdCodec.Build(EntityIds.Character(partnerId)));
		}

		Task ITradeOutbound.SendPartnerCash(uint recipientId, ulong tin)
		{
			return SendTradeRpc(recipientId, GameCharInterfaceMethods.DispatchPartnerUpdatedCash, TradeCashCodec.Build(new TradeCash(tin)));
		}

		// The same body either way; only the opcode says whose side of the table the item is on.
		// The token was a bare 0 here. No capture pins that (the trade path was recovered from the client's method
		// table, not a trace) and it contradicts the measured rule: every captured message describing a real item
		// INSTANCE carries one - 165/165 DispatchAddItem entries, 8,479/8,479 snapshot entries, 2,734/2,734 shop
		// wares - while only the template ADVERTS (AH listings, mail attachments, claim lists) carry 0. A trade
		// offer names an instance, so it carries the token (#723).
		Task ITradeOutbound.SendTradeItem(uint recipientId, Item item, uint slot, uint count, bool mine)
		{
			var entry = SnapshotItems.InventoryEntry(FullCharacterRoots.ItemId(recipientId, ContainerKind.Bag, slot), item, AcgHash.ForWire(item.Id, item.Hash),
			[
				new SnapshotItems.ItemStat(0, 1), new SnapshotItems.ItemStat(23, (uint)FullCharacterRoots.RecordId(recipientId, ContainerKind.Bag, slot)),
				new SnapshotItems.ItemStat(26, count)
			], item.Level);

			return SendTradeRpc(recipientId, mine
				? GameCharInterfaceMethods.DispatchTradeAddedItem
				: GameCharInterfaceMethods.DispatchTradePartnerAddedItem, TradeAddedItemCodec.Build(new TradeAddedItem(entry, count)));
		}

		// The remove twin of SendTradeItem: the same one-body/two-opcodes shape, so only the opcode says whose side of
		// the table the item left. Recovered from the client's method table rather than a capture - see
		// TradeRemovedItemCodec for why that is sound here (#427).
		Task ITradeOutbound.SendTradeItemRemoved(uint recipientId, uint slot, bool mine)
		{
			return SendTradeRpc(recipientId, mine
				? GameCharInterfaceMethods.DispatchTradeRemovedItem
				: GameCharInterfaceMethods.DispatchTradePartnerRemovedItem, TradeRemovedItemCodec.Build(slot));
		}

		Task ITradeOutbound.SendAcceptAck(uint recipientId)
		{
			// Unnamed in the client's recovered catalog, so it keeps the honest Reserved name; the capture shows its role
			// unambiguously - it lands on the accepter's own connection, empty, right after their AcceptTrade.
			return SendTradeRpc(recipientId, GameCharInterfaceMethods.Reserved0x6B, []);
		}

		Task ITradeOutbound.SendPartnerAccepted(uint recipientId, uint partnerId)
		{
			return SendTradeRpc(recipientId, GameCharInterfaceMethods.DispatchPartnerAcceptedTrade, EntityIdCodec.Build(EntityIds.Character(partnerId)));
		}

		// The empty method 0x6D then the toast - retail sends both, in that order, to each party.
		async Task ITradeOutbound.SendTradeCompleted(uint recipientId)
		{
			// Also unnamed in the catalog. It lands on BOTH connections, empty, as the trade commits.
			await SendTradeRpc(recipientId, GameCharInterfaceMethods.Reserved0x6D, []);
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

			// Carries the token for the same reason SendTradeItem does: this IS a DispatchAddItem body, and every
			// captured one of those describes a real instance and carries one (#723).
			var entry = SnapshotItems.InventoryEntry(FullCharacterRoots.ItemId(characterId, ContainerKind.Bag, slot), item, AcgHash.ForWire(item.Id, item.Hash),
			[
				new SnapshotItems.ItemStat(0, 1), new SnapshotItems.ItemStat(23, (uint)FullCharacterRoots.RecordId(characterId, ContainerKind.Bag, slot)),
				new SnapshotItems.ItemStat(26, count)
			], item.Level);

			return SendAddItem(client, ContainerKind.Bag, slot, entry);
		}

		// --- IQuestShareOutbound: the wire + world side of a quest share (#101). Same shape as the trade port, and for
		// the same reason - a share always spans two connections, so every op is addressed by character id. -----------
		async Task<bool> IQuestShareOutbound.SendQuestShareRequest(uint recipientId, uint sharerId, uint questId, string questName)
		{
			if (ClientForCharacter(recipientId) is not { } client)
				return false;

			await SendQuestShareRpc(client, GameCharInterfaceMethods.DispatchQuestShareRequest, QuestShareRequestCodec.Build(sharerId, questId, questName));
			return true;
		}

		Task IQuestShareOutbound.SendQuestShareGlobalFeedback(uint recipientId, QuestShareFeedback type, uint value)
		{
			return ClientForCharacter(recipientId) is { } client
				? SendQuestShareRpc(client, GameCharInterfaceMethods.DispatchQuestShareGlobalFeedback, QuestShareGlobalFeedbackCodec.Build(type, value))
				: Task.CompletedTask;
		}

		Task IQuestShareOutbound.SendQuestShareIndividualFeedback(uint recipientId, QuestShareFeedback type, string playerName)
		{
			return ClientForCharacter(recipientId) is { } client
				? SendQuestShareRpc(client, GameCharInterfaceMethods.DispatchQuestShareIndividualFeedback, QuestShareIndividualFeedbackCodec.Build(type, playerName))
				: Task.CompletedTask;
		}

		INetworkClient? IQuestShareOutbound.ClientFor(uint characterId)
		{
			return ClientForCharacter(characterId);
		}

		CharacterLiveState? IQuestShareOutbound.LiveStateOf(uint characterId)
		{
			return GetCharacterLiveState(characterId);
		}

		// The share dispatches ride the same GameCharAgent -> GameCharInterface pair the trade ones do, from the same
		// sender instance. ⚠️ Un-gated: no capture performs a share, so the endpoint is the trade set's by analogy, not
		// by observation - and a wrong sender is exactly the failure that cost the team invite window (#101's team
		// third), which looks like a wrong body and is not one. [V0]
		private static Task SendQuestShareRpc(INetworkClient client, GameCharInterfaceMethods method, byte[] body)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x10), client.Endpoint(RpcInterfaces.GameCharInterfaceType), method, body));
		}

		// One place the trade replies are addressed from, so the endpoints cannot drift between them. Silently no-ops for
		// a character who is not connected here - a partner who vanished mid-trade is TradeService's problem, not the
		// wire's.
		private Task SendTradeRpc(uint characterId, GameCharInterfaceMethods opcode, byte[] body)
		{
			return ClientForCharacter(characterId) is { } client
				? client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x10), client.Endpoint(RpcInterfaces.GameCharInterfaceType), opcode, body))
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
			var taken = (await Database.GetCharacterInventory(characterId, ContainerKind.Bag)).Select(r => r.Slot).ToHashSet();
			// The seeded starter-bag item occupies cell 1 whenever it EXISTS. This used to also require a harvested
			// hash, as a proxy for "will the snapshot render it"; since #723 it always renders, and the old form
			// would hand cell 1 out twice for a bag item whose token had never been captured.
			if (equipment?.FirstPosBag != null)
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

			var taken = (await Database.GetCharacterInventory(characterId, container)).Select(r => r.Slot).ToHashSet();
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
		private async Task<int?> GrantItemToBackpack(INetworkClient client, uint itemTemplateId, uint count = 1, int? preferredSlot = null)
		{
			var item = await Database.GetItem(itemTemplateId);
			if (item == null)
				return null;

			var characterId = client.Session.Character.Id;

			// Quest items (Type 113, e.g. the Scavenger's Key) route to the QUEST container, not the backpack, so they
			// land in the player's quest-items tab (#261).
			//
			// There is no longer a hash gate here. A normal bag item used to REQUIRE a harvested hash (#719): the
			// world-entry snapshot renders `Hash != 0` only, so persisting a hash-less row left the item invisible
			// after a relog, and `RedeliverNonSnapshotBagItems` papered over that by re-pushing it as a live add on
			// every world entry - re-announcing "You received [X]" on every MAP CHANGE. #723 removed the premise:
			// every item now HAS a token, harvested or minted (AcgHash.ForWire), so the snapshot renders it and the
			// grant no longer has to refuse the 232 templates no capture ever showed entering an inventory.
			var isQuestItem = item.Type == ItemType.Quest;

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
					FullCharacterRoots.RecordId(characterId, kind, (ulong)slot.Value), count, item, AcgHash.ForWire(item.Id, item.Hash))
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

			var equipped = new Dictionary<ulong, Item>();
			foreach (var slot in InventoryEquippedSlots)
			{
				if (equipment[slot] is { } item)
					equipped[(ulong)slot] = item;
			}

			var bagFirst = equipment.FirstPosBag;

			var containers = FullCharacterRoots.Containers(characterId, equipped, bagFirst, await Database.GetCharacterInventory(characterId));

			// containers[0] = Equipped(101), [1] = Bag(104) - the two retail pushes. The Resource and Quest containers
			// it also builds are NOT pushed: the capture carries exactly these two.
			foreach (var container in containers.Take(2))
			{
				using var ms = new MemoryStream();
				Serializer.Serialize(ms, container);
				var payload = ms.ToArray();

				await client.SendAsync(new Rpc(WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
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

			await client.SendAsync(new Rpc(WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
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
			await client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchUpdateCash,
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
			return new Rpc(WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob, s => s.WriteArrayUInt32Length(GameTimeCodec.Build(dayTimeSeconds, now)));
		}

		// Frames the GuildUpdateIIR_c (GuildUpdateCodec) on the world conversation: [RpcHeader][DispatchPassBlob]
		// [u32 len][GuildUpdateIIR]. Sent on login for guilded characters and pushed live by the CsPlayerAgent
		// guild-accept flow through the dispatcher seam. The body grammar + round-trip live on GuildUpdateCodec.
		internal static Rpc BuildGuildTag(INetworkClient client, uint characterId, string guildName)
		{
			return new Rpc(WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
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

			return new Rpc(WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
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

		// Builds the player's own vitals (0x96B8DC59) from the player's LIVE world-model vitals - the world-entry
		// replay emits this instead of replaying the captured player's vitals.
		//
		// `vitals` must be the live EntityVitals, not `character.MaxVitals()`. The raw class/level curve does not
		// include worn gear, so sending it told the client a current the server did not agree with: an L23 Assassin
		// with +156 stamina of gear was announced at 742 while the world model held 898, and the bar sat short of
		// where regen would immediately put it.
		internal static Rpc BuildVitals(INetworkClient client, EntityVitals vitals)
		{
			var character = client.Session.Character;
			var stats = new CharacterMaxStats { Health = vitals.MaxHealth, Mana = vitals.MaxMana, Stamina = vitals.MaxStamina };
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
				// Level AND ApprenticeLevel - the client divides by the latter for armour mitigation, and the
				// nameplate renders max() of the two. See CharacterStatBlock.SetLevel.
				.SetLevel(character.Level)
				.SetExperience(character.Level, character.Experience);

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
			return new Rpc(WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob, s => s.WriteArrayUInt32Length(spawnRecord));
		}

		// Prepends the shared world-entry RpcHeader + outer length/CRC (the WriteHeader/WriteLengthHash path) to a
		// record body that ALREADY carries its own [u32 bodyLen] prefix - i.e. the `packet[bodyStart..]` form that
		// SpawnPacketBuilder and WriteArrayUInt32Length produce. This is the keystone that lets a
		// from-scratch record body reach the wire without a captured packet header.
		internal static Rpc FrameWorldEntryRecord(INetworkClient client, params ReadOnlySpan<byte> bodyWithLenPrefix)
		{
			return new Rpc(WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob, bodyWithLenPrefix.ToArray());
		}

		// Pushes one item into the client's inventory live, on the same GameCharAgent conversation the wallet
		// updates (0x2005) ride.
		private static async Task SendAddItem(INetworkClient client, ContainerKind containerType, uint itemPos, ProtoInventoryGameEntry entry, uint feedback = 0)
		{
			var charId = client.Session.Character.Id;

			await client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchAddItem,
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
			return client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchDisplayItemLootChatMessage,
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

			await client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchRemoveItem,
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

		// UNSEATS the player: the ride buff's end-twin 0x31EB1501 (empty payload) + StatUpd(Mount=0), broadcast to
		// viewers so the horse leaves their screens too (#428). It is the one place a mount comes off, because retail
		// ends every dismount with the same two records whatever caused it (mount capture frame 1449) - the button
		// press (after DismountChannelMs), a hit that throws the rider, and the respawn after a death all reach it.
		// The persisted mount is forgotten so world entry does not re-seat one the player no longer has (#142).
		private async Task SendDismount(INetworkClient client)
		{
			var character = client.Session.Character;
			var entity = _simulation.PlayerWorldEntity(client);
			if (entity?.IsMounted != true && character?.MountId == null)
				return;

			var self = character.Id;
			await ApplySpell(client, self, self, MountCatalog.DismountSpell);
			var stats = new CharacterStatBlock
			{
				[CharacterStatId.Mount] = 0
			};
			await SendStatUpdate(client, stats, self);
			await _broadcaster.BroadcastStatsToViewers(client, stats);

			entity?.IsMounted = false;
			entity?.MountRideStat = 0;
			entity?.MountDamageTaken = 0;

			character.MountId = null;
			await Database.SaveCharacterMount(self, null);
		}

		// #637: raises retail's "Do you want to travel to <map>?" box for a zone-line door, and remembers the trip it
		// is confirming. The (threadCtxId, cookie) pair is retail's script-thread handle; the client only ever echoes
		// it, so ours are a correlation token rather than a real thread - the character id plus a per-send counter,
		// which is unique within a session and is all the answer has to match.
		//
		// Retail's own sender endpoint instance varies per box across the corpus (0x54/0x56..0x5A - it is the scry
		// agent that happens to own the script), so 0x58 here is the house default, not a captured constant.
		private async Task SendTravelConfirmation(INetworkClient client, Map destination, Vector3? arrival)
		{
			var threadCtxId = client.Session.Character.Id;
			var cookie = (ulong)Interlocked.Increment(ref _travelConfirmCookie);
			client.Session.PendingTravelConfirm = new PendingTravelConfirm(threadCtxId, cookie, destination.Id, arrival);

			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x58), client.Endpoint(RpcInterfaces.GameCharInterfaceType),
				GameCharInterfaceMethods.DispatchScryShowMessageBox,
				ScryShowMessageBoxCodec.Build(destination.Id, LdbKey.ToWire(destination.TravelConfirmKey), threadCtxId, cookie)));

			Logger.LogInformation("Travel confirm for '{Map}' ({MapId}) sent to character {CharacterId} (cookie 0x{Cookie:X16}).", destination.Name, destination.Id,
				client.Session.Character.Id, cookie);
		}

		// Pushes the death/release screen: the player's death position + the resurrection spots they can pick.
		private static Task SendValidResurrectionSpots(INetworkClient client, Vector3 deathPosition, IReadOnlyList<RespawnPoint> spots)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x59), client.Endpoint(RpcInterfaces.GameCharInterfaceType),
				GameCharInterfaceMethods.DispatchValidResurrectionSpots, ValidResurrectionSpotsCodec.Build(ValidResurrectionSpotLists.Known(deathPosition, spots))));
		}

		// WHICH of the two vitals blocks to send is decided by WHAT MOVED, and both forms are retail's (#586).
		//
		// `EntityVitals.ToCurrentsBlock` - the three CURRENT ids - is for a push where only a current moved: a hit, a
		// cast's mana cost, the regen tick, fall damage. It is what retail overwhelmingly sends
		// (`--report vitals-shape`: 3,694 of the 4,900 blocks on a non-own handle carry exactly
		// {HealthCurrent, StaminaCurrent, ManaCurrent}).
		//
		// `EntityVitals.ToVitalsBlock` - current AND max - is for a push where a MAX moved: a level-up, a gear or buff
		// change to a pool, a GM `.setstat`. Use CombatService.ReseedFromMaxStats(client).ToVitalsBlock() there.
		//
		// ⚠️ The rule is "what moved", NOT "always send everything", and it is not "always send currents" either.
		// There used to be a blanket `SendVitals` pair emitting the three currents alone, justified as "matches the
		// retail full-spawn announce; max rides the snapshot" - and it was removed because the max ids reach the
		// client only when something sends them, so every later max change was invisible and the bar kept its
		// world-entry seed (#450). The replacement over-corrected the other way: sending all six on every swing is
		// three fabricated records per entity per hit on the hottest path in the game, and for an NPC - which has no
		// class curve, so ClassBasePools is null - the max ids fall back to our ceiling, which is not the quantity
		// that slot holds. Neither blanket is right; the split above is.

		// Pushes a ChatTextIIR (raw remoteformat XML) to the client's chat window. Addressed to the player's
		// GameInterface instance, on the world conversation.
		private static Task SendWorldChat(INetworkClient client, string xml)
		{
			// As SendFeedback: the [u32 len] is the sender's (#530 rule 2), written by WorldIir.Send.
			return WorldIir.Send(client, ChatTextCodec.Build(client.Session.Character.Id, Encoding.Latin1.GetBytes(xml)));
		}

		// Sends a pre-framed world IIR body ([len]-prefixed) on the world conversation (opcode 0x2000).

		// ---- APPRENTICESHIP (#144) ----
		//
		// ⚠️ Un-gated, for the same reason quest sharing's are: no capture performs an apprenticeship, so the ENDPOINT
		// pair is taken from the neighbouring GameCharInterfaceProject dispatches that ARE observed (SyncCanSprint /
		// UpdateSprintBar) rather than from an observation of these. The BODIES are the client's own signatures. [V0]
		async Task<bool> IApprenticeOutbound.SendApprenticeRequest(uint studentId, uint teacherId)
		{
			if (ClientForCharacter(studentId) is not { } client)
				return false;

			await SendApprenticeRpc(client, GameCharInterfaceProjectMethods.DispatchRequestApprentice, RequestApprenticeCodec.Build(teacherId));
			return true;
		}

		Task IApprenticeOutbound.SendApprenticeEntry(uint recipientId, uint teacherId, uint studentId, bool added)
		{
			return ClientForCharacter(recipientId) is { } client
				? SendApprenticeRpc(client, added
					? GameCharInterfaceProjectMethods.DispatchAddApprenticeEntry
					: GameCharInterfaceProjectMethods.DispatchRemoveApprenticeEntry, ApprenticeEntryCodec.Build(teacherId, studentId))
				: Task.CompletedTask;
		}

		Task IApprenticeOutbound.SendApprenticeMode(uint recipientId, bool on)
		{
			// The mode flag is on GameCharInterface, not GameCharInterfaceProject - a different interface, so a
			// different endpoint pair, which is exactly the distinction `WireOpcode` cannot express.
			return ClientForCharacter(recipientId) is { } client
				? client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x10), client.Endpoint(RpcInterfaces.GameCharInterfaceType),
					GameCharInterfaceMethods.DispatchApprenticeModeOnOff, ApprenticeModeCodec.Build(on)))
				: Task.CompletedTask;
		}

		async Task IApprenticeOutbound.SendApprenticeLevel(uint characterId, uint apprenticeLevel)
		{
			if (ClientForCharacter(characterId) is not { } client)
				return;

			// ApprenticeLevel ALONE - not `SetLevel`, which deliberately writes Level too so the nameplate tracks a GM
			// `.level` (#472). Here the real level is unchanged and only the mitigation term moves.
			await SendStatUpdate(client, new CharacterStatBlock().Set(CharacterStatId.ApprenticeLevel, apprenticeLevel), characterId);
		}

		Task IApprenticeOutbound.SendApprenticeFeedback(uint characterId, string message)
		{
			return _agentServerDispatcher.SendSystemMessage(characterId, message);
		}

		uint? IApprenticeOutbound.LevelOf(uint characterId)
		{
			return ClientForCharacter(characterId)?.Session.Character.Level;
		}

		string? IApprenticeOutbound.NameOf(uint characterId)
		{
			return ClientForCharacter(characterId)?.Session.Character.Name;
		}

		// The SEARCH stance (#505): retail's answer to PerformSpecialAction 0x064 is one player-addressed
		// BehaviorControlCenterIIR at slot BehaviorId.Search, and nothing else - no stat change, no buff. The client
		// waits for it before the stance takes, which is why a press with no answer reads as "the client sent
		// nothing".
		//
		// The animation action is the CHARACTER's, not the stance's, so it comes from a per-class map holding only the
		// two classes a capture shows. An unmapped class logs and does nothing, the same call the spellweave toggle
		// makes: a borrowed id would play some other class's animation and read as working.
		private async Task PerformSearchStance(INetworkClient client)
		{
			var character = client.Session.Character;
			if (!StartingData.SearchStanceAction.TryGetValue(character.Class, out var animAction))
			{
				Logger.LogInformation("Search stance: no captured animation action for class {Class} - not answering (#505).", character.Class);
				return;
			}

			var entity = _simulation.PlayerWorldEntity(client);
			await WorldIir.Send(client, BehaviorControlCenterCodec.Build(BehaviorAnimations.SearchStance(character.Id, animAction, entity?.NextBehaviorPhase() ?? 3)));
		}

		// The HIDE stance (#505) - a TOGGLE on one selector, so the server holds which way the next press goes.
		// Entering carries the shadow coverage the client itself reported a moment earlier (SetShadowCoverageIIR);
		// leaving is the shared stand-up action, the one part of the pair that is NOT class-specific.
		//
		// ⚠️ The EXIT record's shape is taken from the two necromancer sightings (flags 8, no echoed command id). The
		// one Herald of Xotli exit in the corpus carries flags 10 and a command id instead, on an otherwise identical
		// 8-byte-tailed record. Three sightings that disagree two-to-one is not enough to model the difference, so the
		// majority form is sent for both and the divergence is written down rather than silently averaged away.
		private async Task ToggleSneakHide(INetworkClient client)
		{
			var character = client.Session.Character;
			if (!StartingData.SneakHideEnterAction.TryGetValue(character.Class, out var enterAction))
			{
				Logger.LogInformation("Hide stance: no captured animation action for class {Class} - not answering (#505).", character.Class);
				return;
			}

			var entity = _simulation.PlayerWorldEntity(client);
			var hidden = entity?.IsHidden ?? false;
			var phase = entity?.NextBehaviorPhase() ?? 3;

			var record = hidden
				? BehaviorAnimations.SneakHideExit(character.Id, StartingData.SneakHideExitAction, phase)
				: BehaviorAnimations.SneakHideEnter(character.Id, enterAction, phase, entity?.NextBehaviorCommandId() ?? 3, entity?.ShadowCoverage ?? 100);

			await WorldIir.Send(client, BehaviorControlCenterCodec.Build(record));
			if (entity != null)
				entity.IsHidden = !hidden;

			Logger.LogInformation("Hide stance: '{Name}' {Action}.", character.Name, hidden
				? "left hiding"
				: "entered hiding");
		}

		// REST (#505). Unlike search and hide this is not a stance at all: retail answers with a self-CAST of the
		// spell the client calls `Resting` - "Recovering health, mana, energy, and stamina" - on slot 4 MagicAttack,
		// with the spell row's own 20,500 ms cast time in the record. The player regains vitals over that channel,
		// which is what a 20-second cast bar with no spell at the end of it IS.
		//
		// The record is class-independent (the cast animation 177216 is shared with the Path of Asura recall) and is
		// byte-identical across four captured characters, so there is no per-class map here.
		//
		// ⚠️ What is emitted is the CAST. The regen RATE while resting is not modelled: no capture measures it, and
		// the tick's rates are corpus-verified numbers (RegenRates, #442) that a guessed rest multiplier would
		// quietly break. `IsResting` is set so the tick can be taught the real rate once one is measured, and it is
		// cleared when the channel elapses - the channel length is the spell row's, not invented.
		private async Task PerformRestCast(INetworkClient client)
		{
			var character = client.Session.Character;
			var entity = _simulation.PlayerWorldEntity(client);

			var record = BehaviorAnimations.RestCast(character.Id, entity?.NextBehaviorPhase() ?? 1);
			await WorldIir.Send(client, BehaviorControlCenterCodec.Build(record));

			// ⚠️ Rest is a CAST, so it has to arm the cast path's cancel state or it can never be cancelled (#505).
			// The client ends this channel the same way it ends any other: it sends the c2s cancel BCC (seq 0x0002)
			// when the player moves, and `HandleBccCastSpell` answers it with retail's 0x0007 abort - but ONLY if it
			// finds the BEGIN's args here. Rest is answered to a `PerformSpecialAction` rather than to a c2s cast, so
			// nothing ever set them, `beginArgs` came back null, and the cancel fell through to a plain echo instead
			// of the abort. The client then stayed channeling: the bar hung, and a channeling client sends no further
			// cast OR melee, so one press of rest silently disabled every combat action the player had.
			//
			// Verified against `NecroL80_..._cast-general-abilities` frames 867/921/927 - begin seq 0x0004 (phase 8,
			// activation 2050) -> c2s cancel seq 0x0002 -> s2c abort seq 0x0007, whose args are these begin args with
			// the cancel's own command id spliced in. So the record to send back is not a completion at all.
			client.Session.PendingCastArgs = record.Args;
			client.Session.PendingCastMode = 0; // no client command id at begin - the cancel brings its own

			if (entity == null)
				return;

			entity.RestingUntil = DateTimeOffset.UtcNow.AddMilliseconds(RestingChannelMs);
			Logger.LogInformation("Rest: '{Name}' began resting for {Ms} ms.", character.Name, RestingChannelMs);
		}

		private static Task SendApprenticeRpc(INetworkClient client, GameCharInterfaceProjectMethods method, byte[] body)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentProjectType, 0x54), client.Endpoint(RpcInterfaces.GameCharInterfaceProjectType), method, body));
		}
	}
}
