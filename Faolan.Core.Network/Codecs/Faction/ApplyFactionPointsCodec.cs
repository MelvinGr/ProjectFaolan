using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Faction
{
	/// <summary>
	///     <c>DispatchApplyFactionPoints</c> - "your standing with this faction is now N", as
	///     <c>[u32 factionId][u32 points]</c>.
	/// </summary>
	/// <remarks>
	///     The message has zero occurrences in the capture corpus in either direction, since the dumps carry no Khitai
	///     play. The body is not inferred from that silence: the client's own method table names the parameters
	///     <c>(i_FactionId, i_Points)</c> [V1: docs/re/aoc-client-rpc-methods.json], and every sibling
	///     scalar-parameter method on these interfaces writes its arguments as bare big-endian values with no header of
	///     their own.
	///     <para>
	///         Points are signed here while the wire slot is a u32: the ladder runs -1,100,000 to +1,100,000
	///         (<c>FactionLadder</c>), so the value is written as the two's-complement bit pattern, which is what a
	///         <c>uint</c> parameter carrying a negative count means everywhere else on this protocol.
	///     </para>
	/// </remarks>
	public static class ApplyFactionPointsCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchApplyFactionPoints;

		// Canonical: the only method here that writes bytes.
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

	/// <remarks>
	///     <c>Points</c> is signed where the wire slot is a u32: the client's own bounds are symmetric about zero
	///     (<c>MinFactionPoints</c> = -1,100,000), so a standing really can be negative and a <c>uint</c> field would
	///     make "hostile" unrepresentable.
	/// </remarks>
	public sealed record ApplyFactionPoints(uint FactionId, int Points);
}
