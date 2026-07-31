using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// UpdateSimpleDynelIIR (0x30536411) - a loot bag's visual update as it is consumed (sent right before the fade).
	// 24 B: [subop][0xC87A LootBag][bagHandle][u32 0][u32 0][01][00][01][01].
	//
	// ⚠️ The four trailing bytes are UNDECODED - one captured shape, kept as fields rather than named, so a caller
	// that needs a different combination can send it without inventing a meaning for it.
	public static class UpdateSimpleDynelCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.UpdateSimpleDynelIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(UpdateSimpleDynelIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		// The bag-consumed visual, in the one captured shape.
		public static byte[] Build(uint bagHandle)
		{
			return Build(new UpdateSimpleDynelIIR(EntityClass.LootBag, bagHandle, 0, 0, 0x01, 0x00, 0x01, 0x01));
		}

		public static UpdateSimpleDynelIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(UpdateSimpleDynelCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static UpdateSimpleDynelIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "UpdateSimpleDynelIIR subop");
			var typeTag = r.ReadU32Be();
			var handle = r.ReadU32Be();
			var word0 = r.ReadU32Be();
			var word1 = r.ReadU32Be();
			return new UpdateSimpleDynelIIR(typeTag, handle, word0, word1, r.ReadU8(), r.ReadU8(), r.ReadU8(), r.ReadU8());
		}

		internal static void Write(FaolanStream s, UpdateSimpleDynelIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(v.TypeTag).WriteUInt32(v.Handle).WriteUInt32(v.Word0).WriteUInt32(v.Word1).WriteByte(v.B0).WriteByte(v.B1).WriteByte(v.B2).WriteByte(v.B3);
		}
	}

	// UpdateSimpleDynelIIR (0x30536411).
	public sealed record UpdateSimpleDynelIIR(uint TypeTag, uint Handle, uint Word0, uint Word1, byte B0, byte B1, byte B2, byte B3);
}
