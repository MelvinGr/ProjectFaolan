using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// KnubotStartTradeIIR (0x4B2A47BF) - opens a vendor/NPC trade window. ReadSubClass (FUN_009a63a0) runs the
	// Knubot base prefix then a list of TradeItem_c entries, each named by an XML `<remoteformat>` string (the same
	// Knubot dialogue text as the conversation packets). The trade/dialogue PAYLOAD is that conversation-system
	// structure, so this codec constructs the frame ([handle][env]) and carries the payload verbatim - byte-exact -
	// rather than re-modelling the whole Knubot XML shop grammar here (docs/re/v1-iir-readers.md).
	//
	// The u32 carrier length belongs to the SENDER (#530 rule 2).
	public static class KnubotStartTradeCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.KnubotStartTradeIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(KnubotStartTradeIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static KnubotStartTradeIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(KnubotStartTradeCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static KnubotStartTradeIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "KnubotStartTradeIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			var env = r.ReadU8();
			return new KnubotStartTradeIIR(handle, env, r.ReadRemaining().ToArray());
		}

		internal static void Write(FaolanStream s, KnubotStartTradeIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Handle).WriteByte(v.Env).WriteArray(v.Payload);
		}
	}

	// KnubotStartTradeIIR (0x4B2A47BF).
	public sealed record KnubotStartTradeIIR(uint Handle, byte Env, byte[] Payload);
}
