using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     SpellBookIIR - the client reader (FUN_00795ba0) triggers a spellbook refresh on the carrier handle. The body
	///     carries a count-framed list whose elements the reader does not consume, so the trailing word is a
	///     <see cref="ListFrame" /> count and an empty book is the empty-list frame rather than a constant. Grammar from the
	///     client reader; see docs/re/v1-iir-readers.md.
	/// </summary>
	/// <remarks>The u32 carrier length belongs to the framing sender, not to this body.</remarks>
	public static class SpellBookCodec
	{
		public const uint Opcode = (uint)IirTypes.SpellBookIIR;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(SpellBookIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint handle, int count = 0, byte env = 0x00)
		{
			return Build(new SpellBookIIR(handle, env, count));
		}

		public static SpellBookIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SpellBookCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static SpellBookIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "SpellBookIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			var env = r.ReadU8();
			return new SpellBookIIR(handle, env, r.ReadListCount());
		}

		internal static void Write(FaolanStream s, SpellBookIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Handle).WriteByte(v.Env).WriteUInt32(ListFrame.ForCount(v.Count));
		}
	}

	/// <summary>The SpellBookIIR body.</summary>
	public sealed record SpellBookIIR(uint Handle, byte Env, int Count);
}
