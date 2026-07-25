using System;
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
		// [subop 4F57DC08][TypeTag 0][Handle 0][dayTime 01472BDD00000000][flag 01][timestamp 0001920C6A024E93][rate 1.0].
		private const string CapturedRecord = "4f57dc08000000000000000001472bdd00000000010001920c6a024e933f800000";

		// The capture's own resync timestamp read as unix epoch seconds - its low word, 0x6A024E93.
		private static readonly DateTimeOffset CapturedInstant = DateTimeOffset.FromUnixTimeSeconds(0x6A024E93);

		[Fact]
		public void Build_AtTheCapturedInstant_MatchesCapturedSnapshot()
		{
			Assert.Equal(CapturedRecord, Convert.ToHexString(GameTimeCodec.Build(0x01472BDD_00000000UL, CapturedInstant)).ToLowerInvariant());
		}

		// The unit, from the client's own GameTime_t: 86400 game seconds to a game day, advancing 5 per real second.
		// Those two are what the corpus-derived 17280s real day has to equal, and the whole model rests on them.
		[Fact]
		public void TheClockUnit_MatchesTheClientsOwnConstants()
		{
			Assert.Equal(86400u, GameTimeCodec.GameDaySeconds);
			Assert.Equal(5u, GameTimeCodec.GameSecondsPerRealSecond);
			Assert.Equal(17280u, GameTimeCodec.GameDayRealSeconds);
		}

		// The time of day advances at 5x, wraps at 86400, and never leaves the day - a value outside it would put the
		// sun somewhere the client's `t % 86400` does not expect.
		[Fact]
		public void TimeOfDay_Advances5xAndWrapsWithinTheDay()
		{
			var start = GameTimeCodec.TimeOfDay(CapturedInstant);

			Assert.Equal(start + 300, GameTimeCodec.TimeOfDay(CapturedInstant.AddSeconds(60)));
			// A full real day is exactly 5 game days, so it lands back on the same time of day.
			Assert.Equal(start, GameTimeCodec.TimeOfDay(CapturedInstant.AddSeconds(GameTimeCodec.GameDayRealSeconds)));

			for (var minutes = 0; minutes < 60 * 12; minutes += 7)
				Assert.InRange(GameTimeCodec.TimeOfDay(CapturedInstant.AddMinutes(minutes)), 0u, GameTimeCodec.GameDaySeconds - 1);
		}

		// The day rolls over at the same moment the resync anchor's game-day counter ticks. They are separate fields and
		// nothing forces this - it is the reason the phase is locked to the wall clock rather than to process start.
		[Fact]
		public void TimeOfDay_WrapsWhenTheAnchorsGameDayCounterTicks()
		{
			// Walk to a real second where the game day is about to roll over.
			var atMidnight = DateTimeOffset.FromUnixTimeSeconds(1782048203 / GameTimeCodec.GameDayRealSeconds * GameTimeCodec.GameDayRealSeconds + GameTimeCodec.GameDayRealSeconds);

			Assert.Equal(0u, GameTimeCodec.TimeOfDay(atMidnight));
			Assert.Equal(GameTimeCodec.GameDaySeconds - GameTimeCodec.GameSecondsPerRealSecond, GameTimeCodec.TimeOfDay(atMidnight.AddSeconds(-1)));

			var before = GameTimeCodec.ResyncTimestamp(atMidnight.AddSeconds(-1)) >> 32;
			Assert.Equal(before + 1, GameTimeCodec.ResyncTimestamp(atMidnight) >> 32);
		}

		// dayTime is 32.32 fixed point - the seconds go in the HIGH word. Getting this backwards would put the clock
		// 4 billion game-days out.
		[Fact]
		public void DayTime_PutsTheSecondsInTheHighWord()
		{
			var dayTime = GameTimeCodec.DayTime(CapturedInstant);

			Assert.Equal(GameTimeCodec.TimeOfDay(CapturedInstant), (uint)(dayTime >> 32));
			Assert.Equal(0u, (uint)dayTime);
		}

		// The `.time` GM command shifts the phase by asking for a time of day; the offset must land on it exactly.
		[Theory]
		[InlineData(0u)]
		[InlineData(6 * 3600u)]
		[InlineData(12 * 3600u)]
		[InlineData(23 * 3600u + 59 * 60u)]
		public void TimeOfDay_CanBePhaseShiftedOntoAnyRequestedTime(uint wanted)
		{
			var offset = ((long)wanted - GameTimeCodec.TimeOfDay(CapturedInstant) + GameTimeCodec.GameDaySeconds) % GameTimeCodec.GameDaySeconds;

			Assert.Equal(wanted, GameTimeCodec.TimeOfDay(CapturedInstant, offset));
		}

		// Both halves of the anchor are wall-clock derived, so the whole u64 has to fall out of the instant alone. The
		// high word is the game-day counter (unix / 17280); asserting it here is what pins the 4.8h game day in code.
		[Fact]
		public void ResyncTimestamp_ReproducesBothCapturedAnchors()
		{
			Assert.Equal(0x0001920C_6A024E93UL, GameTimeCodec.ResyncTimestamp(CapturedInstant));
			// ...and the create-editor capture's anchor, a different day, from the same one-line encoding.
			Assert.Equal(0x00019272_6A1D3837UL, GameTimeCodec.ResyncTimestamp(DateTimeOffset.FromUnixTimeSeconds(0x6A1D3837)));
		}

		// The clock must actually move now that it is generated - the bug this replaced was a frozen anchor.
		[Fact]
		public void ResyncTimestamp_AdvancesWithWallClock()
		{
			var day = GameTimeCodec.ResyncTimestamp(CapturedInstant) >> 32;

			Assert.Equal(0x6A024E93UL + 60, GameTimeCodec.ResyncTimestamp(CapturedInstant.AddSeconds(60)) & 0xFFFFFFFF);
			Assert.Equal(day + 1, GameTimeCodec.ResyncTimestamp(CapturedInstant.AddSeconds(GameTimeCodec.GameDayRealSeconds)) >> 32);
		}
	}
}
