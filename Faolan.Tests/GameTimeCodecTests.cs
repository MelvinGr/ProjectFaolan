using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact snapshot for GameTimeIIR (0x4F57DC08) - the world clock. The record's resync timestamp is generated
	// from the wall clock rather than replayed (#351), so the gate is: feed the instant the capture was taken and the
	// codec must reproduce the captured bytes exactly. That keeps this a real oracle - the expected bytes come from the
	// capture, and the generated timestamp has to land on the captured u64 on its own.
	//
	// WorldEntryGeneratedRecordsTests.BuildGameTime_ReproducesCapturedRecord asserts the same bytes through the
	// listener; this pins the CODEC boundary so a GameTimeCodec refactor is guarded on its own.
	public class GameTimeCodecTests
	{
		// [subop 4F57DC08][TypeTag 0][Handle 0][bool 01][f32 dayTime 472BDD00 = 43997s = 12:13][u32 word 1][u32 gameDay 0001920C][u32 unix 6A024E93][f32 rate 1.0].
		private const string CapturedRecord = "4f57dc08000000000000000001472bdd00000000010001920c6a024e933f800000";

		// The capture's own resync timestamp read as unix epoch seconds - its low word, 0x6A024E93.
		private static readonly DateTimeOffset CapturedInstant = DateTimeOffset.FromUnixTimeSeconds(0x6A024E93);

		[Fact]
		public void Build_AtTheCapturedInstant_MatchesCapturedSnapshot()
		{
			Assert.Equal(CapturedRecord, Convert.ToHexString(GameTimeCodec.Build(BitConverter.UInt32BitsToSingle(0x472BDD00), CapturedInstant)).ToLowerInvariant());
		}

		// The unit, from the client's own GameTime_t: 86400 game seconds to a game day, advancing 5 per real second.
		// Those two are what the corpus-derived 17280s real day has to equal, and the whole model rests on them.
		[Fact]
		public void TheClockUnit_MatchesTheClientsOwnConstants()
		{
			Assert.Equal(86400u, GameClock.GameDaySeconds);
			Assert.Equal(5u, GameClock.GameSecondsPerRealSecond);
			Assert.Equal(17280u, GameClock.GameDayRealSeconds);
		}

		// The time of day advances at 5x, wraps at 86400, and never leaves the day - a value outside it would put the
		// sun somewhere the client's `t % 86400` does not expect.
		[Fact]
		public void TimeOfDay_Advances5xAndWrapsWithinTheDay()
		{
			var start = GameClock.TimeOfDay(CapturedInstant);

			Assert.Equal(start + 300f, GameClock.TimeOfDay(CapturedInstant.AddSeconds(60)));
			// A full real day is exactly 5 game days, so it lands back on the same time of day.
			Assert.Equal(start, GameClock.TimeOfDay(CapturedInstant.AddSeconds(GameClock.GameDayRealSeconds)));

			for (var minutes = 0; minutes < 60 * 12; minutes += 7)
				Assert.InRange(GameClock.TimeOfDay(CapturedInstant.AddMinutes(minutes)), 0f, GameClock.GameDaySeconds - 1f);
		}

		// The day rolls over at the same moment the resync anchor's game-day counter ticks. They are separate fields and
		// nothing forces this - it is the reason the phase is locked to the wall clock rather than to process start.
		[Fact]
		public void TimeOfDay_WrapsWhenTheAnchorsGameDayCounterTicks()
		{
			// Walk to a real second where the game day is about to roll over.
			var atMidnight = DateTimeOffset.FromUnixTimeSeconds(1782048203 / GameClock.GameDayRealSeconds * GameClock.GameDayRealSeconds + GameClock.GameDayRealSeconds);

			Assert.Equal(0f, GameClock.TimeOfDay(atMidnight));
			Assert.Equal((float)(GameClock.GameDaySeconds - GameClock.GameSecondsPerRealSecond), GameClock.TimeOfDay(atMidnight.AddSeconds(-1)));

			var before = GameClock.ResyncTimestamp(atMidnight.AddSeconds(-1)) >> 32;
			Assert.Equal(before + 1, GameClock.ResyncTimestamp(atMidnight) >> 32);
		}

		// dayTime is an IEEE FLOAT of game seconds, and it starts one byte later than the record's old reading claimed
		// (#351). This is the assertion that would have caught the mistake: the captured word decodes to a sane time of
		// day as a float and to nonsense as an integer, and the bool in front of it is what the client validates - a
		// dayTime written where that bool belongs is rejected outright ("isToBePassed on bool has value G").
		[Fact]
		public void DayTime_IsAFloatOfGameSeconds_AtTheCapturedOffset()
		{
			var record = GameTimeCodec.Build(BitConverter.UInt32BitsToSingle(0x472BDD00), CapturedInstant);

			// [u32 subop][u32 typeTag][u32 handle][u8 bool][f32 dayTime]...
			Assert.Equal(1, record[12]);
			Assert.Equal(0x472BDD00u, record.ReadU32Be(13));

			var seconds = BitConverter.UInt32BitsToSingle(0x472BDD00);
			Assert.Equal(43997f, seconds);                                       // 12:13, which is what the client renders
			Assert.InRange(seconds, 0f, GameClock.GameDaySeconds);           // ...a real time of day
			Assert.True(0x472BDD00u > GameClock.GameDaySeconds * 1000, "read as an INTEGER the same word is 1.19e9 - nowhere near a time of day, which is how the float reading was confirmed");
		}

		// The `.time` GM command shifts the phase by asking for a time of day; the offset must land on it exactly.
		[Theory]
		[InlineData(0u)]
		[InlineData(6 * 3600u)]
		[InlineData(12 * 3600u)]
		[InlineData(23 * 3600u + 59 * 60u)]
		public void TimeOfDay_CanBePhaseShiftedOntoAnyRequestedTime(uint wanted)
		{
			var offset = ((long)wanted - (long)GameClock.TimeOfDay(CapturedInstant) + GameClock.GameDaySeconds) % GameClock.GameDaySeconds;

			Assert.Equal((float)wanted, GameClock.TimeOfDay(CapturedInstant, offset));
		}

		// Both halves of the anchor are wall-clock derived, so the whole u64 has to fall out of the instant alone. The
		// high word is the game-day counter (unix / 17280); asserting it here is what pins the 4.8h game day in code.
		[Fact]
		public void ResyncTimestamp_ReproducesBothCapturedAnchors()
		{
			Assert.Equal(0x0001920C_6A024E93UL, GameClock.ResyncTimestamp(CapturedInstant));
			// ...and the create-editor capture's anchor, a different day, from the same one-line encoding.
			Assert.Equal(0x00019272_6A1D3837UL, GameClock.ResyncTimestamp(DateTimeOffset.FromUnixTimeSeconds(0x6A1D3837)));
		}

		// The clock must actually move now that it is generated - the bug this replaced was a frozen anchor.
		[Fact]
		public void ResyncTimestamp_AdvancesWithWallClock()
		{
			var day = GameClock.ResyncTimestamp(CapturedInstant) >> 32;

			Assert.Equal(0x6A024E93UL + 60, GameClock.ResyncTimestamp(CapturedInstant.AddSeconds(60)) & 0xFFFFFFFF);
			Assert.Equal(day + 1, GameClock.ResyncTimestamp(CapturedInstant.AddSeconds(GameClock.GameDayRealSeconds)) >> 32);
		}
	}
}
