using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Text
{
	/// <summary>
	///     s2c <c>ChannelChat</c> - a post on a chat channel:
	///     <c>[u8 channelType][u32 channelId][u32 senderId][u16 len][text][u16 0]</c>.
	/// </summary>
	/// <remarks>
	///     The channel (type plus id) from the original post is relayed verbatim: the client only displays a channel it is
	///     subscribed to, so the per-channel routing is the client's own subscription filter - a global channel reaches
	///     everyone, while ~Playfield's id is a map id, so only same-map clients are subscribed and the rest ignore it. A
	///     <c>ChatSenderInfo</c> precedes it so the speaker's name can be shown.
	///     <para>
	///         The c2s post carries the same three leading fields without the sender id, which the server stamps, which is
	///         why the declared opcode is the s2c method and the inbound form is read by the listener's own dispatch.
	///     </para>
	/// </remarks>
	public static class ChannelChatCodec
	{
		public const ushort Opcode = (ushort)ChatServerInterfaceOpcodes.ChannelChat;

		// Canonical: the only method here that writes bytes.
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

	/// <summary>The s2c ChannelChat body.</summary>
	public sealed record ChannelChat(byte ChannelType, uint ChannelId, uint SenderId, string Text);
}
