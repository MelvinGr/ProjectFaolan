using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	/// <summary>
	///     <c>DispatchGuildMemberUpdate</c> - one member's roster row changed (rank, online flag, playfield): the member
	///     element and nothing else.
	/// </summary>
	/// <remarks>
	///     It is the narrow counterpart of <c>DispatchGuildUpdate</c>, which re-sends the guild header and the whole
	///     roster. <c>CSPlayerInterfaceDispatch_t::DispatchGuildMemberUpdate</c> (FUN_01340c70) reads no scalars at all:
	///     it constructs the element (FUN_005658a0) and deserializes it (FUN_00566740), the same pair
	///     <c>DispatchGuildMemberJoined</c> uses for its second argument, which is what identifies the payload as the
	///     member record.
	///     <para>No capture performs a roster change other than a join. [V0]</para>
	/// </remarks>
	public static class GuildMemberUpdateCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchGuildMemberUpdate;

		public static byte[] Build(GuildMemberUpdate value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			GuildMemberRecordCodec.Write(s, value.Member);

			return s.ToArray();
		}

		public static byte[] Build(GuildMemberRecord member)
		{
			return Build(new GuildMemberUpdate(member));
		}

		public static GuildMemberUpdate Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GuildMemberUpdateCodec));
			var value = new GuildMemberUpdate(GuildMemberRecordCodec.Read(ref r));
			r.EnsureAtEnd();

			return value;
		}
	}

	public sealed record GuildMemberUpdate(GuildMemberRecord Member);
}
