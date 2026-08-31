using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.World
{
	// GameTimeIIR (0x4F57DC08) - the world clock that drives the day/night cycle; it resyncs the client's GameTime_t.
	// 33 bytes:
	//   [u32 subop][u32 class][u32 handle][u8 isToBePassed][f32 dayTime][u32 unapplied][u32 gameDay][u32 unix][f32 rate]
	// The class/handle carrier is 0 on every record we send (the clock is not addressed to an entity).
	//
	// The time MODEL - what dayTime and the resync anchor mean, and why both are generated rather than replayed - is
	// GameClock next door.
	public static class GameTimeCodec
	{
		public const uint Opcode = (uint)IirTypes.GameTimeIIR;

		// The u32 that sits between dayTime and the anchor. The client's ReadSubClass loads it but the apply never
		// forwards it to the clock; it holds 0..3 across the corpus (n = 11/37/16/77 for 0/1/2/3). We send the captured
		// world-entry value. NB this is the byte the old decode mislabelled as a "flag" at the END of dayTime.
		private const uint UnappliedWord = 1;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(GameTimeIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		// dayTime is passed in rather than read from the clock so a golden test can rebuild a captured record exactly:
		// the time of day is server state (like a position), while the record's LAYOUT is what the capture pins.
		// `rate` is the trailing f32 the client feeds straight into its own clock multiplier (GameTime_t +0xD4), so a
		// GM speeding the day up is a WIRE change the client follows rather than a server-side fudge it drifts against.
		// Defaulted, so every existing caller - and the golden test that rebuilds a captured record - is unaffected.
		public static byte[] Build(float dayTimeSeconds, DateTimeOffset now, float rate = 1.0f)
		{
			var (gameDay, unixSeconds) = GameClock.ResyncAnchor(now);
			return Build(new GameTimeIIR(dayTimeSeconds, UnappliedWord, gameDay, unixSeconds, Rate: rate));
		}

		public static byte[] Build(DateTimeOffset now, long offsetGameSeconds = 0)
		{
			return Build(GameClock.TimeOfDay(now, offsetGameSeconds), now);
		}

		public static GameTimeIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GameTimeCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static GameTimeIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "GameTimeIIR subop");
			var classWord = r.ReadU32Be();
			var handle = r.ReadU32Be();
			var isToBePassed = r.ReadU8() != 0;
			var dayTime = r.ReadF32Be();
			var unapplied = r.ReadU32Be();
			var gameDay = r.ReadU32Be();
			var unixSeconds = r.ReadU32Be();
			return new GameTimeIIR(dayTime, unapplied, gameDay, unixSeconds, isToBePassed, r.ReadF32Be(), classWord, handle);
		}

		internal static void Write(FaolanStream s, GameTimeIIR v)
		{
			s.WriteUInt32(Opcode)
				.WriteUInt32(v.ClassWord)
				.WriteUInt32(v.Handle)
				.WriteByte(v.IsToBePassed)
				.WriteFloat(v.DayTimeSeconds)
				.WriteUInt32(v.UnappliedWord)
				.WriteUInt32(v.GameDay)
				.WriteUInt32(v.UnixSeconds)
				.WriteFloat(v.Rate);
		}
	}

	// GameTimeIIR (0x4F57DC08).
	public sealed record GameTimeIIR(float DayTimeSeconds, uint UnappliedWord, uint GameDay, uint UnixSeconds, bool IsToBePassed = true, float Rate = 1.0f, uint ClassWord = 0, uint Handle = 0);
}
