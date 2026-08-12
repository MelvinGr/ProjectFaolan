using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Faction
{
	// `DispatchApplyFactionRank` (GameCharInterface 0x4F) - "your rank with this faction is now N" (Gitea #310).
	//
	//   [u32 factionId][u32 rank]
	//
	// The twin of `ApplyFactionPointsCodec`, recovered the same way and with the same caveat: zero occurrences in the
	// corpus, parameters named `(i_FactionId, i_Rank)` by the client's own method table
	// [V1: docs/re/aoc-client-rpc-methods.json], body = the two parameters as bare big-endian values (#132's rule).
	//
	// ⚠️ RANK is derived, never stored: it is `FactionLadder.RankFor(points)` over the same points the other message
	// carries. The two are sent together because the client keeps them in different places - the standing bar off the
	// points, the TITLE off the rank - and a server that sent only one would leave the other showing the last value
	// it was told. `Rank` is 0..4, and 0 means "no title yet", which is every standing below 50,000 including the
	// whole negative half.
	public static class ApplyFactionRankCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchApplyFactionRank;

		// CANONICAL - the only method here that writes bytes.
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
