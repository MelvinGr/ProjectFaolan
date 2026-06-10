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

		// Build-side oracle: replacing the CharacterIIR component with its own decoded form is a byte-identical no-op.
		[Fact]
		public void SetCharacterMessage_WithUnchangedDecodedMessage_IsByteIdentical()
		{
			var body = SpawnPacketBuilder.PlayerTemplateBytes;
			var message = ((ReadOnlySpan<byte>)CharacterMessage(body)).FromProtobuf<ProtoCoreCharacterIIR>();

			var rebuilt = SpawnPacketBuilder.SetCharacterMessage(body, message);

			Assert.Equal(body, rebuilt);
		}

		// Changing the message (here the name, a different length) resizes the body, keeps it decodable, and fixes up
		// the enclosing length prefixes (the body-length field @0 == total - 4).
		[Fact]
		public void SetCharacterMessage_ChangingName_ResizesAndStaysConsistent()
		{
			var body = SpawnPacketBuilder.PlayerTemplateBytes;
			var message = ((ReadOnlySpan<byte>)CharacterMessage(body)).FromProtobuf<ProtoCoreCharacterIIR>();
			message.Name = "Tester"; // 6 chars vs "Strie" (5)

			var rebuilt = SpawnPacketBuilder.SetCharacterMessage(body, message);

			Assert.Equal(body.Length + 1, rebuilt.Length);
			Assert.Equal((uint)(rebuilt.Length - 4), BinaryPrimitives.ReadUInt32BigEndian(rebuilt.AsSpan(0, 4)));
			var back = ((ReadOnlySpan<byte>)CharacterMessage(rebuilt)).FromProtobuf<ProtoCoreCharacterIIR>();
			Assert.Equal("Tester", back.Name);
			Assert.NotNull(back.Bcc); // the tail (after the name) survived the splice
		}
	}
}
