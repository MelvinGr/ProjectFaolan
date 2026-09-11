using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Dialogue
{
	/// <summary>
	///     <c>KnubotStartTradeIIR</c> - opens a vendor/NPC trade window.
	/// </summary>
	/// <remarks>
	///     <c>ReadSubClass</c> (FUN_009a63a0) runs the Knubot base prefix then a list of <c>TradeItem_c</c> entries, each
	///     named by an XML <c>remoteformat</c> string - the same Knubot dialogue text as the conversation packets. The
	///     trade payload is that conversation-system structure, so this codec constructs the frame and carries the payload
	///     verbatim, byte-exact, rather than re-modelling the whole Knubot XML shop grammar
	///     (docs/re/v1-iir-readers.md). The u32 carrier length belongs to the sender.
	/// </remarks>
	public static class KnubotStartTradeCodec
	{
		public const uint Opcode = (uint)IirTypes.KnubotStartTradeIIR;

		// Canonical: the only method here that writes bytes.
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

	/// <summary>The KnubotStartTradeIIR body.</summary>
	public sealed record KnubotStartTradeIIR(uint Handle, byte Env, byte[] Payload);
}
