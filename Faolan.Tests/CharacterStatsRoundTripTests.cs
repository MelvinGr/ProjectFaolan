using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Protobuf;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The build-side oracle for the spawn's CharacterStats component (sex/side/race/class/health/... = the
	// generated ProtoCoreCharacterIIR.CharacterStats): decoding the real captured block and re-encoding it is
	// byte-identical, so the component is built from a typed object populated from DB character data - which is
	// what TryRebuildPlayerSpawnFromDb ships today (docs/re/player-spawn-generation.md).
	public class CharacterStatsRoundTripTests
	{
		[Fact]
		public void TemplateCharacterMessage_ReEncodesByteIdentical()
		{
			var template = Golden.Bytes("SpawnAssassinTemplate");
			Assert.True(SpawnBodyReader.TryReadCharacterMessage(template, out var read));

			// Re-encoding the typed ProtoCoreCharacterIIR (which contains CharacterStats) reproduces the captured
			// message slice byte-for-byte - the build-side oracle, located structurally (no fixed Strie offset).
			var captured = template.AsSpan(read.Offset, read.Length).ToArray();
			Assert.Equal(Convert.ToHexString(captured), Convert.ToHexString(read.Message.ToProtobuf()));
		}

		[Fact]
		public void TemplateCharacterStats_DecodesExpectedIdentity()
		{
			var stats = TemplateCharacterStats();

			// Strie: sex code 3, race 1 (cross-checked against the other captures' player spawns, which are sex 2 /
			// race 3). Life (field 6) = 9000.
			Assert.Equal(3u, stats.Sex);
			Assert.Equal(1u, stats.Race);
			Assert.Equal(9000u, stats.Life);
		}

		// Locates the ProtoCoreCharacterIIR structurally (SpawnBodyReader, not a fixed byte offset) and returns its
		// typed CharacterStats sub-message.
		private static ProtoCoreCharacterIIR.CharacterStats TemplateCharacterStats()
		{
			var template = Golden.Bytes("SpawnAssassinTemplate");
			Assert.True(SpawnBodyReader.TryReadCharacterMessage(template, out var read));
			Assert.NotNull(read.Message.Stats);
			return read.Message.Stats;
		}
	}
}
