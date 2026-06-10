using System;
using System.Buffers.Binary;
using System.Linq;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Protobuf;
using Faolan.GameServer.Spawn;
using Xunit;

namespace Faolan.Tests
{
	// Foundation for GENERATING the player's own SimpleCharFullUpdate spawn from DB character data (replacing the
	// captured spawn-player-template.bin). The player char message inside the spawn body is a ProtoCoreCharacterIIR
	// (name / firstname / lastname / stats / npc_stats / player_stats / bcc / targets / generic_stats / inventory).
	// This locks the key enabler: that whole message decodes and RE-ENCODES byte-identically through the client
	// schema, so the spawn's identity/stats half can be built from a typed object (populated from the character)
	// rather than spliced out of a captured template. The remaining half (the dynel-IIR wrapper + LooksPacket
	// appearance/equipment) is the next step. See docs/spawn-dissection-plan.md / [[world_entry_from_db]].
	public class PlayerSpawnGenerationTests
	{
		// The ProtoCoreCharacterIIR sub-message sits at NameTagOffset (its first field, name=0x0A) and is enclosed by
		// the 4-byte length prefix at NameInnerLengthOffset.
		private static byte[] CharacterMessage(byte[] template)
		{
			var length = (int)BinaryPrimitives.ReadUInt32BigEndian(template.AsSpan(SpawnPacketBuilder.NameInnerLengthOffset, 4));
			Assert.Equal(0x0A, template[SpawnPacketBuilder.NameTagOffset]); // field 1 (name)
			return template.AsSpan(SpawnPacketBuilder.NameTagOffset, length).ToArray();
		}

		[Fact]
		public void PlayerCharacterMessage_RoundTripsByteIdentically()
		{
			var message = CharacterMessage(SpawnPacketBuilder.PlayerTemplateBytes);

			var decoded = ((ReadOnlySpan<byte>)message).FromProtobuf<ProtoCoreCharacterIIR>();
			var reencoded = decoded.ToProtobufBytes();

			Assert.Equal(message.Length, reencoded.Length);
			Assert.True(message.SequenceEqual(reencoded), "ProtoCoreCharacterIIR did not re-encode byte-identically");
		}

		[Fact]
		public void PlayerCharacterMessage_DecodesExpectedIdentity()
		{
			var decoded = ((ReadOnlySpan<byte>)CharacterMessage(SpawnPacketBuilder.PlayerTemplateBytes)).FromProtobuf<ProtoCoreCharacterIIR>();

			Assert.Equal("Strie", decoded.Name);
			Assert.NotNull(decoded.Stats);
			Assert.NotNull(decoded.player_stats);
			Assert.NotNull(decoded.Bcc);
		}
	}
}
