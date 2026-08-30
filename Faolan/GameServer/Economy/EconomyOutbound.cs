using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Inventory;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Protobuf;
using ProtoBuf;
using Faolan.GameServer.Inventory;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Economy
{
	/// <summary>
	///     The wallet + item-grant + inventory WIRE (#826): what ShopService, BankService, TradepostService,
	///     GuildBankService and TradeskillService reach back to, off GameServerListener.
	/// </summary>
	/// <remarks>
	///     Moved only once <see cref="ConnectionRegistry" /> existed - the ops addressed by CHARACTER ID need the
	///     connection lookup, which was the one thing keeping this code on the listener.
	///     <para>
	///         The two-layer shape is DELIBERATE and is the listener's own: a sender (several of them static, since
	///         they hold no state) plus a thin explicit interface member forwarding to it. The port's signatures are
	///         not all the senders' - <c>GrantItemToBackpack</c> takes a `transferring` argument the interface does not
	///         name - so collapsing the two would change what the senders can express.
	///     </para>
	/// </remarks>
	public sealed class EconomyOutbound : IEconomyOutbound
	{
		private readonly ConnectionRegistry _connections;
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;
		// The one op this needs from the quest engine, and a genuine CYCLE: QuestProgressionService takes
		// IEconomyOutbound in its own ctor. It is a post-grant NOTIFICATION, so it is a single-op seam bound right
		// after that service exists - the same shape (and the same reason) as WorldSimulation's outbound.
		private Func<INetworkClient, uint, uint, Task> _advanceCollectGoals = null!;

		public EconomyOutbound(ConnectionRegistry connections, IDatabaseRepository database, ILogger logger)
		{
			_connections = connections;
			_database = database;
			_logger = logger;
		}

		// Bound in the listener ctor immediately after QuestProgressionService is built, before anything can grant.
		internal void BindCollectGoals(Func<INetworkClient, uint, uint, Task> advanceCollectGoals)
		{
			_advanceCollectGoals = advanceCollectGoals;
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
		public async Task<int?> GrantItemToBackpack(INetworkClient client, uint itemTemplateId, uint count = 1, int? preferredSlot = null, string? creatorName = null,
			ItemInstance? transferring = null)
		{
			// ⚠️ Every refusal below says WHY. This method used to return a bare `null` from three different places -
			// no template, no free slot, the delivery itself failing - and the caller could not tell them apart, so a
			// grant that silently did nothing looked identical to a grant that had never been asked for. #824 is what
			// that costs: harvesting a `Spool of Thread` played the whole node animation and put nothing in the bag,
			// and the reason (its template is not in `Items`) appeared in no log anywhere.
			var item = await _database.GetItem(itemTemplateId);
			if (item == null)
			{
				// A DATA gap rather than a player condition, so it is a warning: something in the world - a quest
				// goal, a loot table, a harvest node - is pointing at a template we do not hold. Almost always an
				// item no capture ever showed, whose TEA key is therefore unrecoverable (#203), which is why the row
				// cannot simply be added.
				_logger.LogWarning("Grant refused for {Char}: item template {Template} is not in Items, so there is nothing to hand over.",
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
				_logger.LogInformation("Grant refused for {Char}: no free {Kind} slot for item {Template} ({Name}).",
					client.Session.Character.Name, kind, itemTemplateId, item.Name);
				return null;
			}

			// The instance the client is about to be told about, and the id the live add and the next login's snapshot
			// both name it by (#750). The two used to agree via a formula over (instance, kind, slot); they agree by
			// identity now. A GRANT mints it here; a HAND-OVER moves the existing one, keeping everything hanging off it.
			var delivered = transferring != null
				? await _database.TransferCharacterItem(transferring.Id, characterId, kind, slot.Value)
				: await _database.SetCharacterItemSlot(characterId, kind, slot.Value, itemTemplateId, count, creatorName);
			if (delivered == null)
			{
				// A free slot was found and the write still did not produce a row - so this one is neither a data gap
				// nor a full bag, and it is the only one of the three that should never happen.
				_logger.LogWarning("Grant failed for {Char}: {Kind}[{Slot}] was free and item {Template} ({Name}) still did not persist.",
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
			await _advanceCollectGoals(client, itemTemplateId, count);
			return slot;
		}

		// Pushes one item into the client's inventory live, on the same GameCharAgent conversation the wallet
		// updates (0x2005) ride.
		public static async Task SendAddItem(INetworkClient client, ContainerKind containerType, uint itemPos, ProtoInventoryGameEntry entry, uint feedback = 0)
		{
			var charId = client.Session.Character.Id;

			await client.SendAsync(new Rpc(GameServerListener.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchAddItem,
				AddItemCodec.Build(new AddItem(containerType, charId, itemPos, entry, feedback))));
		}

		// Pushes the character's wallet balance, framed on the same login-world conversation as the 0x201C
		// record stream (the retail money updates ride that conversation).
		public async Task SendUpdateCash(INetworkClient client, ulong balanceTin, UpdateCashReason reason)
		{
			await client.SendAsync(new Rpc(GameServerListener.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchUpdateCash,
				UpdateCashCodec.Build(new UpdateCash(balanceTin, reason))));

			_logger.LogInformation("UpdateCash: {Balance} tin ({Reason}) -> {Character}.", balanceTin, reason, client.Session.Character?.Name);
		}

		// A gem went into a socket (#754). Retail's answer to the socketing request is exactly two messages:
		// DispatchAddToItemSlot for the cell that RECEIVED the gem, then DispatchRemoveItem for the cell the gem
		// left. The socketed item's own container entry is NOT re-sent - the client updates it in place from the
		// first message, which is why nothing here rebuilds the inventory.
		// [V2: NecroL80_mage-necromancer_login_gameplay_annotated_8.tshark.json fr8248 - both in the same frame]
		public async Task SendGemSocketed(INetworkClient client, ContainerKind container, uint targetSlot, ItemInstanceSocket socket, uint gemSlot)
		{
			var charId = client.Session.Character.Id;
			await client.SendAsync(new Rpc(GameServerListener.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), AddToItemSlotCodec.Method,
				AddToItemSlotCodec.Build(container, charId, targetSlot, socket)));

			await SendRemoveItem(client, container, gemSlot);
		}

		// Closes a craft: retail's last word on the transaction, after the product add and the ingredient removals.
		// The body is the RECIPE ID and nothing else - the capture's `0000000F` is recipe 15, whose own rows are Plain
		// Hilt Piece x1 + Copper x4 + Silver x4 -> Bloodsilver Khopesh, matching the removals that precede it.
		// [V2: NecroL80_mage-necromancer_login_gameplay_annotated_5 fr17402]
		//
		// Sent ONLY on success. Retail has no failure form here, and a completion over a craft that did not happen
		// would tell the client to close its window on an item it never got.
		public static Task SendTradeskillComplete(INetworkClient client, uint recipeId)
		{
			return client.SendAsync(new Rpc(GameServerListener.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchTradeskillComplete,
				b => b.WriteUInt32(recipeId)));
		}

		// The GUILD BANK vault (#654), pushed as part of the banker open. The same container builder the personal
		// bank uses, with the guild id where a character id would go.
		public static Task SendGuildBankData(INetworkClient client, ProtoInventoryGame vault)
		{
			return client.SendAsync(new Rpc(GameServerListener.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GuildBankCodec.Method,
				GuildBankCodec.Build(vault)));
		}

		// The echo acknowledging a guild-bank item move - the request's own six values, raw.
		public static Task SendGuildBankMove(INetworkClient client, GuildBankMove move)
		{
			return client.SendAsync(new Rpc(GameServerListener.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GuildBankMoveCodec.Method,
				GuildBankMoveCodec.Build(move)));
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
		public async Task<uint> TakeItemsFromInventory(INetworkClient client, uint itemTemplateId, uint count)
		{
			if (count == 0)
				return 0;

			var characterId = client.Session.Character.Id;
			var steps = InventoryTake.Plan(await _database.GetCharacterItems(characterId), itemTemplateId, count);

			foreach (var step in steps)
			{
				// A partial take DECREMENTS the item in place - the crafter and any socketed gems stay on it because the
				// row is not re-minted (#750).
				if (step.Remaining == 0)
					await _database.SetCharacterItemSlot(characterId, step.Container, step.Slot, null);
				else
					await _database.SetCharacterItemCount(characterId, step.Container, step.Slot, step.Remaining);

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

		// Pushes a live item removal to the client (the inverse of SendAddItem).
		public static async Task SendRemoveItem(INetworkClient client, ContainerKind containerType, uint itemPos, uint amountToRemove = 0)
		{
			var charId = client.Session.Character.Id;

			await client.SendAsync(new Rpc(GameServerListener.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchRemoveItem,
				RemoveItemCodec.Build(new RemoveItem(containerType, charId, itemPos, amountToRemove))));
		}


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


		Task<uint> IEconomyOutbound.TakeItemsFromInventory(INetworkClient client, uint itemTemplateId, uint count)
		{
			return TakeItemsFromInventory(client, itemTemplateId, count);
		}

		// Was ITradeOutbound's, reached from IEconomyOutbound through `((ITradeOutbound)this)`. Both ports declare the
		// same op and both halves it needs are at THIS layer, so it is implemented once here and TradeOutbound forwards
		// to it - which also retires the cast, the shape that broke PushVitals when its port moved (d66482f1).
		Task<int?> IEconomyOutbound.TransferItemToCharacter(uint characterId, ItemInstance instance)
		{
			return TransferItemToCharacter(characterId, instance);
		}

		public async Task<int?> TransferItemToCharacter(uint characterId, ItemInstance instance)
		{
			return _connections.ForCharacter(characterId) is { } client
				? await GrantItemToBackpack(client, instance.TemplateId, instance.Count, transferring: instance)
				: null;
		}


		public async Task<int> BackpackCapacity(uint characterId)
		{
			var bag = await _database.GetCharacterItemSlot(characterId, ContainerKind.Equipped, (int)CharacterEquipmentSlot.Bag);
			return (int)(FullCharacterRoots.BaseBackpackSlots + (bag?.Template.BagExtraSlots ?? 0));
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
		public async Task<int?> FindFreeContainerSlot(uint characterId, ContainerKind container, int capacity, int? preferred = null)
		{
			if (preferred is { } slot && (slot < 0 || slot >= capacity))
				throw new ArgumentOutOfRangeException(nameof(preferred), slot, $"a requested {container} slot outside 0..{capacity - 1} cannot come from the client - the body was misread.");

			var taken = (await _database.GetCharacterItems(characterId, container)).Select(i => i.Slot).ToHashSet();
			if (preferred is { } wanted && !taken.Contains(wanted))
				return wanted;

			return Enumerable.Range(0, capacity).Where(s => !taken.Contains(s)).Cast<int?>().FirstOrDefault();
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
		public async Task SendUpdateInventory(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			var containers = FullCharacterRoots.Containers(characterId, await _database.GetCharacterItems(characterId));

			// containers[0] = Equipped(101), [1] = Bag(104) - the two retail pushes. The Resource and Quest containers
			// it also builds are NOT pushed: the capture carries exactly these two.
			foreach (var container in containers.Take(2))
			{
				using var ms = new MemoryStream();
				Serializer.Serialize(ms, container);
				var payload = ms.ToArray();

				await client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
					s => s.WriteArrayUInt32Length(UpdateInventoryCodec.Build(new UpdateInventoryIIR(characterId, 0x00, payload)))));
			}

			_logger.LogDebug("Pushed UpdateInventoryIIR for {Character}: Equipped + Bag (#380).", client.Session.Character.Name);
		}
	}
}
