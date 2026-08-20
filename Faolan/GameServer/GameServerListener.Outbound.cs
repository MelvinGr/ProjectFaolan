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
using Faolan.Core.Network.Codecs.Inventory;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Progression;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Network.Server.GameServer;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Apprentice;
using Faolan.GameServer.Combat;
using Faolan.GameServer.Economy;
using Faolan.GameServer.Inventory;
using Faolan.GameServer.Progression;
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

		Task<int?> IEconomyOutbound.GrantItemToBackpack(INetworkClient client, uint itemTemplateId, uint count, int? preferredSlot, string? creatorName)
		{
			return GrantItemToBackpack(client, itemTemplateId, count, preferredSlot, creatorName);
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

		Task IEconomyOutbound.SendGemSocketed(INetworkClient client, ContainerKind container, uint targetSlot, ItemInstanceSocket socket, uint gemSlot)
		{
			return SendGemSocketed(client, container, targetSlot, socket, gemSlot);
		}

		Task IEconomyOutbound.SendTradeskillComplete(INetworkClient client, uint recipeId)
		{
			return SendTradeskillComplete(client, recipeId);
		}

		Task IEconomyOutbound.SendGuildBankData(INetworkClient client, ProtoInventoryGame vault)
		{
			return SendGuildBankData(client, vault);
		}

		Task IEconomyOutbound.SendGuildBankMove(INetworkClient client, GuildBankMove move)
		{
			return SendGuildBankMove(client, move);
		}

		Task<int?> IEconomyOutbound.TransferItemToCharacter(uint characterId, ItemInstance instance)
		{
			return ((ITradeOutbound)this).TransferItemToCharacter(characterId, instance);
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
		// `windowSlot` is the TRADE WINDOW position, not an inventory cell - it addresses the offer on both players'
		// screens. The offered INSTANCE is passed in rather than looked up, because neither of the two things this
		// method knows can find it: `recipientId` is whoever is being TOLD (the partner, for the `mine: false` half),
		// and `windowSlot` is not a bag slot. Resolving it from those two was wrong in both directions.
		async Task ITradeOutbound.SendTradeItem(uint recipientId, ItemInstance offered, uint windowSlot, uint count, bool mine)
		{
			var item = offered.Template;
			var entry = SnapshotItems.InventoryEntry(FullCharacterRoots.ItemId(offered.Id), item, AcgHash.ForWire(item.Id, item.Hash),
			[
				new SnapshotItems.ItemStat(0, 1), new SnapshotItems.ItemStat(23, (uint)FullCharacterRoots.RecordId(recipientId, ContainerKind.Bag, windowSlot)),
				new SnapshotItems.ItemStat(26, count)
			], sockets: offered.Sockets);

			await SendTradeRpc(recipientId, mine
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
			// #792 named it: its handler is byte for byte DispatchPartnerAcceptedTrade's, on the LOCAL holder instead
			// of the one the ID32 names - so it is the argument-less YOU half. Which is exactly what the capture
			// already said: it lands on the accepter's own connection, empty, right after their AcceptTrade.
			return SendTradeRpc(recipientId, GameCharInterfaceMethods.DispatchAcceptedTrade, []);
		}

		Task ITradeOutbound.SendPartnerAccepted(uint recipientId, uint partnerId)
		{
			return SendTradeRpc(recipientId, GameCharInterfaceMethods.DispatchPartnerAcceptedTrade, EntityIdCodec.Build(EntityIds.Character(partnerId)));
		}

		// The 0x6D trade-completed method then the toast - retail sends both, in that order, to each party.
		async Task ITradeOutbound.SendTradeCompleted(uint recipientId)
		{
			// #792 named it too: the client's handler is CoreTradeHolder's own reset - the in-trade flag cleared, the
			// accepted byte and both cash slots zeroed, both item lists freed - and it fires the trade-ended signal
			// with 1 where TradeAbort (0x6A) sends 0. The capture agrees: it lands on BOTH connections, empty, as the
			// trade commits.
			await SendTradeRpc(recipientId, GameCharInterfaceMethods.DispatchTradeCompleted, []);
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

		async Task<int?> ITradeOutbound.GrantItemToCharacter(uint characterId, uint itemTemplateId, uint count)
		{
			// Resolving the connection here is the whole point of the character-id form: the trade path holds only
			// the receiver's id, and everything past this line is the shared grant - persistence, the live add, the
			// quest-container routing and AdvanceCollectGoals (#726).
			return ClientForCharacter(characterId) is { } client
				? await GrantItemToBackpack(client, itemTemplateId, count)
				: null;
		}

		// The HAND-OVER form: the same delivery, except `transferring` MOVES instead of a new instance being minted, so
		// the item keeps its identity, its crafter and its gems (#750).
		async Task<int?> ITradeOutbound.TransferItemToCharacter(uint characterId, ItemInstance instance)
		{
			return ClientForCharacter(characterId) is { } client
				? await GrantItemToBackpack(client, instance.TemplateId, instance.Count, transferring: instance)
				: null;
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
			var bag = await Database.GetCharacterItemSlot(characterId, ContainerKind.Equipped, (int)CharacterEquipmentSlot.Bag);
			return (int)(BaseBackpackSlots + (bag?.Template.BagExtraSlots ?? 0));
		}

		// The first free backpack cell, skipping occupied ones. The backpack size is 24 base + the equipped bag's extra
		// slots (#12), so a grown bag can be filled. Null = full. Read-only DB helper (mirrors
		// AddItemBuilder.FindFreeBagSlot - kept local to avoid a 4th seam).
		private async Task<int?> FindFreeBagSlot(uint characterId)
		{
			var taken = (await Database.GetCharacterItems(characterId, ContainerKind.Bag)).Select(i => i.Slot).ToHashSet();
			var capacity = await BackpackCapacity(characterId);
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

			var taken = (await Database.GetCharacterItems(characterId, container)).Select(i => i.Slot).ToHashSet();
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
		//
		// `transferring` turns this from a MINT into a HAND-OVER: the delivery, the slot search, the live add, the
		// quest-container routing and AdvanceCollectGoals are all identical, but the named instance MOVES into the found
		// cell instead of a new one being created. That is what a player trade is, and re-minting there silently
		// destroyed the item's crafter and its socketed gems. It is a parameter rather than a second method precisely so
		// the choke point stays ONE path - InventoryGrantChokePointTests exists to stop a delivery growing its own.
		private async Task<int?> GrantItemToBackpack(INetworkClient client, uint itemTemplateId, uint count = 1, int? preferredSlot = null, string? creatorName = null,
			ItemInstance? transferring = null)
		{
			// ⚠️ Every refusal below says WHY. This method used to return a bare `null` from three different places -
			// no template, no free slot, the delivery itself failing - and the caller could not tell them apart, so a
			// grant that silently did nothing looked identical to a grant that had never been asked for. #824 is what
			// that costs: harvesting a `Spool of Thread` played the whole node animation and put nothing in the bag,
			// and the reason (its template is not in `Items`) appeared in no log anywhere.
			var item = await Database.GetItem(itemTemplateId);
			if (item == null)
			{
				// A DATA gap rather than a player condition, so it is a warning: something in the world - a quest
				// goal, a loot table, a harvest node - is pointing at a template we do not hold. Almost always an
				// item no capture ever showed, whose TEA key is therefore unrecoverable (#203), which is why the row
				// cannot simply be added.
				Logger.LogWarning("Grant refused for {Char}: item template {Template} is not in Items, so there is nothing to hand over.",
					client.Session.Character.Name, itemTemplateId);
				return null;
			}

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
			{
				// An ordinary PLAYER condition, not a defect - a full bag is a thing that happens - so it is
				// informational. It is still stated, because "nothing arrived" reads the same to the player either
				// way and the two need different answers from whoever reads the log.
				Logger.LogInformation("Grant refused for {Char}: no free {Kind} slot for item {Template} ({Name}).",
					client.Session.Character.Name, kind, itemTemplateId, item.Name);
				return null;
			}

			// The instance the client is about to be told about, and the id the live add and the next login's snapshot
			// both name it by (#750). The two used to agree via a formula over (instance, kind, slot); they agree by
			// identity now. A GRANT mints it here; a HAND-OVER moves the existing one, keeping everything hanging off it.
			var delivered = transferring != null
				? await Database.TransferCharacterItem(transferring.Id, characterId, kind, slot.Value)
				: await Database.SetCharacterItemSlot(characterId, kind, slot.Value, itemTemplateId, count, creatorName);
			if (delivered == null)
			{
				// A free slot was found and the write still did not produce a row - so this one is neither a data gap
				// nor a full bag, and it is the only one of the three that should never happen.
				Logger.LogWarning("Grant failed for {Char}: {Kind}[{Slot}] was free and item {Template} ({Name}) still did not persist.",
					client.Session.Character.Name, kind, slot.Value, itemTemplateId, item.Name);
				return null;
			}

			// Live DispatchAddItem so the item appears without a relog. Quest items go to the QUEST container (0x71), 			// matching retail's chain-capture delivery; everything else to the backpack (0x68).
			var containerType = isQuestItem
				? ContainerKind.Quest
				: ContainerKind.Bag;
			// The LIVE-ADD stat list, not the snapshot's - retail's two shapes differ (#722).
			// The INSTANCE overload: a hand-over can carry an item that has already BOUND to its previous owner, and
			// only the row knows that. Spreading it back into loose scalars is how the crafter and the sockets got
			// lost before #750, and the bound bit would have been the next one.
			var entry = SnapshotItems.LiveAddEntry(delivered, FullCharacterRoots.ItemId(delivered.Id),
				FullCharacterRoots.RecordId(characterId, kind, (ulong)slot.Value));
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
			var containers = FullCharacterRoots.Containers(characterId, await Database.GetCharacterItems(characterId));

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

		// Removes an item from the player's backpack: deletes the persisted bag item (so it stays gone across relog) AND
		// pushes the live DispatchRemoveItem so it disappears immediately. The mirror of GrantItemToBackpack. Returns
		// true if an item was there to remove.
		private async Task<bool> RemoveItemFromBackpack(INetworkClient client, int slot, uint amountToRemove = 0)
		{
			var characterId = client.Session.Character.Id;
			if (await Database.GetCharacterItemSlot(characterId, ContainerKind.Bag, slot) == null)
				return false;

			await Database.SetCharacterItemSlot(characterId, ContainerKind.Bag, slot, null);
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
			var steps = InventoryTake.Plan(await Database.GetCharacterItems(characterId), itemTemplateId, count);

			foreach (var step in steps)
			{
				// A partial take DECREMENTS the item in place - the crafter and any socketed gems stay on it because the
				// row is not re-minted (#750).
				if (step.Remaining == 0)
					await Database.SetCharacterItemSlot(characterId, step.Container, step.Slot, null);
				else
					await Database.SetCharacterItemCount(characterId, step.Container, step.Slot, step.Remaining);

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

		// #792: the CloseShop ACK. Retail answers `CloseShop` with `DispatchTradeAbort` (GameCharInterface `0x6A`,
		// zero-length) and we answered nothing at all - so the vendor session was left open on the client's side of
		// the model.
		//
		// The name is the CLIENT's: its handler is `CoreTradeHolder::TradeAbort()`, whose own assert string reads
		// *"is closing all trades"* - a vendor session IS a trade to the client, which is why one message serves both.
		// The TRIGGER is measured rather than assumed: of the 153 frames across 132 captures, **152 are the immediate
		// reply to `CloseShop`** (the odd one out follows an `UpdateQuestMarkerNPC`, i.e. a shop closed by walking
		// away). [V2: Hakotep_hox_login_gameplay0.tshark.json fr695 -> fr697, and 151 more like it across the corpus]
		//
		// ⚠️ It aborts EVERY open trade, not just the shop, so it must not be sent while a player-to-player trade is
		// running. Nothing here can do that today - a shop and a trade cannot both be open - but the constraint is the
		// reason this is not a general "close the window" message.
		private static Task SendTradeAbort(INetworkClient client)
		{
			return client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchTradeAbort, _ => { }));
		}

		// #792: the zero-length GameCharInterface `0x1B` retail sends immediately before every `IntendedTeleportIIR` -
		// **102 of 102 across the corpus, no exceptions**, on cross-map handoffs and same-map revives alike.
		//
		// ⚠️ The client does NOTHING with it: `GamecodeClient_t`'s override of the vtable slot it dispatches to
		// (+0x70) is an empty function. So this is sent for fidelity with retail's byte stream and for nothing else -
		// do not hang behaviour off it, and do not go looking for what it "should" do.
		private static Task SendTeleportPending(INetworkClient client)
		{
			return client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.Reserved0x1B, _ => { }));
		}

		// Closes a craft: retail's last word on the transaction, after the product add and the ingredient removals.
		// The body is the RECIPE ID and nothing else - the capture's `0000000F` is recipe 15, whose own rows are Plain
		// Hilt Piece x1 + Copper x4 + Silver x4 -> Bloodsilver Khopesh, matching the removals that precede it.
		// [V2: NecroL80_mage-necromancer_login_gameplay_annotated_5 fr17402]
		//
		// Sent ONLY on success. Retail has no failure form here, and a completion over a craft that did not happen
		// would tell the client to close its window on an item it never got.
		private static Task SendTradeskillComplete(INetworkClient client, uint recipeId)
		{
			return client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchTradeskillComplete,
				b => b.WriteUInt32(recipeId)));
		}

		// Awards a TAG (#748): `DispatchAddTag`, body = the tag id and nothing else, 4 bytes. That covers two
		// phenomena sharing one id space - an ACHIEVEMENT (the catalog is LDB 55701/55700, imported into
		// `Achievements`: 7917 "Seeing Red" / *"Achieve a kill streak."*, 716 "Old Tarantia Docks") and the item-shop
		// "bundle shown" flags whose `HasTag` gates live in the client's own shop Lua (6617-6620, 7316, 7416-7421).
		// [V2: NecroL80_..._gameplay_annotated_2 t+285.5 tag 0x2CC; 23 awards over 14 captures]
		//
		// ⚠️ There is no login re-send, and nothing needs one: NOT ONE of the 23 captured awards lands in a login
		// burst, because the client learns the set it already holds from the FullChar snapshot (SendBurstlessWorldEntry
		// unions `GetCharacterTags` into it). So this message is strictly the moment-of-award event, which is why the
		// caller must not send it for a tag the character already has.
		//
		// A REMOVE twin exists in the client's method table - `DispatchRemoveTag` (0x17) - with zero captured frames, so
		// nothing is built for it.
		private static Task SendAddTag(INetworkClient client, uint tagId)
		{
			return client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchAddTag,
				b => b.WriteUInt32(tagId)));
		}

		// Grants a tag and tells the client, once. Persists first (so a relog keeps it, via the snapshot) and only
		// sends the award if the row was new - re-awarding a held tag would pop the client's banner again.
		// Returns the achievement it named, or null for an id the client's catalog does not carry (an item-shop flag,
		// or an entry the catalog is short of); false in `Granted` means they already had it.
		private async Task<(bool Granted, Achievement? Achievement)> GrantTag(INetworkClient client, uint tagId)
		{
			var granted = await Database.AddCharacterTag(client.Session.Character.Id, tagId);
			if (granted)
				await SendAddTag(client, tagId);

			return (granted, await Database.GetAchievement(tagId));
		}

		// Answers a TradeskillRequestIIR (recipe-book open) with the TradeskillReplyIIR (0x9062D559) recipe list built
		// from the Recipe catalog, so the Crafting -> Recipes window populates. The carrier is the standard world IIR
		// framing (GameAgent 0x58 -> GameInterface <instance>), body = [subop][0xC350][handle][reply], byte-exact vs
		// the capture (docs/subsystems/tradeskill-recipes.md). Empty catalog -> an empty (count=0x3F1) list, which opens the book
		// cleanly with no recipes.
		private async Task SendCraftingRecipeBook(INetworkClient client)
		{
			var handle = client.Session.Character.Id;

			// #735: only what the character has TRAINED for. A fresh character has learned no profession and so gets
			// an EMPTY book, which is retail - the book is what a profession buys. The gate is the recipe's own
			// `RequiredFeatId` against the character's rank on that profession's ladder; see TradeskillService.
			var rows = await _tradeskill.BookFor(handle, await Database.GetRecipes());
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
		// NOT universal (beach vs mainland differ), so they come from the DB (Map.PlayfieldCount/BlobLastWord/Flags/Instanced), 		// defaulting to the beach values for un-sourced maps. Position is the character's entry transform (per-session);
		// nearClip 15.0 + the farClip float are camera params (left constant). `map` may be null (fresh/unmapped -> beach
		// defaults).
		// The word at body [55:59] (client obj+0x54). We send 1 for every map, which is what 97 of the 151 captured
		// records carry; the other 54 carry 0, and the split is not per-map (4000 and 4010 carry both). Was written
		// as 0x01000000 while the builder's framing was a byte out - see BuildPlayfieldFiir (#451). [V0]
		private const uint SceneWord = 1u;

		// ⭐ #512: `teamId` names the TEAM that owns this copy of the playfield, when one does. The proxy's owner pair
		// is `(type, id)`, and retail's own group entry carries `(0xDEA9, <teamId>)` there where a personal instance
		// carries `(PlayfieldFieldA, <characterId>)` - the Amphitheatre of Karutonia entry names the exact id
		// `DispatchYouJoinedTeam` had announced three seconds earlier. So the pair is switched WHOLE rather than
		// having the id swapped under the map's own type word: a team id under type 2 would be a shape retail never
		// sends. Null (the common case) leaves the personal form exactly as it was.
		internal static Rpc BuildMapChange(INetworkClient client, Map map, uint playfieldToken, uint? teamId = null, bool epic = false)
		{
			var character = client.Session.Character;
			var charInstance = map?.PlayfieldInstanced ?? true
				? character.Id
				: 0u;
			var count = map?.PlayfieldCount ?? 2u;
			var blobLastWord = map?.PlayfieldBlobLastWord ?? 3u;
			var flags = map?.PlayfieldFlags ?? 0u;
			// #451: the two per-map words that used to be builder constants. The `??` defaults are those constants, so
			// a map with no imported MapChange behaves exactly as before rather than silently getting a zero.
			var fieldA = map?.PlayfieldFieldA ?? 0u;
			var fieldC = map?.PlayfieldFieldC ?? 7u;

			// The owner pair, switched as a unit when a team owns the copy (see the remark above).
			var ownerType = teamId is null ? fieldA : PlayfieldProxy.TeamOwnerType;
			var ownerId = teamId ?? charInstance;

			// #309: the proxy's class word is the copy's DIFFICULTY - 0xCABC on the captured epic Spider Caverns
			// entry against 0xC79C everywhere else. Everything else in the record is mode-independent.
			return new Rpc(WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(PlayfieldFCodec.Build(new Vector3(character.PositionX, character.PositionY, character.PositionZ), character.MapId,
					ownerId, count, playfieldToken, 15.0f, BitConverter.UInt32BitsToSingle(0x40E3944Fu), flags, SceneWord, blobLastWord, ownerType, fieldC,
					epic ? PlayfieldProxy.EpicPlayfieldClass : PlayfieldProxy.PlayfieldClass)));
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
		// ⚠️ The 4th word is the item's LEVEL, not a count. The body is the same ProtoAcgItem-shaped layout the
		// client's `itemref://` chat link carries - [template][template][0][level][hash][key1][key2][key3] - and
		// reading it as a count is what put the bogus 40 / 60 / 40 yields into HarvestCatalog (#732): Items.Level for
		// Granite, Oak and Iron is 40, 60 and 40 exactly. The line renders an item LINK, so the level is what its
		// tooltip reads; how many arrived is the ADD's business, not this message's.
		// The 48-byte tail is the hash + the three key quads, which we do not fill - the link still renders.
		private static Task SendItemLootChatMessage(INetworkClient client, uint itemId, uint level)
		{
			return client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchDisplayItemLootChatMessage,
				b => b.WriteUInt32(itemId).WriteUInt32(itemId).WriteUInt32(0u).WriteUInt32(level).WriteArray(new byte[48])));
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

		// A gem went into a socket (#754). Retail's answer to the socketing request is exactly two messages:
		// DispatchAddToItemSlot for the cell that RECEIVED the gem, then DispatchRemoveItem for the cell the gem
		// left. The socketed item's own container entry is NOT re-sent - the client updates it in place from the
		// first message, which is why nothing here rebuilds the inventory.
		// [V2: NecroL80_mage-necromancer_login_gameplay_annotated_8.tshark.json fr8248 - both in the same frame]
		private async Task SendGemSocketed(INetworkClient client, ContainerKind container, uint targetSlot, ItemInstanceSocket socket, uint gemSlot)
		{
			var charId = client.Session.Character.Id;
			await client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), AddToItemSlotCodec.Method,
				AddToItemSlotCodec.Build(container, charId, targetSlot, socket)));

			await SendRemoveItem(client, container, gemSlot);
		}

		// The GUILD BANK vault (#654), pushed as part of the banker open. The same container builder the personal
		// bank uses, with the guild id where a character id would go.
		private static Task SendGuildBankData(INetworkClient client, ProtoInventoryGame vault)
		{
			return client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GuildBankCodec.Method,
				GuildBankCodec.Build(vault)));
		}

		// The echo acknowledging a guild-bank item move - the request's own six values, raw.
		private static Task SendGuildBankMove(INetworkClient client, GuildBankMove move)
		{
			return client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GuildBankMoveCodec.Method,
				GuildBankMoveCodec.Build(move)));
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
			await _buffs.ApplySpell(client, self, self, MountCatalog.DismountSpell);
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

		// #821: the ACG INSTANCE BIND box - the confirmation retail raises instead of teleporting when the destination
		// is a personal instance. The client answers with RequestACGTeleport (or, if the player declines, with nothing
		// at all), so the offer is recorded on the session for the handler to check against.
		//
		// The proxy is built for the DESTINATION: its class word is what marks the playfield as ACG, and it is echoed
		// back verbatim in the request. The instance token is 0 - we do not mint per-player instance tokens (#268), and
		// the capture's token is that session's, not a per-map constant.
		private async Task SendAcgTeleportChoice(INetworkClient client, uint destinationMapId, uint playfieldClass, Vector3? arrival)
		{
			client.Session.PendingAcgTeleport = new PendingAcgTeleport(destinationMapId, arrival);

			var proxy = PlayfieldProxy.ForMap(destinationMapId) with { ClassWord = playfieldClass };
			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x56), client.Endpoint(RpcInterfaces.GameCharInterfaceType),
				GameCharInterfaceMethods.DispatchACGTeleportChoice,
				AcgTeleportChoiceCodec.Build(new AcgTeleportChoice(client.Session.Character.Id, proxy))));

			Logger.LogInformation("ACG bind box for map {MapId} sent to character {CharacterId}.", destinationMapId, client.Session.Character.Id);
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

		// The level-scaled rating QUAD (#685) - AttackRating / DefenseRating / MagicRating / MagicalResist, four
		// player-addressed StatIIRs carrying one number, in the wire order the corpus sends them.
		//
		// It rides the login snapshot (`SnapshotStatSheet`), and until now that was the ONLY place it ever went - so a
		// character who dinged read the level it logged in at until the next relog. Retail restates it, and the corpus
		// names two of the occasions outright:
		//
		//   - a LEVEL-UP. 15 of the 18 captured `NewLevelIIR` events are followed by a quad burst, 11 of them within
		//     0.1 s, i.e. inside the ding burst itself. That one is a correctness fix, not fidelity: the value is
		//     `400 x level - 300` and the level just changed.
		//   - the COMBAT TOGGLE (#505). 13 of 13 captured 0x4E2 presses draw a burst 0.31-1.61 s later, against a
		//     median burst interval of 10.4 s across the corpus - so it is causal, not adjacency. Mechanically it is
		//     what a sheathe/draw IS: the active weapon changes, so the ratings are restated.
		//
		// ⚠️ Those are two occasions, not THE rule: bursts arrive every ~10 s in a live capture and most of them
		// follow neither. [V0: what makes retail re-send the quad] stands - what is settled is the VALUE.
		private static async Task SendCombatRatings(INetworkClient client)
		{
			var character = client.Session.Character;
			var rating = CombatRatingCurve.For(character.Level);
			foreach (var stat in CombatRatingCurve.Stats)
				await WorldIir.Send(client, StatCodec.Build(StatIirs.ForCharacter(character.Id, (uint)stat, rating)));
		}

		// --- ILevelUpOutbound: the wire side of a ding. Explicit forwards to the ops that already existed here, which
		// is what keeps LevelUpService off the listener itself - see Progression/ILevelUpOutbound.cs.
		Task<EntityVitals> ILevelUpOutbound.ReseedVitals(INetworkClient client)
		{
			return _combat.ReseedFromMaxStats(client);
		}

		Task ILevelUpOutbound.SendCombatRatings(INetworkClient client)
		{
			return SendCombatRatings(client);
		}

		Task ILevelUpOutbound.AnnounceAbility(INetworkClient client, Ability ability)
		{
			return SendAddSpecialAbility(client, ability);
		}

		Task ILevelUpOutbound.LearnSpell(INetworkClient client, uint spellId)
		{
			return SendLearnSpell(client, spellId);
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
			// #272: a stance is something the OTHER players watch you do, so the same record is relayed to everyone who
			// can see the actor. `BehaviorControlCenterIIR` is 11/11 broadcast in the two-client capture
			// (`--report broadcast-check`), and a search crouch nobody else sees is a player frozen upright to them.
			var searchStance = BehaviorControlCenterCodec.Build(BehaviorAnimations.SearchStance(character.Id, animAction, entity?.NextBehaviorPhase() ?? 3));
			await WorldIir.Send(client, searchStance);
			await _broadcaster.BroadcastBehaviorToViewers(client, searchStance);
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

			// #272: relayed like the search stance - entering or leaving hiding is visible on the actor, and a rogue
			// who slips into the shadows only on their own screen is still standing there for everyone else.
			var stance = BehaviorControlCenterCodec.Build(record);
			await WorldIir.Send(client, stance);
			await _broadcaster.BroadcastBehaviorToViewers(client, stance);
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
			// #272: a 20-second rest cast is an animation on the actor - relayed, like every other player behaviour.
			var restCast = BehaviorControlCenterCodec.Build(record);
			await WorldIir.Send(client, restCast);
			await _broadcaster.BroadcastBehaviorToViewers(client, restCast);

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
