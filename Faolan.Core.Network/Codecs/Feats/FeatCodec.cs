using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Feats
{
	/// <summary>
	///     FeatIIR - the feat train/unlock handshake.
	/// </summary>
	/// <remarks>
	///     The body, starting at the sub-opcode (the caller frames it with DispatchPassBlob and
	///     <c>WriteArrayUInt32Length</c>, which prepends the u32 length):
	///     <code>
	///     [u32 FeatIIR][u32 characterClass][u32 charHandle][u8 0x00][u8 state][u32 0][list frame][count x u32 featId]
	///     </code>
	///     <para>
	///         State 0x04 is the c2s train/unlock request and 0x05 the s2c ack echoing the requested id list - the same
	///         shape as the claims/redeem window. The feat ids share the id-space of the spawn's pre-IIR feat list
	///         (train order, composite base*100+rank). Decoded from the hakotep feat captures; see
	///         docs/subsystems/feats.md.
	///     </para>
	/// </remarks>
	public static class FeatCodec
	{
		public const uint Opcode = (uint)IirTypes.FeatIIR;

		public const byte StateAck = 0x05;
		public const byte StateTrainRequest = 0x04;

		// The wire body from the sub-opcode; the u32 length prefix is the framing sender's (WriteArrayUInt32Length).
		public static byte[] Build(FeatIIR value)
		{
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(uint charHandle, byte state, IReadOnlyList<uint> featIds)
		{
			return Build(new FeatIIR(charHandle, state, featIds));
		}

		public static FeatIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(FeatCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		// Read sequentially so that every field is accounted for - the entity-class word, the constant byte before
		// `state` and the zero word before the list frame included - and the id list runs to the end of the body.
		internal static FeatIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "the FeatIIR sub-opcode");
			r.ExpectU32Be(EntityClass.Character, "the character entity class");
			var charHandle = r.ReadU32Be();
			r.ReadU8(); // constant 0x00
			var state = r.ReadU8();
			r.ReadU32Be(); // zero word

			var count = r.ReadListCount();
			var featIds = new uint[count];
			for (var i = 0; i < count; i++)
				featIds[i] = r.ReadU32Be();

			return new FeatIIR(charHandle, state, featIds);
		}

		internal static void Write(FaolanStream s, FeatIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.CharHandle).WriteByte(0x00).WriteByte(v.State).WriteUInt32(0u).WriteUInt32(ListFrame.ForCount(v.FeatIds.Count));
			foreach (var id in v.FeatIds)
				s.WriteUInt32(id);
		}
	}

	public sealed record FeatIIR(uint CharHandle, byte State, IReadOnlyList<uint> FeatIds);
}
