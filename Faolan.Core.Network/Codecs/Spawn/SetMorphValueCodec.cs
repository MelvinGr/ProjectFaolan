using System;
using System.Text;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// SetMorphValueIIR (0xBADF5A4B) - sets one morph/resource value on an entity. Variable length:
	//
	//   [u32 subop][u32 class][u32 handle][u16 0][u8 len][value][f32 weight BE][u8 p][u32 q]
	//
	// The editor streams the chosen hair/texture resource refs this way; `weight` is 1.0 in every capture and the
	// small p/q trailing pair carries morph flags/index. Raw-body floats are big-endian.
	//
	// ⚠️ `Value` is the WHOLE length-prefixed string, leading '*' included. This codec used to model that byte as a
	// separate `NameTag = 0x2A` constant with the name after it - but 0x2A IS '*', and the client's own morph values
	// are '*'-prefixed resource refs ("*hairmesh_207920"): `SendMorphValueAck` echoes the client's string with no tag
	// of its own and switches its default weight on `StartsWith("*")`, which the tag reading cannot express. Two
	// emitters, one message, and the field boundary was in the wrong place in one of them (#530 §3's pattern, #542).
	// No byte moved - the tag form wrote exactly the same [len][*][name].
	//
	// The u32 carrier length belongs to the SENDER (#530 rule 2).
	public static class SetMorphValueCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.SetMorphValueIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(SetMorphValueIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static SetMorphValueIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SetMorphValueCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static SetMorphValueIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "SetMorphValueIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			r.ExpectU16Be(0, "leading zero half-word");

			var value = Encoding.Latin1.GetString(r.ReadBytesU8Length());
			var weight = r.ReadF32Be();
			var p = r.ReadU8();
			return new SetMorphValueIIR(handle, value, weight, p, r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, SetMorphValueIIR v)
		{
			// Wire strings are raw Latin1 bytes - see FaolanStream.ReadString.
			s
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.Handle)
				.WriteUInt16(0)
				.WriteStringByteLength(v.Value)
				.WriteFloat(v.Weight)
				.WriteByte(v.P)
				.WriteUInt32(v.Q);
		}
	}

	// SetMorphValueIIR (0xBADF5A4B). `Value` is the morph/resource ref verbatim, '*' prefix included.
	public sealed record SetMorphValueIIR(uint Handle, string Value, float Weight, byte P, uint Q);
}
