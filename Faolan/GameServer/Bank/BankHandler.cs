using System.IO;
using System.Threading.Tasks;
using Faolan.Core.Data;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Protobuf;
using Faolan.GameServer.Packets.FullCharSnapshot;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;
using ProtoBuf;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	// Personal BANK storage (the separate storage opened at the trader/auctionhouse NPC). Decoded from
	// account-faolan-character-necrol80-login-Has-inventory-at-trader-bank (memory protocol_bank_storage): the bank
	// is just another inventory container - kind 105 (0x69) - delivered ON-DEMAND (NOT in the login FullChar
	// snapshot) when the bank is opened. The wire delivery mirrors the world-entry container handshake:
	//   1. 0x201c x N  - register each bank slot (on the GameCharAgent conversation, like the inventory/money path)
	//   2. BankIIR (0x0BA1C1CB) - the content, framed exactly like the FullChar 0x737A6DF9 snapshot:
	//        [u32 subop=0BA1C1CB][u32 type=0000C350][u32 charInstance][u8 flag=01][u32 BE blobLen][ProtoInventoryGame]
	//      (on the GameAgent->GameInterface world conversation). The container's items are standard
	//      InventoryListEntry entries - the same codec SnapshotItemBuilder already produces for the backpack.
	public partial class GameServerListener
	{
		private const uint BankIirSubOpcode = 0x0BA1C1CB; // BankIIR_t
		private const uint BankMaxSize = 50; // captured bank capacity
		private const uint BankDisabledSlots = 30; // captured disabled-slot count (unpurchased bank space)
		private const ushort BankRegisterOpcode = 0x201C;

		// The BankIIR record (everything after the 0x2000 packet's u32 length prefix). Golden-tested byte-exact
		// against the captured bank (BankStorageTests).
		internal static byte[] BuildBankIirRecord(uint charInstance, byte[] containerBlob)
		{
			return new FaolanStream()
				.WriteUInt32(BankIirSubOpcode)
				.WriteUInt32(0x0000C350u)
				.WriteUInt32(charInstance)
				.WriteByte((byte)0x01)
				.WriteUInt32((uint)containerBlob.Length)
				.WriteArray(containerBlob)
				.ToArray();
		}

		// One 0x201c bank-slot registration body (20 bytes): [u32 kind=105][u32 charInstance][u32 slot][u32 0x17]
		// [u16 0x0f][u16 recordId]. Sent once per occupied bank slot before the BankIIR content.
		internal static void WriteBankSlotRegistration(FaolanStream s, uint charInstance, uint slot, ushort recordId)
		{
			s
				.WriteUInt32(FullCharSnapshotBuilder.BankKind)
				.WriteUInt32(charInstance)
				.WriteUInt32(slot)
				.WriteUInt32(0x17u)
				.WriteUInt16((ushort)0x000F)
				.WriteUInt16(recordId);
		}

		private static byte[] SerializeContainer(ProtoInventoryGame container)
		{
			using var ms = new MemoryStream();
			Serializer.Serialize(ms, container);
			return ms.ToArray();
		}

		// Sends the bank: a 0x201c slot registration per occupied cell, then the BankIIR content. The container is
		// built from the character's CharacterInventory rows in the bank container (kind 105) via the proven
		// snapshot item codec.
		private async Task SendBank(INetworkClient client, ProtoInventoryGame bank)
		{
			var charInstance = client.Session.Character.Id;

			ushort recordId = 0x0A18; // sequential per-slot record id (session-volatile; matches the capture's range)
			foreach (var entry in bank.Items)
			{
				var ps = new PacketStream();
				ps.WriteHeader(LoginWorld201CSender, LoginWorld201CReceiver(client), BankRegisterOpcode);
				WriteBankSlotRegistration(ps, charInstance, entry.Pos, recordId++);
				await ps.SendAsync(client);
			}

			var record = BuildBankIirRecord(charInstance, SerializeContainer(bank));
			await new PacketStream()
				.WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), 0x2000)
				.WriteArrayUInt32Length(record)
				.SendAsync(client);

			Logger.LogInformation("Bank: sent {Count} items to {Character} (BankIIR, container kind {Kind}).",
				bank.Items.Count, client.Session.Character?.Name, FullCharSnapshotBuilder.BankKind);
		}
	}
}
