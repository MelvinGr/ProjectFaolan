using System;
using System.Buffers.Binary;
using System.Numerics;
using Faolan.Core.Extensions;
using Faolan.GameServer.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Proves the player-spawn construction machinery (SpawnPacketBuilder + PlayerSpawnReader) generalizes to NPC
	// FA015807 bodies - the dissection finding that an NPC spawn is the byte-identical grammar as the player
	// spawn (docs/archive/spawn-dissection-plan.md Phase 4). These are the acceptance gates for building NPC spawns from
	// structured data: the transform is reconstructed from typed pos/rot, and the CharacterIIR (the NPC's
	// identity/appearance/stats/equipment) round-trips through the typed ProtoCoreCharacterIIR.
	public class NpcSpawnConstructionTests
	{
		// The full construction oracle: rebuilding the whole NPC body from its own transform AND its own decoded
		// CharacterIIR (transform via BuildSpawnBody, identity/equipment via the now-generic SetCharacterMessage)
		// reproduces the captured bytes exactly. This is the gate for driving an NPC spawn from structured data -
		// swap any field and the body stays valid.
		[Theory, InlineData("SpawnNpcCrocodile"), InlineData("SpawnNpcCasilda")]
		public void BuildSpawn_FromOwnTransformAndIir_IsByteIdentical(string golden)
		{
			var template = Golden.Bytes(golden);
			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(template, out var read));

			var handle = BinaryPrimitives.ReadUInt32BigEndian(template.AsSpan(SpawnPacketBuilder.HandleOffset, 4));
			var position = new Vector3(ReadLE(template, SpawnPacketBuilder.PosXOffset), ReadLE(template, SpawnPacketBuilder.PosYOffset), ReadLE(template, SpawnPacketBuilder.PosZOffset));
			var rotation = new Quaternion(ReadLE(template, SpawnPacketBuilder.RotXOffset), ReadLE(template, SpawnPacketBuilder.RotYOffset), ReadLE(template, SpawnPacketBuilder.RotZOffset),
				ReadLE(template, SpawnPacketBuilder.RotWOffset));

			var built = SpawnPacketBuilder.BuildSpawn(template, handle, position, rotation, read.Message);

			Assert.Equal(template, built);
		}

		// Rebuilding an NPC body from its own handle/position/rotation reproduces the captured bytes exactly -
		// the same Phase-0 gate the player spawn passes, now for an animal (Crocodile: no equipment, minimal
		// 41B post-IIR) and a humanoid (Casilda: worn equipment). Any mis-mapped transform offset fails here.
		[Theory, InlineData("SpawnNpcCrocodile"), InlineData("SpawnNpcCasilda")]
		public void BuildSpawnBody_FromOwnTransform_IsByteIdentical(string golden)
		{
			var template = Golden.Bytes(golden);

			var handle = BinaryPrimitives.ReadUInt32BigEndian(template.AsSpan(SpawnPacketBuilder.HandleOffset, 4));
			var position = new Vector3(ReadLE(template, SpawnPacketBuilder.PosXOffset), ReadLE(template, SpawnPacketBuilder.PosYOffset), ReadLE(template, SpawnPacketBuilder.PosZOffset));
			var rotation = new Quaternion(ReadLE(template, SpawnPacketBuilder.RotXOffset), ReadLE(template, SpawnPacketBuilder.RotYOffset), ReadLE(template, SpawnPacketBuilder.RotZOffset),
				ReadLE(template, SpawnPacketBuilder.RotWOffset));

			var built = SpawnPacketBuilder.BuildSpawnBody(template, handle, position, rotation);

			Assert.Equal(template, built);
		}

		// Moving an NPC changes only the mapped transform bytes; the body length is preserved.
		[Fact]
		public void BuildSpawnBody_WithNewTransform_PreservesLength()
		{
			var template = Golden.Bytes("SpawnNpcCrocodile");
			var built = SpawnPacketBuilder.BuildSpawnBody(template, 0x00088888u, new Vector3(1f, 2f, 3f), Quaternion.Identity);

			Assert.Equal(template.Length, built.Length);
			Assert.Equal(0x00088888u, BinaryPrimitives.ReadUInt32BigEndian(built.AsSpan(SpawnPacketBuilder.HandleOffset, 4)));
			Assert.Equal(1f, ReadLE(built, SpawnPacketBuilder.PosXOffset));
			Assert.Equal(3f, ReadLE(built, SpawnPacketBuilder.PosZOffset));
		}

		// The NPC's identity/appearance/stats/equipment is a ProtoCoreCharacterIIR (same type the player spawn
		// uses); the generic reader locates and decodes it, and re-encoding the decoded message is byte-identical
		// (the build-side oracle - a field swapped from the DB re-encodes to a valid splice).
		[Theory, InlineData("SpawnNpcCrocodile", "Crocodile", 14049u, false), InlineData("SpawnNpcCasilda", "Casilda", 9411u, true)]
		public void ReadCharacterMessage_DecodesNpcIir_RoundTrips(string golden, string name, uint templateId, bool hasEquipment)
		{
			var body = Golden.Bytes(golden);

			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(body, out var result));
			Assert.Equal(name, result.Message.Name);
			Assert.Equal(templateId, result.Message.npc_stats.TemplateId); // NpcStats f4 (fixed32) = identity
			Assert.Equal(hasEquipment, result.Message.InventoryEntries.Count > 0);

			// Re-encoding the decoded IIR reproduces the captured slice byte-for-byte (the splice oracle).
			var reEncoded = result.Message.ToProtobufBytes();
			Assert.Equal(body
				.AsSpan(result.Offset, result.Length)
				.ToArray(), reEncoded);
		}

		// Renaming an NPC (a length change) grows the body and adjusts the two NPC length prefixes (body length @0
		// and the CharacterIIR length prefix @iirOff-4) - and only those; NPCs have no @133 outer island.
		[Fact]
		public void SetCharacterMessage_NpcRename_AdjustsBodyAndIirLengths()
		{
			var body = Golden.Bytes("SpawnNpcCrocodile");
			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(body, out var read));
			var iirLenOffset = read.Offset - 4;
			var oldBodyLen = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0, 4));
			var oldIirLen = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(iirLenOffset, 4));

			read.Message.Name = "Ancient Crocodile"; // +8 chars
			var rebuilt = SpawnPacketBuilder.SetCharacterMessage(body, read.Message);

			var delta = rebuilt.Length - body.Length;
			Assert.Equal(8, delta);
			Assert.Equal((uint)(oldBodyLen + delta), BinaryPrimitives.ReadUInt32BigEndian(rebuilt.AsSpan(0, 4)));
			Assert.Equal((uint)(oldIirLen + delta), BinaryPrimitives.ReadUInt32BigEndian(rebuilt.AsSpan(iirLenOffset, 4)));

			// Re-reading the rebuilt body yields the new name (the splice is structurally valid).
			Assert.True(PlayerSpawnReader.TryReadCharacterMessage(rebuilt, out var reread));
			Assert.Equal("Ancient Crocodile", reread.Message.Name);
		}

		private static float ReadLE(byte[] b, int offset)
		{
			return BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(offset, 4));
		}
	}
}
