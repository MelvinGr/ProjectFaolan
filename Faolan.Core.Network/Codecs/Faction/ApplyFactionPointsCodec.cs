using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Faction
{
	// `DispatchApplyFactionPoints` (GameCharInterface 0x50) - "your standing with this faction is now N" (Gitea #310).
	//
	//   [u32 factionId][u32 points]
	//
	// ⚠️ ZERO occurrences in the capture corpus, in either direction - the dumps carry no Khitai play at all. The body
	// is not guessed from that silence: the client's own method table names the parameters `(i_FactionId, i_Points)`
	// [V1: docs/re/aoc-client-rpc-methods.json], and every sibling scalar-parameter method on these interfaces writes
	// its arguments as bare big-endian values with no header of their own - the rule #132 established when it
	// recovered the guild-bank bodies the same way. So the body IS its two parameters, and there is nothing to invent.
	//
	// ⚠️ POINTS ARE SIGNED on our side and this field is not: the ladder runs −1,100,000 … +1,100,000 (FactionLadder),
	// and the wire slot is a u32. It is written as the two's-complement bit pattern, which is what a `uint` parameter
	// carrying a negative count means everywhere else on this protocol - see the record's remarks.
	public static class ApplyFactionPointsCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchApplyFactionPoints;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(ApplyFactionPoints value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt32(value.FactionId).WriteUInt32(unchecked((uint)value.Points));

			return s.ToArray();
		}

		public static byte[] Build(uint factionId, int points)
		{
			return Build(new ApplyFactionPoints(factionId, points));
		}

		public static ApplyFactionPoints Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(ApplyFactionPointsCodec));
			var factionId = r.ReadU32Be();
			var value = new ApplyFactionPoints(factionId, unchecked((int)r.ReadU32Be()));
			r.EnsureAtEnd();

			return value;
		}
	}

	// ⚠️ `Points` is SIGNED here where the wire slot is a u32, and that is the honest modelling rather than a
	// convenience: the client's own bounds are symmetric about zero (`MinFactionPoints` = −1,100,000), so a standing
	// really can be negative and a `uint` field would make "hostile" unrepresentable.
	public sealed record ApplyFactionPoints(uint FactionId, int Points);
}
