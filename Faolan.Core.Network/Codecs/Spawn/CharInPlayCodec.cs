using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Spawn
{
	/// <summary>
	///     CharInPlayIIR - the "character is now in play" signal:
	///     <c>[u32 subop][u32 class][u32 handle][u8 env]</c>.
	/// </summary>
	/// <remarks>
	///     An empty record: the client reader (FUN_0075a920) only resolves the SimpleChar for the handle and logs, so the
	///     carrier handle is the whole message. See docs/re/iir-readers-combat-world.md. <c>env</c> is the base IIR's flag
	///     byte, 0 in every capture, and the u32 carrier length belongs to the framing sender.
	/// </remarks>
	public static class CharInPlayCodec
	{
		public const uint Opcode = (uint)IirTypes.CharInPlayIIR;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(CharInPlayIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static CharInPlayIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(CharInPlayCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static CharInPlayIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "CharInPlayIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			return new CharInPlayIIR(handle, r.ReadU8());
		}

		internal static void Write(FaolanStream s, CharInPlayIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Handle).WriteByte(v.Env);
		}
	}

	/// <summary>The CharInPlayIIR body.</summary>
	public sealed record CharInPlayIIR(uint Handle, byte Env = 0);
}
