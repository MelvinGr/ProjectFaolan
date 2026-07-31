using System;
using System.Text;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// SetMorphValueIIR (0xBADF5A4B) - sets one morph/resource value on an entity. Variable length:
	//
	//   [u32 subop][u32 class][u32 handle][u16 0][u8 blobLen][0x2A][name][f32 weight BE][u8 p][u32 q]
	//
	// `blobLen` counts the 0x2A tag plus the name, i.e. 1 + name.Length. The editor streams the chosen hair/texture
	// resource refs ("hairmesh_207920", "texscript_183875") this way; `weight` is 1.0 in every capture and the small
	// p/q trailing pair carries morph flags/index. Raw-body floats are big-endian.
	//
	// The u32 carrier length belongs to the SENDER (#530 rule 2).
	public static class SetMorphValueCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.SetMorphValueIIR;

		// The protobuf-shaped tag that opens the length-prefixed blob. Constant across every captured record.
		private const byte NameTag = 0x2A;

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

			var blob = r.ReadBytesU8Length();
			var blobReader = new SpanReader(blob, nameof(SetMorphValueCodec));
			blobReader.ExpectU8(NameTag, "morph name tag");
			var name = Encoding.Latin1.GetString(blobReader.ReadRemaining());

			var weight = r.ReadF32Be();
			var p = r.ReadU8();
			return new SetMorphValueIIR(handle, name, weight, p, r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, SetMorphValueIIR v)
		{
			// Wire strings are raw Latin1 bytes - see FaolanStream.ReadString.
			var name = Encoding.Latin1.GetBytes(v.Name);
			s
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.Handle)
				.WriteUInt16(0)
				.WriteByte(1 + name.Length)
				.WriteByte(NameTag)
				.WriteArray(name)
				.WriteFloat(v.Weight)
				.WriteByte(v.P)
				.WriteUInt32(v.Q);
		}
	}

	// SetMorphValueIIR (0xBADF5A4B).
	public sealed record SetMorphValueIIR(uint Handle, string Name, float Weight, byte P, uint Q);
}
