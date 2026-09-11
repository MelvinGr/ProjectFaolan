using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Pvp
{
	/// <summary>
	///     <c>DispatchRankingCaluculationCountdown(hoursUntilCaclulation)</c> - how long until the PvP ranking is
	///     recalculated, as a single <c>[u8]</c>. The client's own spelling of the method and its field is kept,
	///     because the name is evidence.
	/// </summary>
	/// <remarks>
	///     The single captured frame carries 0x0C, i.e. 12 hours, which fixes the unit as hours: a minutes or seconds
	///     countdown could not fit a day in a byte.
	/// </remarks>
	public static class RankingCountdownCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchRankingCaluculationCountdown;

		public static byte[] Build(RankingCountdown value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(byte hoursUntilCalculation)
		{
			return Build(new RankingCountdown(hoursUntilCalculation));
		}

		public static RankingCountdown Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(RankingCountdownCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static RankingCountdown Read(ref SpanReader r)
		{
			return new RankingCountdown(r.ReadU8());
		}

		internal static void Write(FaolanStream s, RankingCountdown v)
		{
			s.WriteByte(v.HoursUntilCalculation);
		}
	}

	public sealed record RankingCountdown(byte HoursUntilCalculation);
}
