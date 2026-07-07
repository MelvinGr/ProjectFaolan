using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Opcodes;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Protobuf;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;
using ProtoBuf;
using Faolan.Core.Network.Codecs.GameServer.FullCharSnapshot;

namespace Faolan.GameServer.Economy.Bank
{
	// Personal BANK storage (the separate storage opened at the trader/auctionhouse NPC). Decoded from
	// NecroL80_mage-necromancer_login_Has-inventory-at-trader-bank (memory protocol_bank_storage): the bank
	// is just another inventory container - kind 105 (0x69) - delivered ON-DEMAND (NOT in the login FullChar
	// snapshot) when the bank is opened. The wire delivery mirrors the world-entry container handshake:
	//   1. 0x201c x N  - register each bank slot (on the GameCharAgent conversation, like the inventory/money path)
	//   2. BankIIR (0x0BA1C1CB) - the content, framed exactly like the FullChar 0x737A6DF9 snapshot:
	//        [u32 subop=0BA1C1CB][u32 type=0000C350][u32 charInstance][u8 flag=01][u32 BE blobLen][ProtoInventoryGame]
	//      (on the GameAgent->GameInterface world conversation). The container's items are standard
	//      InventoryListEntry entries - the same codec SnapshotItemBuilder already produces for the backpack.
	// Extracted from GameServerListener as a composed collaborator; the live item-grant outbound op (for a
	// withdraw) is reached through the shared IEconomyOutbound port (the listener implements it).
	public sealed class BankService
	{
		private const uint BankDisabledSlots = 30; // captured disabled-slot count (unpurchased bank space)

		private const uint BankIirSubOpcode = (uint)GameServer0X2000Opcodes.BankIIR; // BankIIR_t
		private const uint BankMaxSize = 50; // captured bank capacity
		private const ushort BankRegisterOpcode = 0x201C;
		private readonly IDatabaseRepository _database;
		private readonly IEconomyOutbound _outbound;
		private readonly ILogger _logger;

		public BankService(IDatabaseRepository database, ILogger logger, IEconomyOutbound outbound)
		{
			_database = database;
			_logger = logger;
			_outbound = outbound;
		}

		// Rebuilds the personal bank container (kind 105) from the character's freshly-persisted CharacterInventory
		// rows and re-sends it. Reuses the same path as the .ah bank tab open.
		public async Task ResendBank(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			var rows = await _database.GetCharacterInventory(characterId);
			var bankRows = rows.Where(r => r.Container == (int)FullCharSnapshotBuilder.BankKind).ToList();
			var bank = FullCharSnapshotBuilder.BuildContainer(FullCharSnapshotBuilder.BankKind, BankMaxSize, BankDisabledSlots, characterId, bankRows);
			await SendBank(client, bank);
		}

		// Sends the bank: a 0x201c slot registration per occupied cell, then the BankIIR content. The container is
		// built from the character's CharacterInventory rows in the bank container (kind 105) via the proven
		// snapshot item codec.
		public async Task SendBank(INetworkClient client, ProtoInventoryGame bank)
		{
			var charInstance = client.Session.Character.Id;

			ushort recordId = 0x0A18; // sequential per-slot record id (session-volatile; matches the capture's range)
			foreach (var entry in bank.Items)
			{
				var ps = new PacketStream();
				ps.WriteHeader(ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x58), ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, client.Session.Account.ClientInstance), BankRegisterOpcode);
				WriteBankSlotRegistration(ps, charInstance, entry.Pos, recordId++);
				await ps.SendAsync(client);
			}

			var record = BuildBankIirRecord(charInstance, SerializeContainer(bank));
			await new PacketStream().WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob).WriteArrayUInt32Length(record).SendAsync(client);

			_logger.LogInformation("Bank: sent {Count} items to {Character} (BankIIR, container kind {Kind}).", bank.Items.Count, client.Session.Character?.Name, FullCharSnapshotBuilder.BankKind);
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

			var sKind = (int)move.SourceContainerId.Type;
			var dKind = (int)move.DestinationContainerId.Type;
			const int bank = (int)FullCharSnapshotBuilder.BankKind;
			const int bag = (int)FullCharSnapshotBuilder.BagKind;
			if (sKind != bank && dKind != bank)
				return; // not a bank move - nothing to refresh

			// Refresh the bank tab from the just-persisted rows (covers deposit, withdraw, and in-bank rearrange).
			await ResendBank(client);

			// Withdraw (bank -> bag): the item now lives in the bag; push the live add so it renders there too.
			if (sKind == bank && dKind == bag)
			{
				var dPos = move.DestinationItemPos;
				var rows = await _database.GetCharacterInventory(character.Id);
				var row = rows.FirstOrDefault(r => r.Container == bag && r.Slot == dPos);
				if (row?.ItemId == null)
					return;

				var item = await _database.GetItem(row.ItemId.Value);
				if (item is not { Hash: not 0 })
					return;

				var entry = SnapshotItemBuilder.BuildListEntry((ulong)dPos,
						FullCharSnapshotBuilder.ItemId(character.Id, FullCharSnapshotBuilder.BagKind, (ulong)dPos),
						FullCharSnapshotBuilder.RecordId(character.Id, FullCharSnapshotBuilder.BagKind, (ulong)dPos),
						row.Count,
						item,
						item.Hash)
					.Item;
				await _outbound.SendAddItem(client, 0x68u, (uint)dPos, entry);
			}
		}

		// The BankIIR record (everything after the 0x2000 packet's u32 length prefix). Golden-tested byte-exact
		// against the captured bank (BankStorageTests).
		public static byte[] BuildBankIirRecord(uint charInstance, byte[] containerBlob)
		{
			return new FaolanStream().WriteUInt32(BankIirSubOpcode).WriteUInt32(0x0000C350u).WriteUInt32(charInstance).WriteByte(0x01).WriteUInt32(containerBlob.Length).WriteArray(containerBlob).ToArray();
		}

		// One 0x201c bank-slot registration body (20 bytes): [u32 kind=105][u32 charInstance][u32 slot][u32 0x17]
		// [u16 0x0f][u16 recordId]. Sent once per occupied bank slot before the BankIIR content.
		public static void WriteBankSlotRegistration(FaolanStream s, uint charInstance, uint slot, ushort recordId)
		{
			s.WriteUInt32(FullCharSnapshotBuilder.BankKind).WriteUInt32(charInstance).WriteUInt32(slot).WriteUInt32(0x17u).WriteUInt16(0x000F).WriteUInt16(recordId);
		}

		private static byte[] SerializeContainer(ProtoInventoryGame container)
		{
			using var ms = new MemoryStream();
			Serializer.Serialize(ms, container);
			return ms.ToArray();
		}
	}
}
