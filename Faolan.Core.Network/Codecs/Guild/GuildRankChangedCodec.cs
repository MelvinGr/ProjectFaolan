using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	/// <summary>
	///     <c>DispatchGuildRankChanged</c> - "your rank changed", to the member it happened to:
	///     <c>[u8 newRank][u8 oldRank]</c>.
	/// </summary>
	/// <remarks>
	///     Both fields are one byte. <c>CSPlayerInterfaceDispatch_t::DispatchGuildRankChanged</c> (FUN_013451e0) reads
	///     each with the raw-byte primitive <c>FUN_00cba1b0(dst, 1)</c> rather than the u32 reader <c>FUN_0048f900</c>
	///     that every neighbouring guild field uses, so a four-byte rank would put the client six bytes past the end of a
	///     two-byte body.
	///     <para>
	///         New first, old second: the client's signature is <c>(newRank, oldRank)</c> and the reader fills them in
	///         that order. A rank is an index into the governing form's ladder (<c>GuildRankLadder</c>), so it is only
	///         meaningful next to the guild's form - which is why the guild master's own feedback
	///         (<see cref="GuildRankFeedbackCodec" />) carries the form and this one does not: the member already has it
	///         from the full update.
	///     </para>
	///     <para>No capture performs a promotion or demotion. [V0]</para>
	/// </remarks>
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
