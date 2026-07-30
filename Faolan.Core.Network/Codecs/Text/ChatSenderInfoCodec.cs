using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Text
{
	// s2c ChatSenderInfo (0x0014) - the id -> NAME binding: [u32 characterId][u32 0][string name]. The chat tier
	// never puts a name on any other message, so this is what teaches the client who an id is. It is sent BEFORE a
	// whisper or a channel post so the speaker can be shown, and once per friend when a friend list is delivered.
	//
	// ⚠️ Omitting it on a friend add leaves the client with an id and a state but no NAME, and it renders nothing at
	// all - no entry, no error. That is why the friend list stayed empty while the other two messages were
	// byte-perfect.
	public static class ChatSenderInfoCodec
	{
		public const ushort Opcode = (ushort)ChatServerInterfaceOpcodes.ChatSenderInfo;

		public static byte[] Build(ChatSenderInfo value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt32(value.CharacterId).WriteUInt32(0).WriteString(value.Name);

			return s.ToArray();
		}

		public static byte[] Build(uint characterId, string name)
		{
			return Build(new ChatSenderInfo(characterId, name));
		}

		public static ChatSenderInfo Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(ChatSenderInfoCodec));
			var characterId = r.ReadU32Be();
			r.ExpectU32Be(0, "the reserved word");
			var value = new ChatSenderInfo(characterId, r.ReadStringU16Length());
			r.EnsureAtEnd();

			return value;
		}
	}

	public sealed record ChatSenderInfo(uint CharacterId, string Name);
}
