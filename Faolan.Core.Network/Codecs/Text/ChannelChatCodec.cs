using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Text
{
	// ChannelChat (0x0041) - a post on a chat CHANNEL:
	//   [u8 channelType][u32 channelId][u32 senderId][u16 len][text][u16 0]
	//
	// The channel (type + id) from the original post is relayed VERBATIM: the client only displays a channel it is
	// subscribed to, so the per-channel routing is the client's own subscription filter (a global channel reaches
	// everyone; ~Playfield's id is a map id, so only same-map clients are subscribed and the rest ignore it).
	// A ChatSenderInfo (0x0014) precedes it so the speaker's name can be shown.
	//
	// ⚠️ The c2s post carries the same three leading fields WITHOUT the sender id - the server stamps that - which is
	// why `Opcode` is the s2c method and the inbound form is read by the listener's own dispatch.
	//
	// Moved in from `Faolan/ChatServer/Packets.cs` in #530.
	public static class ChannelChatCodec
	{
		public const ushort Opcode = (ushort)ChatServerInterfaceOpcodes.ChannelChat;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(ChannelChat value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(byte channelType, uint channelId, uint senderId, string text)
		{
			return Build(new ChannelChat(channelType, channelId, senderId, text));
		}

		public static ChannelChat Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(ChannelChatCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static ChannelChat Read(ref SpanReader r)
		{
			var channelType = r.ReadU8();
			var channelId = r.ReadU32Be();
			var senderId = r.ReadU32Be();
			var text = r.ReadStringU16Length();
			r.ExpectU16Be(0, "the trailing word");

			return new ChannelChat(channelType, channelId, senderId, text);
		}

		internal static void Write(FaolanStream s, ChannelChat v)
		{
			s.WriteByte(v.ChannelType).WriteUInt32(v.ChannelId).WriteUInt32(v.SenderId).WriteString(v.Text).WriteUInt16(0);
		}
	}

	// s2c ChannelChat (0x0041).
	public sealed record ChannelChat(byte ChannelType, uint ChannelId, uint SenderId, string Text);
}
