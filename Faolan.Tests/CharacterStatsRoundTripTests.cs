using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Protobuf;
using Faolan.GameServer.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The build-side oracle for the spawn's CharacterStats component (sex/side/race/class/health/... = the
	// generated ProtoCoreCharacterIIR.CharacterStats): decoding the real captured block and re-encoding it is
	// byte-identical, so the component is BUILT from a typed object populated from DB character data - which is
	// what TryRebuildPlayerSpawnFromDb ships today (docs/player-spawn-generation-plan.md).
	public class CharacterStatsRoundTripTests
	{
		[Fact]
		public void TemplateCharacterStats_DecodesExpectedIdentity()
		{
			var payload = TemplateCharacterStatsPayload(out _);
			var stats = payload.FromProtobuf<ProtoCoreCharacterIIR.CharacterStats>();

			// Strie: sex code 3, race 1 (cross-checked against the other captures' player spawns, which are sex 2 /
			// race 3). Life (field 6) = 9000.
			Assert.Equal(3u, stats.Sex);
			Assert.Equal(1u, stats.Race);
			Assert.Equal(9000u, stats.Life);
		}

		[Fact]
		public void TemplateCharacterStats_ReEncodesByteIdentical()
		{
			var payload = TemplateCharacterStatsPayload(out _);
			var stats = payload.FromProtobuf<ProtoCoreCharacterIIR.CharacterStats>();

			var reEncoded = stats.ToProtobufBytes();

			Assert.Equal(Convert.ToHexString(payload), Convert.ToHexString(reEncoded));
		}

		private static ReadOnlySpan<byte> TemplateCharacterStatsPayload(out byte[] template)
		{
			template = Golden.Bytes("SpawnStrieTemplate");
			var p = SpawnPacketBuilder.CharacterStatsTagOffset;
			Assert.Equal(0x22, template[p]); // 0x22 length-delimited field tag
			int len = template[p + 1]; // 67 < 128, single-byte length
			return template.AsSpan(p + 2, len);
		}
	}
}
