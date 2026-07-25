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
			Assert.Equal(CapturedRecord, Convert.ToHexString(GameTimeCodec.Build(CapturedInstant)).ToLowerInvariant());
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
