using System;
using System.Buffers.Binary;
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

		// Splicing the captured stats back in unchanged is a byte-identical no-op (delta 0) - the build-side oracle
		// for SetCharacterStats.
		[Fact]
		public void SetCharacterStats_WithCapturedStats_IsByteIdentical()
		{
			var stats = TemplateCharacterStatsPayload(out var template).FromProtobuf<ProtoCoreCharacterIIR.CharacterStats>();

			var rebuilt = SpawnPacketBuilder.SetCharacterStats(template, stats);

			Assert.Equal(Convert.ToHexString(template), Convert.ToHexString(rebuilt));
		}

		// A field override that grows the component must add the same delta to the body length and both enclosing
		// length prefixes (@133, @304), and the change must round-trip back out.
		[Fact]
		public void SetCharacterStats_GrowingOverride_AdjustsEnclosingLengths()
		{
			var stats = TemplateCharacterStatsPayload(out var template).FromProtobuf<ProtoCoreCharacterIIR.CharacterStats>();
			stats.Life = 0xFFFFFFFF; // 5-byte varint, larger than the template's 2-byte life -> grows the payload

			var rebuilt = SpawnPacketBuilder.SetCharacterStats(template, stats);
			var delta = rebuilt.Length - template.Length;

			Assert.True(delta > 0, "override should have grown the component");
			AssertBeDelta(template, rebuilt, 0, delta);
			AssertBeDelta(template, rebuilt, SpawnPacketBuilder.NameOuterLengthOffset, delta);
			AssertBeDelta(template, rebuilt, SpawnPacketBuilder.NameInnerLengthOffset, delta);

			int len = rebuilt[SpawnPacketBuilder.CharacterStatsTagOffset + 1];
			var back = rebuilt.AsSpan(SpawnPacketBuilder.CharacterStatsTagOffset + 2, len).FromProtobuf<ProtoCoreCharacterIIR.CharacterStats>();
			Assert.Equal(0xFFFFFFFFu, back.Life);
		}

		private static void AssertBeDelta(byte[] before, byte[] after, int offset, int delta)
		{
			var b = BinaryPrimitives.ReadUInt32BigEndian(before.AsSpan(offset, 4));
			var a = BinaryPrimitives.ReadUInt32BigEndian(after.AsSpan(offset, 4));
			Assert.Equal((uint)(b + delta), a);
		}
	}
}
