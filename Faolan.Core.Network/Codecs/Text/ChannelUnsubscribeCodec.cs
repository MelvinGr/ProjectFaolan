using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Text
{
	/// <summary>
	///     <c>ChannelUnsubscribe</c> - drops the client off one chat channel: <c>[u8 type][u32 id]</c>.
	/// </summary>
	/// <remarks>
	///     The subscription's twin, and named from it: the body is byte-identical to the (type, id) pair
	///     <see cref="ChannelSubscriptionCodec" /> opens with, minus the name string and trailing flags. The ids match
	///     channels subscribed earlier in the same capture - a ~Team subscription is followed by an unsubscribe on the
	///     same channel, and in the two-client group capture the "leave goup now" post on a team channel is followed by an
	///     unsubscribe on it. ~Playfield flips the same way as the player changes playfield: the old channel is dropped,
	///     then the new one subscribed.
	///     <para>
	///         Sending it matters, because channel routing is the client's job: a ChannelChat is relayed to every
	///         connected client and each one displays only the channels it holds a subscription for. A stale ~Playfield
	///         subscription therefore leaves the player hearing the playfield they left and never the one they are
	///         standing in.
	///     </para>
	/// </remarks>
	public static class ChannelUnsubscribeCodec
	{
		public const ushort Opcode = (ushort)ChatServerInterfaceOpcodes.ChannelUnsubscribe;

		// Canonical: the only method here that writes bytes.
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

	/// <summary>
	///     One chat channel the client is no longer on: the same (type, id) that identifies it in a ChannelSubscription.
	/// </summary>
	public sealed record ChannelUnsubscribe(byte Type, uint Id);
}
