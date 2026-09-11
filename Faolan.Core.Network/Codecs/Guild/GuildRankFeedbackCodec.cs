using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	/// <summary>
	///     <c>DispatchGuildGMPromoteFeedback</c> / <c>DispatchGuildGMDemoteFeedback</c> - the confirmation the guild
	///     master gets for a rank change they just made:
	///     <c>[u32 governingForm][u32 oldRank][u32 newRank][string name]</c>.
	/// </summary>
	/// <remarks>
	///     One layout, two methods, with the verb in the method, so this declares <c>HandledMethods</c> rather than naming
	///     one of them. Both readers (FUN_01340fb0 / FUN_01341220) are the same three <c>FUN_0048f900</c> u32 reads
	///     followed by the string primitive, in the order their bail-out strings name.
	///     <para>
	///         <c>GM</c> here is guild master, not game master: the client prefixes its game-master methods with
	///         <c>GM</c> at the front (<c>DispatchGMGuildNameChanged</c>, <c>DispatchGMReplyGuildInfoFromChar</c>, c2s
	///         <c>GMGuildName</c>), while this pair is <c>DispatchGuild</c> plus <c>GM</c> and carries exactly what a
	///         promoter's toast needs: who, from which rung to which, and the ladder to name them off.
	///     </para>
	///     <para>
	///         The ranks are u32 here and u8 in <c>DispatchGuildRankChanged</c>. The two readers genuinely use different
	///         primitives, so a "ranks are one byte everywhere" assumption would break here.
	///     </para>
	///     <para>No capture performs a promotion or demotion. [V0]</para>
	/// </remarks>
	public static class GuildRankFeedbackCodec
	{
		public static readonly CSPlayerInterfaceMethods[] HandledMethods =
			[CSPlayerInterfaceMethods.DispatchGuildGMPromoteFeedback, CSPlayerInterfaceMethods.DispatchGuildGMDemoteFeedback];

		public static byte[] Build(GuildRankFeedback value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt32((uint)value.GoverningForm).WriteUInt32(value.OldRank).WriteUInt32(value.NewRank).WriteString(value.Name);

			return s.ToArray();
		}

		public static byte[] Build(GuildGoverningForm form, byte oldRank, byte newRank, string name)
		{
			return Build(new GuildRankFeedback(form, oldRank, newRank, name));
		}

		public static GuildRankFeedback Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GuildRankFeedbackCodec));
			var value = new GuildRankFeedback((GuildGoverningForm)r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be(), r.ReadStringU16Length());
			r.EnsureAtEnd();

			return value;
		}
	}

	public sealed record GuildRankFeedback(GuildGoverningForm GoverningForm, uint OldRank, uint NewRank, string Name);
}
