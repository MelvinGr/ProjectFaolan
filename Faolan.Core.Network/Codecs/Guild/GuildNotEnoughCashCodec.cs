using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	// s2c `GameCharInterface.DispatchGuildNotEnoughCash(i_nAmount)` - the client's OWN refusal for a guild-bank
	// deposit the payer cannot afford (#654).
	//
	// It matters that this exists, because the alternative was silence. Every other refusal in this subsystem is
	// logged and nothing is sent, on the grounds that retail's client will not send a request its own UI already
	// forbids - but here the client CAN send one (it does not know the wallet moved), retail has a named message for
	// exactly this case, and the field is the amount that was short. So the honest answer is retail's own, not a
	// chat line and not nothing.
	//
	// Body layout as for every other GameCharInterface scalar method: the parameter, big-endian, no header. The
	// amount is 64-bit to match the wallet and `Guild.Cash`, both u64.
	// [V0: recovered from the client's dispatch table - the whole guild-bank surface is uncaptured]
	public static class GuildNotEnoughCashCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchGuildNotEnoughCash;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(GuildNotEnoughCash value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt64(value.AmountTin);

			return s.ToArray();
		}

		public static byte[] Build(ulong amountTin)
		{
			return Build(new GuildNotEnoughCash(amountTin));
		}

		public static GuildNotEnoughCash Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GuildNotEnoughCashCodec));
			var value = new GuildNotEnoughCash(r.ReadU64Be());
			r.EnsureAtEnd();

			return value;
		}
	}

	public sealed record GuildNotEnoughCash(ulong AmountTin);
}
