using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Pvp
{
	// DispatchRankingCaluculationCountdown(hoursUntilCaclulation) - CSPlayerInterface 0x3D. How long until the PvP
	// ranking is recalculated (#874). The client's own spelling of both the method and its field is kept, as
	// everywhere else: the name is evidence, not prose.
	//
	//   [u8 hoursUntilCaclulation]
	//
	// One byte. The single captured frame carries 0x0C = 12 hours, which says the unit is HOURS and not something
	// finer - a minutes or seconds countdown could not fit a day in a byte.
	//
	// Belongs to the Massive-PvP / minigame ranking work (#146).
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
