using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Text
{
	// ChannelUnsubscribe (0x003D) - drops the client off one chat channel:
	//   [u8 type][u32 id]
	//
	// The subscription's twin, and named from that: the body is byte-identical to the (type, id) pair
	// ChannelSubscriptionCodec opens with, minus the name string and trailing flags (#532). The ids match channels
	// subscribed earlier in the same capture - a `~Team` subscription (82 000f08db) is followed by 0x003D on
	// 82 000f08db, and in the two-client group capture the "leave goup now" post on channel 82 0051dd9a is followed by
	// 0x003D on that channel. `~Playfield` flips the same way as the player changes playfield: the old channel is
	// dropped, then the new one subscribed.
	//
	// ⚠️ It MATTERS that this is sent, because channel routing is the client's job: a ChannelChat is relayed to every
	// connected client and each one displays only the channels it holds a subscription for. A stale `~Playfield`
	// subscription therefore does not merely linger - the player keeps hearing the playfield they left and never hears
	// the one they are standing in (#562).
	public static class ChannelUnsubscribeCodec
	{
		public const ushort Opcode = (ushort)ChatServerInterfaceOpcodes.ChannelUnsubscribe;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(ChannelUnsubscribe value)
		{
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(byte type, uint id)
		{
			return Build(new ChannelUnsubscribe(type, id));
		}

		public static ChannelUnsubscribe Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(ChannelUnsubscribeCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static ChannelUnsubscribe Read(ref SpanReader r)
		{
			var type = r.ReadU8();

			return new ChannelUnsubscribe(type, r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, ChannelUnsubscribe v)
		{
			s.WriteByte(v.Type).WriteUInt32(v.Id);
		}
	}

	// One chat channel the client is no longer on: the same (type, id) that identifies it in a ChannelSubscription.
	public sealed record ChannelUnsubscribe(byte Type, uint Id);
}
