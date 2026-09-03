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
	// Personal BANK storage (the separate storage opened at the trader/auctionhouse NPC). Decoded from
	// NecroL80_mage-necromancer_login_Has-inventory-at-trader-bank (memory protocol_bank_storage): the bank
	// is just another inventory container - kind 105 (0x69) - delivered ON-DEMAND (NOT in the login FullChar
	// snapshot) when the bank is opened. The wire delivery mirrors the world-entry container handshake:
	//   1. 0x201c x N  - register each bank slot (on the GameCharAgent conversation, like the inventory/money path)
	//   2. BankIIR (0x0BA1C1CB) - the content, framed exactly like the FullChar 0x737A6DF9 snapshot:
	//        [u32 subop=0BA1C1CB][u32 type=0000C350][u32 charInstance][u8 flag=01][u32 BE blobLen][ProtoInventoryGame]
	//      (on the GameAgent->GameInterface world conversation). The container's items are standard
	//      InventoryListEntry entries - the same codec SnapshotItems already produces for the backpack.
	// Extracted from GameServerListener as a composed collaborator; the live item-grant outbound op (for a
	// withdraw) is reached through the shared IEconomyOutbound port (the listener implements it).
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

		// Rebuilds the personal bank container (kind 105) from the character's freshly-persisted bank items and
		// re-sends it. Reuses the same path as the .ah bank tab open.
		public async Task ResendBank(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			var stored = await _database.GetCharacterItems(characterId, ContainerKind.Bank);
			var bank = FullCharacterRoots.Container(ContainerKind.Bank, BankMaxSize, BankDisabledSlots, characterId, stored);
			await SendBank(client, bank);
		}

		// Sends the bank: a 0x201c slot registration per occupied cell, then the BankIIR content. The container is
		// built from the character's item instances in the bank container (kind 105) via the proven snapshot item
		// codec.
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

		// Live-renders a MoveItemInInventory that touches the personal bank (kind 105). PersistInventoryMove has
		// already written the new slots; the client, however, gets NO response to a bank move, so a deposit looks
		// like the item "disappeared" (it left the bag client-side but the bank tab never refreshed). The retail
		// deposit/withdraw response isn't captured yet, so this is a PoC: re-send the whole bank (BankIIR) to refresh
		// the bank tab, and on a withdraw (bank -> bag) also push the live DispatchAddItem (0x2020) so the item
		// renders in the bag. NEEDS in-client verification (does an already-open bank window re-read the BankIIR?).
		public async Task UpdateBankAfterMove(INetworkClient client, ProtoMoveInventoryItemIIR move)
		{
			var character = client.Session.Character;
			if (character == null || move?.SourceContainerId == null || move.DestinationContainerId == null)
				return;

			var sKind = (ContainerKind)move.SourceContainerId.Type;
			var dKind = (ContainerKind)move.DestinationContainerId.Type;
			if (sKind != ContainerKind.Bank && dKind != ContainerKind.Bank)
				return; // not a bank move - nothing to refresh

			// Refresh the bank tab from the just-persisted rows (covers deposit, withdraw, and in-bank rearrange).
			await ResendBank(client);

			// Withdraw (bank -> bag): the item now lives in the bag; push the live add so it renders there too.
			if (sKind == ContainerKind.Bank && dKind == ContainerKind.Bag)
			{
				var dPos = move.DestinationItemPos;
				var withdrawn = await _database.GetCharacterItemSlot(character.Id, ContainerKind.Bag, dPos);
				if (withdrawn == null)
					return;

				// The SAME instance that was in the bank - a withdrawal is a move, so the live add names it by its own
				// id and carries its crafter and its gems with it (#750).
				var entry = SnapshotItems.ListEntry(withdrawn, FullCharacterRoots.ItemId(withdrawn.Id), FullCharacterRoots.RecordId(character.Id, ContainerKind.Bag, (ulong)dPos)).Item;
				await _outbound.SendAddItem(client, ContainerKind.Bag, (uint)dPos, entry);
			}
		}
	}
}
