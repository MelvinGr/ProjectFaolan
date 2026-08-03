using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	// DispatchGuildGMPromoteFeedback (0x2032) / DispatchGuildGMDemoteFeedback (0x2033) - the confirmation the
	// GUILD MASTER gets for a rank change they just made:
	//
	//   [u32 governingForm][u32 oldRank][u32 newRank][string name]
	//
	// One layout, two methods, the verb in the opcode - so this declares HandledMethods rather than picking one of
	// them as "the" method. Both readers (FUN_01340fb0 / FUN_01341220) are the same three `FUN_0048f900` u32 reads
	// followed by the string primitive, in the order their bail-out strings name.
	//
	// ⚠️ `GM` here is GUILD MASTER, not game master. The client prefixes its game-master methods with `GM` at the
	// FRONT (`DispatchGMGuildNameChanged`, `DispatchGMReplyGuildInfoFromChar`, c2s `GMGuildName`); this pair is
	// `DispatchGuild` + `GM`, and it carries exactly what a promoter's toast needs - who, from which rung to which,
	// and the ladder to name them off.
	//
	// ⚠️ The ranks are u32 HERE and u8 in DispatchGuildRankChanged. That is not a transcription slip: the two
	// readers genuinely use different primitives (see GuildRankChangedCodec), which is exactly the kind of asymmetry
	// a "ranks are one byte everywhere" assumption would break on.
	//
	// ⚠️ No capture performs a promotion or demotion. [V0]
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
