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
using Faolan.GameServer.Wire;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Economy
{
	/// <summary>
	///     The wallet, item-grant and inventory wire: what <see cref="ShopService" />, <see cref="BankService" />,
	///     <see cref="TradepostService" />, <see cref="GuildBankService" /> and <see cref="TradeskillService" /> reach
	///     back to.
	/// </summary>
	/// <remarks>
	///     The ops addressed by character id resolve the connection through <see cref="ConnectionRegistry" />.
	///     <para>
	///         The two-layer shape is deliberate: a sender - several of them static, since they hold no state - plus a
	///         thin explicit interface member forwarding to it. The port's signatures are not all the senders':
	///         <see cref="GrantItemToBackpack" /> takes a <c>transferring</c> argument the interface does not name, so
	///         collapsing the two would change what the senders can express.
	///     </para>
	/// </remarks>
	public sealed class EconomyOutbound : IEconomyOutbound
	{
		private readonly ConnectionRegistry _connections;
		private readonly ICatalogRepository _catalog;
		private readonly ICharacterInventoryRepository _inventory;
		private readonly ILogger _logger;
		// The one op this needs from the quest engine, and a genuine cycle: QuestProgressionService takes
		// IEconomyOutbound in its own constructor. It is a post-grant notification, so it is a single-op seam bound
		// right after that service exists - the same shape, and the same reason, as WorldSimulation's outbound.
		private Func<INetworkClient, uint, uint, Task> _advanceCollectGoals = null!;

		public EconomyOutbound(ConnectionRegistry connections, ICatalogRepository catalog, ICharacterInventoryRepository inventory, ILogger logger)
		{
			_connections = connections;
			_catalog = catalog;
			_inventory = inventory;
			_logger = logger;
		}

		// Bound in the listener constructor immediately after QuestProgressionService is built, before anything can
		// grant.
		internal void BindCollectGoals(Func<INetworkClient, uint, uint, Task> advanceCollectGoals)
		{
			_advanceCollectGoals = advanceCollectGoals;
		}

		/// <summary>
		///     Grants an item to the player's backpack: persists it to a free inventory bag cell, so it survives relog
		///     through the world-entry snapshot, and pushes the live DispatchAddItem so it appears immediately.
		/// </summary>
		/// <param name="preferredSlot">
		///     Honoured only for the backpack: a quest item routes to its own container, where a bag slot index means
		///     nothing.
		/// </param>
		/// <param name="transferring">
		///     Turns this from a mint into a hand-over: the delivery, the slot search, the live add, the
		///     quest-container routing and the collect-goal hook are all identical, but the named instance moves into
		///     the found cell instead of a new one being created. That is what a player trade is, and re-minting there
		///     would destroy the item's crafter and its socketed gems. It is a parameter rather than a second method so
		///     that the choke point stays one path.
		/// </param>
		/// <returns>The bag slot it landed in, or null when the template is unknown or the bag is full.</returns>
		/// <remarks>
		///     The live add is unconditional, loot included: both a loot-all (c2s 0x2043) and a single drag (c2s
		///     0x2046) are answered by retail with three 0x201c DispatchSetItemStat and a 0x2020 DispatchAddItem, and
		///     persisting without the live add leaves the items un-takeable in-client.
		/// </remarks>
		public async Task<int?> GrantItemToBackpack(INetworkClient client, uint itemTemplateId, uint count = 1, int? preferredSlot = null, string? creatorName = null,
			ItemInstance? transferring = null)
		{
			// Every refusal below says why, because the three - no template, no free slot, the delivery itself failing
			// - are indistinguishable to the caller, which gets a bare null. Without the log, a grant that silently
			// did nothing looks identical to one that was never asked for.
			var item = await _catalog.GetItem(itemTemplateId);
			if (item == null)
			{
				// A data gap rather than a player condition, so it is a warning: something in the world - a quest goal,
				// a loot table, a harvest node - points at a template the catalog does not hold. Almost always an item
				// no capture ever showed, whose TEA key is therefore unrecoverable, which is why the row cannot simply
				// be added.
				_logger.LogWarning("Grant refused for {Char}: item template {Template} is not in Items, so there is nothing to hand over.",
					client.Session.Character.Name, itemTemplateId);
				return null;
			}

			var characterId = client.Session.Character.Id;

			// Quest items (Type 113, the Scavenger's Key for one) route to the quest container rather than the
			// backpack, so they land in the player's quest-items tab.
			//
			// There is no hash gate: the world-entry snapshot renders an item with `Hash != 0`, and every item has a
			// token, harvested or minted (`AcgHash.ForWire`), so a template no capture ever showed entering an
			// inventory is still deliverable.
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
				// An ordinary player condition rather than a defect, so it is informational. It is still stated,
				// because "nothing arrived" reads the same to the player either way and the two need different answers
				// from whoever reads the log.
				_logger.LogInformation("Grant refused for {Char}: no free {Kind} slot for item {Template} ({Name}).",
					client.Session.Character.Name, kind, itemTemplateId, item.Name);
				return null;
			}

			// The instance the client is about to be told about, and the id both the live add and the next login's
			// snapshot name it by. A grant mints it here; a hand-over moves the existing one, keeping everything
			// hanging off it.
			var delivered = transferring != null
				? await _inventory.TransferCharacterItem(transferring.Id, characterId, kind, slot.Value)
				: await _inventory.SetCharacterItemSlot(characterId, kind, slot.Value, itemTemplateId, count, creatorName);
			if (delivered == null)
			{
				// A free slot was found and the write still did not produce a row, so this is neither a data gap nor a
				// full bag - the only one of the three that should never happen.
				_logger.LogWarning("Grant failed for {Char}: {Kind}[{Slot}] was free and item {Template} ({Name}) still did not persist.",
					client.Session.Character.Name, kind, slot.Value, itemTemplateId, item.Name);
				return null;
			}

			// Live DispatchAddItem so the item appears without a relog. Quest items go to the quest container (0x71),
			// matching retail's chain-capture delivery; everything else to the backpack (0x68).
			var containerType = isQuestItem
				? ContainerKind.Quest
				: ContainerKind.Bag;
			// The live-add stat list rather than the snapshot's - retail's two shapes differ - and the instance
			// overload, because a hand-over can carry an item already bound to its previous owner and only the row
			// knows that. Spreading it back into loose scalars loses the crafter, the sockets and the bound bit.
			var entry = SnapshotItems.LiveAddEntry(delivered, FullCharacterRoots.ItemId(delivered.Id),
				FullCharacterRoots.RecordId(characterId, kind, (ulong)slot.Value));
			await SendAddItem(client, containerType, (uint)slot.Value, entry);

			// The item is now in the player's inventory, so any collect objective asking for it ticks, however it got
			// here: quest-pickup node, corpse bag, vendor, harvest, claim or GM command. This is the one choke point
			// every grant passes through, which is why the hook lives here rather than in each caller.
			await _advanceCollectGoals(client, itemTemplateId, count);
			return slot;
		}

		/// <summary>
		///     Pushes one item into the client's inventory live, on the same GameCharAgent conversation the wallet
		///     updates (0x2005) ride.
		/// </summary>
		public static async Task SendAddItem(INetworkClient client, ContainerKind containerType, uint itemPos, ProtoInventoryGameEntry entry, uint feedback = 0)
		{
			var charId = client.Session.Character.Id;

			await client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchAddItem,
				AddItemCodec.Build(new AddItem(containerType, charId, itemPos, entry, feedback))));
		}

		/// <summary>
		///     Pushes the character's wallet balance, framed on the same login-world conversation as the 0x201C record
		///     stream, which is where retail's money updates ride.
		/// </summary>
		public async Task SendUpdateCash(INetworkClient client, ulong balanceTin, UpdateCashReason reason)
		{
			await client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchUpdateCash,
				UpdateCashCodec.Build(new UpdateCash(balanceTin, reason))));

			_logger.LogInformation("UpdateCash: {Balance} tin ({Reason}) -> {Character}.", balanceTin, reason, client.Session.Character?.Name);
		}

		/// <summary>
		///     A gem went into a socket. Retail's answer to the socketing request is exactly two messages:
		///     DispatchAddToItemSlot for the cell that received the gem, then DispatchRemoveItem for the cell the gem
		///     left.
		/// </summary>
		/// <remarks>
		///     The socketed item's own container entry is not re-sent - the client updates it in place from the first
		///     message - which is why nothing here rebuilds the inventory.
		///     [V2: NecroL80_mage-necromancer_login_gameplay_annotated_8.tshark.json fr8248 - both in the same frame]
		/// </remarks>
		public static async Task SendGemSocketed(INetworkClient client, ContainerKind container, uint targetSlot, ItemInstanceSocket socket, uint gemSlot)
		{
			var charId = client.Session.Character.Id;
			await client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), AddToItemSlotCodec.Method,
				AddToItemSlotCodec.Build(container, charId, targetSlot, socket)));

			await SendRemoveItem(client, container, gemSlot);
		}

		/// <summary>
		///     Closes a craft: retail's last word on the transaction, after the product add and the ingredient
		///     removals. The body is the recipe id and nothing else.
		/// </summary>
		/// <remarks>
		///     The capture's <c>0000000F</c> is recipe 15, whose own rows are Plain Hilt Piece x1 + Copper x4 +
		///     Silver x4 to a Bloodsilver Khopesh, matching the removals that precede it.
		///     [V2: NecroL80_mage-necromancer_login_gameplay_annotated_5 fr17402]
		///     <para>
		///         Sent only on success. Retail has no failure form here, and a completion over a craft that did not
		///         happen would tell the client to close its window on an item it never got.
		///     </para>
		/// </remarks>
		public static Task SendTradeskillComplete(INetworkClient client, uint recipeId)
		{
			return client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchTradeskillComplete,
				b => b.WriteUInt32(recipeId)));
		}

		/// <summary>
		///     The guild-bank vault, pushed as part of the banker open. The same container builder the personal bank
		///     uses, with the guild id where a character id would go.
		/// </summary>
		public static Task SendGuildBankData(INetworkClient client, ProtoInventoryGame vault)
		{
			return client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GuildBankCodec.Method,
				GuildBankCodec.Build(vault)));
		}

		/// <summary>The echo acknowledging a guild-bank item move: the request's own six values, raw.</summary>
		public static Task SendGuildBankMove(INetworkClient client, GuildBankMove move)
		{
			return client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GuildBankMoveCodec.Method,
				GuildBankMoveCodec.Build(move)));
		}

		/// <summary>
		///     Takes <paramref name="count" /> of an item template out of the player's inventory - the inverse of
		///     <see cref="GrantItemToBackpack" />, used by a quest hand-in.
		/// </summary>
		/// <returns>How many were actually removed.</returns>
		/// <remarks>
		///     <para>
		///         Deliberately not backpack-only, unlike <see cref="RemoveItemFromBackpack" />: a quest item lives in
		///         the Quest container (0x71), so a bag-only walk would report success while removing nothing, and the
		///         delivery check that gates this sums the same way, across every container.
		///     </para>
		///     <para>
		///         It removes exactly <paramref name="count" />: a slot holding more than is still wanted is
		///         decremented, never cleared, so handing in 3 of 5 bricks leaves 2. Slots are taken in order, and a
		///         partial take is reported rather than tolerated - the caller has already verified the player holds
		///         enough, so a short result means the inventory changed underneath it.
		///     </para>
		/// </remarks>
		public async Task<uint> TakeItemsFromInventory(INetworkClient client, uint itemTemplateId, uint count)
		{
			if (count == 0)
				return 0;

			var characterId = client.Session.Character.Id;
			var steps = InventoryTake.Plan(await _inventory.GetCharacterItems(characterId), itemTemplateId, count);

			foreach (var step in steps)
			{
				// A partial take decrements the item in place, so the crafter and any socketed gems stay on it: the row
				// is not re-minted.
				if (step.Remaining == 0)
					await _inventory.SetCharacterItemSlot(characterId, step.Container, step.Slot, null);
				else
					await _inventory.SetCharacterItemCount(characterId, step.Container, step.Slot, step.Remaining);

				await SendRemoveItem(client, step.Container, (uint)step.Slot, step.Taken);
			}

			var removed = InventoryTake.Total(steps);
			if (removed > 0)
			{
				// Same follow-up as RemoveItemFromBackpack, and for the same reason: this is a server-initiated
				// removal, which retail follows with both containers in full.
				await SendUpdateInventory(client);
			}

			return removed;
		}

		/// <summary>Pushes a live item removal to the client - the inverse of <see cref="SendAddItem" />.</summary>
		public static async Task SendRemoveItem(INetworkClient client, ContainerKind containerType, uint itemPos, uint amountToRemove = 0)
		{
			var charId = client.Session.Character.Id;

			await client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchRemoveItem,
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

		// Both ports declare this op and both halves it needs are at this layer, so it is implemented once here and
		// TradeOutbound forwards to it.
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
			var bag = await _inventory.GetCharacterItemSlot(characterId, ContainerKind.Equipped, (int)CharacterEquipmentSlot.Bag);
			return (int)(FullCharacterRoots.BaseBackpackSlots + (bag?.Template.BagExtraSlots ?? 0));
		}

		/// <summary>
		///     The first free cell (0..capacity-1) in <paramref name="container" />, skipping persisted rows.
		/// </summary>
		/// <param name="preferred">
		///     The slot the player dropped the item on - the loot window's drag target.
		/// </param>
		/// <exception cref="ArgumentOutOfRangeException">
		///     <paramref name="preferred" /> is outside the container. The client cannot drop an item outside the
		///     backpack, so an out-of-range slot means the wire was misread, and treating it as "first free" would make
		///     a mis-parsed body look like a working loot.
		/// </exception>
		/// <remarks>
		///     An occupied preferred slot falls through to the first free one, which is a game rule rather than a
		///     fallback: the player did drop it there, the bag has already handed the item over, and neither losing the
		///     item nor overwriting what is in the slot is defensible.
		/// </remarks>
		public async Task<int?> FindFreeContainerSlot(uint characterId, ContainerKind container, int capacity, int? preferred = null)
		{
			if (preferred is { } slot && (slot < 0 || slot >= capacity))
				throw new ArgumentOutOfRangeException(nameof(preferred), slot, $"a requested {container} slot outside 0..{capacity - 1} cannot come from the client - the body was misread.");

			var taken = (await _inventory.GetCharacterItems(characterId, container)).Select(i => i.Slot).ToHashSet();
			if (preferred is { } wanted && !taken.Contains(wanted))
				return wanted;

			return Enumerable.Range(0, capacity).Where(s => !taken.Contains(s)).Cast<int?>().FirstOrDefault();
		}

		/// <summary>
		///     Pushes the s2c UpdateInventoryIIR (0xA74D2814) for the player's own Equipped (101) and Bag (104)
		///     containers in full - retail's "here is your inventory again" after the server changes it behind the
		///     player's back.
		/// </summary>
		/// <remarks>
		///     <para>
		///         Decoded from Hoxxling_mage-heraldofxotli_login_gameplay2, where the Slave Manacles come off:
		///     </para>
		///     <code>
		///     f5109 c2s ScryScriptIIR "ShacklesOff"
		///     f5115 s2c DispatchRemoveItem   [container 101][char][pos 0x12][amount 0]
		///     f5118 s2c UpdateInventoryIIR   kind=101 Equipped cap=1028   (522 B)
		///     f5118 s2c UpdateInventoryIIR   kind=104 Bag      cap=24     (1141 B)
		///     </code>
		///     <para>
		///         That is a server-initiated removal followed by both containers whole, in the same frame. It is not
		///         the answer to the c2s probe of the same opcode - retail answers those never, 0 replies to 34
		///         requests.
		///     </para>
		///     <para>
		///         The payload is plain ProtoInventoryGame, the same schema the login snapshot carries, so it is built
		///         by the same container builder rather than a second inventory serialiser - capacities included
		///         (Equipped 1028, Bag 24 plus the equipped bag's extra slots), which is what the capture shows.
		///     </para>
		/// </remarks>
		public async Task SendUpdateInventory(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			var containers = FullCharacterRoots.Containers(characterId, await _inventory.GetCharacterItems(characterId));

			// containers[0] is Equipped (101) and [1] is Bag (104) - the two retail pushes. The Resource and Quest
			// containers the builder also produces are not pushed: the capture carries exactly these two.
			foreach (var container in containers.Take(2))
			{
				using var ms = new MemoryStream();
				Serializer.Serialize(ms, container);
				var payload = ms.ToArray();

				await client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
					s => s.WriteArrayUInt32Length(UpdateInventoryCodec.Build(new UpdateInventoryIIR(characterId, 0x00, payload)))));
			}

			_logger.LogDebug("Pushed UpdateInventoryIIR for {Character}: Equipped + Bag (#380).", client.Session.Character.Name);
		}

		/// <summary>
		///     The backpack's base slot count. The full size is this plus the equipped bag's
		///     <c>Item.BagExtraSlots</c> (equip slot 3), the same sizing the snapshot uses, so a grown bag can
		///     actually be filled.
		/// </summary>
		public const uint BaseBackpackSlots = 24;

		/// <summary>
		///     Removes an item from the player's backpack: deletes the persisted bag item, so it stays gone across
		///     relog, and pushes the live DispatchRemoveItem so it disappears immediately. The mirror of
		///     <see cref="GrantItemToBackpack" />.
		/// </summary>
		/// <returns>True when an item was there to remove.</returns>
		public async Task<bool> RemoveItemFromBackpack(INetworkClient client, int slot, uint amountToRemove = 0)
		{
			var characterId = client.Session.Character.Id;
			if (await _inventory.GetCharacterItemSlot(characterId, ContainerKind.Bag, slot) == null)
				return false;

			await _inventory.SetCharacterItemSlot(characterId, ContainerKind.Bag, slot, null);
			await SendRemoveItem(client, ContainerKind.Bag, (uint)slot, amountToRemove);

			// Retail follows a server-initiated removal with both containers in full, which is exactly this case: the
			// server took something out of the bag, the player did not move it, and that is the shape the ShacklesOff
			// capture shows. A player-initiated move is not followed by one, because the client already knows.
			await SendUpdateInventory(client);
			return true;
		}
	}
}
