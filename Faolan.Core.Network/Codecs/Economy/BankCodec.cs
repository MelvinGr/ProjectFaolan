using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Economy
{
	/// <summary>
	///     <c>BankIIR</c> - the personal-bank content record: the kind-105 inventory container, delivered on demand when
	///     the bank window opens and framed exactly like the FullChar snapshot:
	///     <c>[subop][entityClass][charInstance][u8 flag][u32 containerLen][ProtoInventoryGame]</c>.
	/// </summary>
	/// <remarks>
	///     Decoded from NecroL80_..._Has-inventory-at-trader-bank and golden-tested byte-exact against the captured bank
	///     (<c>BankStorageTests</c>). The per-slot registrations retail sends first are a different message
	///     (<see cref="SetItemStatCodec" />). The u32 carrier length belongs to the sender.
	/// </remarks>
	public static class BankCodec
	{
		public const uint Opcode = (uint)IirTypes.BankIIR;

		// The lead flag every captured record carries.
		public const byte LeadFlag = 0x01;

		// Canonical: the only method here that writes bytes.
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

	/// <summary>The BankIIR body.</summary>
	public sealed record BankIIR(uint CharInstance, ProtoInventoryGame Container);
}
