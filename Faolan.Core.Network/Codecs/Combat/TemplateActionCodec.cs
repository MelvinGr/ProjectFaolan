using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     TemplateActionIIR - the client reader (FUN_007a0b00) reads an item-template struct (FUN_005d5150, 68 bytes on
	///     the wire) then six u32. The struct is item-identity data - a name and length region plus item keys, the
	///     B2 90 80 5C 29 E6 27 CA equip-identity pattern and TEA-key-like blocks - so it is carried verbatim, while the six
	///     trailing u32 are constructed. Grammar from the client reader; see docs/re/v1-iir-readers.md.
	/// </summary>
	/// <remarks>The u32 carrier length belongs to the framing sender, not to this body.</remarks>
	public static class TemplateActionCodec
	{
		public const int TemplateBytes = 68;
		public const uint Opcode = (uint)IirTypes.TemplateActionIIR;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(TemplateActionIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static TemplateActionIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(TemplateActionCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static TemplateActionIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "TemplateActionIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			var env = r.ReadU8();
			var template = r.ReadBytes(TemplateBytes).ToArray();
			return new TemplateActionIIR(handle, env, template, r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, TemplateActionIIR v)
		{
			s
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.Handle)
				.WriteByte(v.Env)
				.WriteArray(v.Template)
				.WriteUInt32(v.F1)
				.WriteUInt32(v.F2)
				.WriteUInt32(v.F3)
				.WriteUInt32(v.F4)
				.WriteUInt32(v.F5)
				.WriteUInt32(v.F6);
		}
	}

	/// <summary>The TemplateActionIIR body.</summary>
	public sealed record TemplateActionIIR(uint Handle, byte Env, byte[] Template, uint F1, uint F2, uint F3, uint F4, uint F5, uint F6);
}
