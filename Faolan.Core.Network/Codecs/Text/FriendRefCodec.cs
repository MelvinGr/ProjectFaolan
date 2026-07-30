using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Text
{
	// The bare `[u32 characterId]` body of the friend-list request/echo pair: c2s AddFriend (0x0028) and
	// RemoveFriend (0x0029) both carry only the character to act on, and the s2c FriendRemoved echoes the id
	// straight back. An ADD gets no echo of this shape - it is answered by the presence push, the name binding and
	// the state (FriendPresenceCodec / ChatSenderInfoCodec / FriendStateCodec).
	//
	// `Opcode` names the s2c echo, which is the one direction that is ours to emit; the two c2s requests share the
	// same one-field body and parse through the same codec.
	public static class FriendRefCodec
	{
		public const ushort Opcode = (ushort)ChatServerInterfaceOpcodes.FriendRemoved;

		public static byte[] Build(FriendRef value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt32(value.CharacterId);

			return s.ToArray();
		}

		public static byte[] Build(uint characterId)
		{
			return Build(new FriendRef(characterId));
		}

		public static FriendRef Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(FriendRefCodec));
			var value = new FriendRef(r.ReadU32Be());
			r.EnsureAtEnd();

			return value;
		}
	}

	public sealed record FriendRef(uint CharacterId);
}
