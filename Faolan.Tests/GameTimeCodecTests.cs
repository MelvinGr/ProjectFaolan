using System;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact snapshot for GameTimeIIR (0x4F57DC08) - the world clock. It is global and the client advances it
	// locally, so the server emits a fixed captured snapshot (GameServerListener.BuildGameTime frames it under a u32
	// length). Oracle: the beach world-entry GameTime record - the same bytes
	// WorldEntryGeneratedRecordsTests.BuildGameTime_ReproducesCapturedRecord validates against the capture, asserted
	// here at the CODEC boundary so a GameTimeCodec refactor is guarded on its own (not only via the listener).
	public class GameTimeCodecTests
	{
		// [subop 4F57DC08][TypeTag 0][Handle 0][FieldA 01472BDD][body: 0 / flag 01 / u64 0001920C6A024E93 / float 1.0].
		private const string CapturedRecord =
			"4f57dc08000000000000000001472bdd00000000010001920c6a024e933f800000";

		[Fact]
		public void Build_MatchesCapturedSnapshot()
		{
			Assert.Equal(CapturedRecord, Convert.ToHexString(GameTimeCodec.Build()).ToLowerInvariant());
		}
	}
}
