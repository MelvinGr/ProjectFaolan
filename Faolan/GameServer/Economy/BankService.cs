using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Wire;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Economy
{
	/// <summary>
	///     Personal bank storage - the separate storage opened at the trader or auction-house NPC.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Decoded from NecroL80_mage-necromancer_login_Has-inventory-at-trader-bank: the bank is another
	///         inventory container, kind 105 (0x69), delivered on demand when the bank is opened rather than in the
	///         login FullChar snapshot. The wire delivery mirrors the world-entry container handshake - first one
	///         0x201c per bank slot, registering it on the GameCharAgent conversation the way the inventory and money
	///         paths do, then BankIIR (0x0BA1C1CB) carrying the content, framed exactly like the FullChar 0x737A6DF9
	///         snapshot:
	///     </para>
	///     <code>
	///     [u32 subop=0BA1C1CB][u32 type=0000C350][u32 charInstance][u8 flag=01][u32 BE blobLen][ProtoInventoryGame]
	///     </code>
	///     <para>
	///         That second message rides the GameAgent-to-GameInterface world conversation, and the container's items
	///         are standard InventoryListEntry entries - the same ones <c>SnapshotItems</c> produces for the backpack.
	///         The live item-grant op a withdraw needs is reached through the shared
	///         <see cref="IEconomyOutbound" /> port.
	///     </para>
	/// </remarks>
	public sealed class BankService
	{
		private const uint BankDisabledSlots = 30; // captured disabled-slot count (unpurchased bank space)

		private const uint BankMaxSize = 50; // captured bank capacity
		private readonly ICharacterInventoryRepository _database;
		private readonly IEconomyOutbound _outbound;
		private readonly ILogger _logger;

		public BankService(ICharacterInventoryRepository database, ILogger logger, IEconomyOutbound outbound)
		{
			_database = database;
			_logger = logger;
			_outbound = outbound;
		}

		/// <summary>
		///     Rebuilds the personal bank container (kind 105) from the character's freshly persisted bank items and
		///     re-sends it, down the same path as opening the bank tab.
		/// </summary>
		public async Task ResendBank(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			var stored = await _database.GetCharacterItems(characterId, ContainerKind.Bank);
			var bank = FullCharacterRoots.Container(ContainerKind.Bank, BankMaxSize, BankDisabledSlots, characterId, stored);
			await SendBank(client, bank);
		}

		/// <summary>
		///     Sends the bank: a 0x201c slot registration per occupied cell, then the BankIIR content, built from the
		///     character's item instances in the bank container (kind 105).
		/// </summary>
		public async Task SendBank(INetworkClient client, ProtoInventoryGame bank)
		{
			var charInstance = client.Session.Character.Id;

			ushort recordId = 0x0A18; // sequential per-slot record id (session-volatile; matches the capture's range)
			foreach (var entry in bank.Items)
			{
				var slotRecordId = recordId++;
				await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x58), client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchSetItemStat,
					SetItemStatCodec.Build(SetItemStats.SlotRegistration(charInstance, entry.Pos, slotRecordId))));
			}

			var record = BankCodec.Build(new BankIIR(charInstance, bank));
			await client.SendAsync(new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(record)));

			_logger.LogInformation("Bank: sent {Count} items to {Character} (BankIIR, container kind {Kind}).", bank.Items.Count, client.Session.Character?.Name, ContainerKind.Bank);
		}

		/// <summary>
		///     Live-renders a MoveItemInInventory that touches the personal bank (kind 105).
		/// </summary>
		/// <remarks>
		///     The move's new slots are already persisted, but the client gets no response of its own to a bank move,
		///     so without this a deposit looks like the item disappeared: it leaves the bag client-side and the bank
		///     tab never refreshes. Retail's own deposit and withdraw responses are not captured, so this re-sends the
		///     whole bank (BankIIR) to refresh the tab and, on a withdraw from bank to bag, also pushes the live
		///     DispatchAddItem (0x2020) so the item renders in the bag.
		/// </remarks>
		public async Task UpdateBankAfterMove(INetworkClient client, ProtoMoveInventoryItemIIR move)
		{
			var character = client.Session.Character;
			if (character == null || move?.SourceContainerId == null || move.DestinationContainerId == null)
				return;

			var sKind = (ContainerKind)move.SourceContainerId.Type;
			var dKind = (ContainerKind)move.DestinationContainerId.Type;
			if (sKind != ContainerKind.Bank && dKind != ContainerKind.Bank)
				return; // not a bank move, so there is nothing to refresh

			// Refresh the bank tab from the just-persisted rows: deposit, withdraw and in-bank rearrange alike.
			await ResendBank(client);

			// Withdraw (bank to bag): the item now lives in the bag, so push the live add to render it there.
			if (sKind == ContainerKind.Bank && dKind == ContainerKind.Bag)
			{
				var dPos = move.DestinationItemPos;
				var withdrawn = await _database.GetCharacterItemSlot(character.Id, ContainerKind.Bag, dPos);
				if (withdrawn == null)
					return;

				// The same instance that was in the bank: a withdrawal is a move, so the live add names it by its own
				// id and carries its crafter and its gems with it.
				var entry = SnapshotItems.ListEntry(withdrawn, FullCharacterRoots.ItemId(withdrawn.Id), FullCharacterRoots.RecordId(character.Id, ContainerKind.Bag, (ulong)dPos)).Item;
				await _outbound.SendAddItem(client, ContainerKind.Bag, (uint)dPos, entry);
			}
		}
	}
}
