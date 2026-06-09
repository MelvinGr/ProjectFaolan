using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Protobuf;
using Faolan.GameServer.Spawn;
using Xunit;

namespace Faolan.Tests
{
	// The build-side oracle for Phase 3 (appearance/identity from DB, docs/spawn-dissection-plan.md). The entity
	// Spawn's CharacterStats component (sex/side/race/class/health/...) is the generated ProtoCoreCharacterIIR.
	// CharacterStats. Proving that decoding the real captured block and re-encoding it is byte-identical means we
	// can BUILD this component from a typed object (eventually from DB character data) and the client will accept
	// the exact bytes - so the next strangler step can replace it the way the transform already is.
	public class CharacterStatsRoundTripTests
	{
		private static ReadOnlySpan<byte> TemplateCharacterStatsPayload(out byte[] template)
		{
			template = SpawnPacketBuilder.PlayerTemplateBytes;
			var p = SpawnPacketBuilder.CharacterStatsTagOffset;
			Assert.Equal(0x22, template[p]); // 0x22 length-delimited field tag
			int len = template[p + 1]; // 67 < 128, single-byte length
			return template.AsSpan(p + 2, len);
		}

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
	}
}
