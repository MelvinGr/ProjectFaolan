using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Economy
{
	// BankIIR (0x0BA1C1CB) - the personal-bank content record: the kind-105 inventory container, delivered on demand
	// when the bank window opens and framed exactly like the FullChar 0x737A6DF9 snapshot:
	//   [subop][class=0xC350][charInstance][u8 flag][u32 containerLen][ProtoInventoryGame]
	// Decoded from NecroL80_..._Has-inventory-at-trader-bank (memory protocol_bank_storage); golden-tested byte-exact
	// against the captured bank (BankStorageTests).
	//
	// The per-slot registrations retail sends first are a different message (`SetItemStatCodec`) - they used to live
	// here as a `WriteBankSlotRegistration` helper, which hid that they were stat updates (#530 wave 7).
	//
	// The u32 carrier length belongs to the SENDER (#530 rule 2).
	public static class BankCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.BankIIR;

		// The lead flag every captured record carries.
		public const byte LeadFlag = 0x01;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(BankIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static BankIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(BankCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static BankIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "BankIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var charInstance = r.ReadU32Be();
			r.ExpectU8(LeadFlag, "lead flag");

			return new BankIIR(charInstance, r.ReadProtobufU32Length<ProtoInventoryGame>());
		}

		internal static void Write(FaolanStream s, BankIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.CharInstance).WriteByte(LeadFlag).WriteArrayUInt32Length(v.Container.ToProtobuf());
		}
	}

	// BankIIR (0x0BA1C1CB).
	public sealed record BankIIR(uint CharInstance, ProtoInventoryGame Container);
}
