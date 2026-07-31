using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Economy
{
	// The personal-bank content record (BankIIR 0x0BA1C1CB) - kind-105 inventory container delivered on-demand when
	// the bank window opens, framed exactly like the FullChar 0x737A6DF9 snapshot ([subop][0xC350][instance][flag]
	// [protobuf container]). Decoded from NecroL80_..._Has-inventory-at-trader-bank (memory protocol_bank_storage);
	// golden-tested byte-exact against the captured bank (BankStorageTests). The slot-registration helper writes one
	// 0x201c bank-slot body per occupied slot, sent before the content (BankService.SendBankContent drives both).
	public static class BankIirCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.BankIIR;

		// The BankIIR record (everything after the 0x2000 packet's u32 length prefix).
		public static byte[] BuildBankIirRecord(uint charInstance, ProtoInventoryGame containerBlob)
		{
			return new FaolanStream().WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(charInstance).WriteByte(0x01).WriteArrayUInt32Length(containerBlob.ToProtobuf()).ToArray();
		}

		// One 0x201c bank-slot registration body (20 bytes): [u32 kind=105][u32 charInstance][u32 slot][u32 0x17]
		// [u16 0x0f][u16 recordId]. Sent once per occupied bank slot before the BankIIR content.
		public static void WriteBankSlotRegistration(FaolanStream s, uint charInstance, uint slot, ushort recordId)
		{
			s.WriteUInt32((uint)ContainerKind.Bank).WriteUInt32(charInstance).WriteUInt32(slot).WriteUInt32(0x17u).WriteUInt16(0x000F).WriteUInt16(recordId);
		}
	}
}
