using System;
using System.Text;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Spawn
{
	/// <summary>
	///     SetMorphValueIIR - sets one morph or resource value on an entity. Variable length:
	///     <c>[u32 subop][u32 class][u32 handle][u16 0][u8 len][value][f32 weight][u8 p][u32 q]</c>, the float
	///     big-endian like every raw-body float.
	/// </summary>
	/// <remarks>
	///     The editor streams the chosen hair and texture resource refs this way. <c>weight</c> is 1.0 in every capture and
	///     the small p/q trailing pair carries morph flags and index.
	///     <para>
	///         <c>Value</c> is the whole length-prefixed string, leading '*' included. The '*' is not a separate tag byte:
	///         the client's own morph values are '*'-prefixed resource refs such as "*hairmesh_207920", and
	///         SendMorphValueAck echoes the client's string with no tag of its own, switching its default weight on whether
	///         the string starts with one.
	///     </para>
	///     <para>The u32 carrier length belongs to the framing sender, not to this body.</para>
	/// </remarks>
	public static class SetMorphValueCodec
	{
		public const uint Opcode = (uint)IirTypes.SetMorphValueIIR;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
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
			// Wire strings are raw Latin1 bytes; see FaolanStream.ReadString.
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

	/// <summary>
	///     The SetMorphValueIIR body. <c>Value</c> is the morph or resource ref verbatim, '*' prefix included.
	/// </summary>
	public sealed record SetMorphValueIIR(uint Handle, string Value, float Weight, byte P, uint Q);
}
