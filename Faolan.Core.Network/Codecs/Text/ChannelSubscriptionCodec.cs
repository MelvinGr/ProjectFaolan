using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Text
{
	// ChannelSubscription (0x003C) - joins the client to one chat channel:
	//   [u8 type][u32 id][u16 len][name][u32 flag][u16 0]
	// Sent on ChatServer connect for each channel the character joins (~Trial / ~Global / ~LFG / ~GlobalEnglish /
	// ~Playfield / ~NewbieHelp, plus the server-defined Claude channel). Byte layout verified from the raw login pcap.
	//
	// The subscription is what makes channel ROUTING the client's job: a ChannelChat is relayed to everyone and each
	// client displays only the channels it holds a subscription for (see ChannelChatCodec).
	//
	// Moved in from `ChatServerListener.SendChannelSubscription` in #530.
	public static class ChannelSubscriptionCodec
	{
		public const ushort Opcode = (ushort)ChatServerInterfaceOpcodes.ChannelSubscription;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(ChannelSubscription value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(byte type, uint id, string name, uint flag)
		{
			return Build(new ChannelSubscription(type, id, name, flag));
		}

		public static ChannelSubscription Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(ChannelSubscriptionCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static ChannelSubscription Read(ref SpanReader r)
		{
			var type = r.ReadU8();
			var id = r.ReadU32Be();
			var name = r.ReadStringU16Length();
			var flag = r.ReadU32Be();
			r.ExpectU16Be(0, "the trailing word");

			return new ChannelSubscription(type, id, name, flag);
		}

		internal static void Write(FaolanStream s, ChannelSubscription v)
		{
			s.WriteByte(v.Type).WriteUInt32(v.Id).WriteString(v.Name).WriteUInt32(v.Flag).WriteUInt16(0);
		}
	}

	// ChannelSubscription (0x003C).
	public sealed record ChannelSubscription(byte Type, uint Id, string Name, uint Flag);
}
