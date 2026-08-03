using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	// DispatchGuildRankChanged (0x2065) - "your rank changed", to the member it happened to: `[u8 newRank][u8 oldRank]`.
	//
	// ⚠️ Both fields are ONE BYTE, and that is the whole reason this codec exists rather than two u32 written by hand.
	// `CSPlayerInterfaceDispatch_t::DispatchGuildRankChanged` (FUN_013451e0) reads each with the raw-byte primitive
	// `FUN_00cba1b0(dst, 1)`, not the u32 reader `FUN_0048f900` that every neighbouring guild field uses - so a
	// four-byte rank would put the client six bytes past the end of a two-byte body.
	//
	// NEW first, OLD second: the client's signature is `(newRank, oldRank)` and the reader fills them in that order.
	// A rank is an INDEX into the governing form's ladder (GuildRankLadder), so it is only meaningful next to the
	// guild's form - which is why the guild-master's own feedback (GuildRankFeedbackCodec) carries the form and this
	// one does not: the member already has it from the full update.
	//
	// ⚠️ No capture performs a promotion or demotion. [V0]
	public static class GuildRankChangedCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchGuildRankChanged;

		public static byte[] Build(GuildRankChanged value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteByte(value.NewRank).WriteByte(value.OldRank);

			return s.ToArray();
		}

		public static byte[] Build(byte newRank, byte oldRank)
		{
			return Build(new GuildRankChanged(newRank, oldRank));
		}

		public static GuildRankChanged Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GuildRankChangedCodec));
			var value = new GuildRankChanged(r.ReadU8(), r.ReadU8());
			r.EnsureAtEnd();

			return value;
		}
	}

	public sealed record GuildRankChanged(byte NewRank, byte OldRank);
}
