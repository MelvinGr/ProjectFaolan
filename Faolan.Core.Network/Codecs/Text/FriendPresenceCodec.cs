using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Text
{
	/// <summary>
	///     s2c <c>FriendPresence</c> - the presence push retail sends on a friend add, but not when restoring the list at
	///     login, addressed by name rather than by id unlike every other message in this subsystem:
	///     <c>[u64 0][u32 stamp][u16 len][u8 0x53][u16 len][name]</c>.
	/// </summary>
	/// <remarks>
	///     The stamp sits in the same range as <see cref="FriendStateCodec" />'s online stamp and is very likely the same
	///     kind of playfield-instance token; nothing in the corpus pins it, so it is sent verbatim.
	/// </remarks>
	public static class FriendPresenceCodec
	{
		public const uint DefaultStamp = 0x01E85C14;
		public const ushort Opcode = (ushort)ChatServerInterfaceOpcodes.FriendPresence;

		// The tag byte inside the presence push's length-prefixed value.
		private const byte PresenceTag = 0x53;

		public static byte[] Build(FriendPresence value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt64(0).WriteUInt32(value.Stamp).WriteArrayUInt16Length(b => b.WriteByte(PresenceTag).WriteString(value.Name));

			return s.ToArray();
		}

		public static byte[] Build(string name)
		{
			return Build(new FriendPresence(name));
		}

		public static FriendPresence Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(FriendPresenceCodec));
			r.ExpectU64Be(0, "the leading reserved word");
			var stamp = r.ReadU32Be();

			// The name rides a length-prefixed value that itself starts with the tag byte, so the inner reader is the outer
			// block rather than the body - a nested frame, not two fields in a row.
			var inner = new SpanReader(r.ReadBytesU16Length(), nameof(FriendPresenceCodec));
			inner.ExpectU8(PresenceTag, "the presence tag");
			var name = inner.ReadStringU16Length();
			inner.EnsureAtEnd();
			r.EnsureAtEnd();

			return new FriendPresence(name, stamp);
		}
	}

	public sealed record FriendPresence(string Name, uint Stamp = FriendPresenceCodec.DefaultStamp);
}
