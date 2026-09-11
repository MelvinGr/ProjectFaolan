using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Faction
{
	/// <summary>
	///     <c>DispatchApplyFactionRank</c> - "your rank with this faction is now N", as
	///     <c>[u32 factionId][u32 rank]</c>.
	/// </summary>
	/// <remarks>
	///     The twin of <see cref="ApplyFactionPointsCodec" />, recovered the same way and with the same caveat: zero
	///     occurrences in the corpus, parameters named <c>(i_FactionId, i_Rank)</c> by the client's own method table
	///     [V1: docs/re/aoc-client-rpc-methods.json], and a body of those two parameters as bare big-endian values.
	///     <para>
	///         Rank is derived rather than stored - it is <c>FactionLadder.RankFor(points)</c> over the same points the
	///         other message carries. Both are sent together because the client keeps them in different places: the
	///         standing bar off the points, the title off the rank, so sending only one leaves the other showing the
	///         last value it was told. Rank is 0..4, and 0 means "no title yet", which is every standing below 50,000
	///         including the whole negative half.
	///     </para>
	/// </remarks>
	public static class ApplyFactionRankCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchApplyFactionRank;

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(ApplyFactionRank value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt32(value.FactionId).WriteUInt32(value.Rank);

			return s.ToArray();
		}

		public static byte[] Build(uint factionId, uint rank)
		{
			return Build(new ApplyFactionRank(factionId, rank));
		}

		public static ApplyFactionRank Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(ApplyFactionRankCodec));
			var factionId = r.ReadU32Be();
			var value = new ApplyFactionRank(factionId, r.ReadU32Be());
			r.EnsureAtEnd();

			return value;
		}
	}

	public sealed record ApplyFactionRank(uint FactionId, uint Rank);
}
