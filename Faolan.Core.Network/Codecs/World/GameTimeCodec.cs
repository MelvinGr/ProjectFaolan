using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.World
{
	/// <summary>
	///     GameTimeIIR - the world clock that drives the day/night cycle, resyncing the client's GameTime_t. 33 bytes:
	///     <c>[u32 subop][u32 class][u32 handle][u8 isToBePassed][f32 dayTime][u32 unapplied][u32 gameDay][u32 unix][f32
	///     rate]</c>. The class and handle carrier is 0 on every record we send, the clock not being addressed to an
	///     entity.
	/// </summary>
	/// <remarks>
	///     The time model - what dayTime and the resync anchor mean, and why both are generated rather than replayed -
	///     lives in <see cref="GameClock" />.
	/// </remarks>
	public static class GameTimeCodec
	{
		public const uint Opcode = (uint)IirTypes.GameTimeIIR;

		/// <summary>
		///     The u32 between dayTime and the anchor. The client's ReadSubClass loads it but the apply never forwards it to
		///     the clock; it holds 0 to 3 across the corpus (11, 37, 16 and 77 records respectively). We send the captured
		///     world-entry value.
		/// </summary>
		private const uint UnappliedWord = 1;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(GameTimeIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		/// <summary>
		///     Builds the record from an explicit time of day. It is passed in rather than read from the clock so a golden
		///     test can rebuild a captured record exactly: the time of day is server state, like a position, while the
		///     record's layout is what the capture pins.
		/// </summary>
		/// <remarks>
		///     <paramref name="rate" /> is the trailing f32 the client feeds straight into its own clock multiplier
		///     (GameTime_t +0xD4), so a GM speeding the day up is a wire change the client follows rather than a server-side
		///     fudge it drifts against.
		/// </remarks>
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

	/// <summary>The GameTimeIIR body.</summary>
	public sealed record GameTimeIIR(float DayTimeSeconds, uint UnappliedWord, uint GameDay, uint UnixSeconds, bool IsToBePassed = true, float Rate = 1.0f, uint ClassWord = 0, uint Handle = 0);
}
