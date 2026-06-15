using System;
using System.Buffers.Binary;
using System.Numerics;
using Faolan.Core.Data;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Protobuf;

namespace Faolan.GameServer.Spawn
{
	// SPLICE-side builder for an FA015807 entity Spawn body: takes a captured spawn (the map burst's player
	// spawn) as the base, constructs the header + transform (position/rotation as typed ProtoVec3/ProtoQuat,
	// i.e. ProtoGameCodeDynelIIR), and splices the per-character ProtoCoreCharacterIIR (name/stats/equipment)
	// over the captured one (SetCharacterMessage). The scaffold frame around the CharacterIIR is then
	// regenerated from the character's DB rows by PlayerSpawnScaffoldCodec/PlayerSpawnScaffoldRows (the
	// GENERATION side - see docs/archive/player-spawn-generation-plan.md). The body is the 0x2000 sub-content:
	//   [u32 len][u32 0xFA015807][u32 0x0000C350][u32 handle][u32 fieldA] 0x2C 0x0A <pos> 0x12 <rot> ...
	// The transform is always the same size, so BuildSpawnBody stays length-preserving; the outer Conan length
	// + CRC are (re)computed by the caller / PacketStream.
	public static class SpawnPacketBuilder
	{
		// Offsets within the FA015807 body, valid for the Strie-shaped beach layout (the burst's player spawn and
		// the SpawnStrieTemplate test golden; other characters' spawns shift with name/equipment length - use
		// PlayerSpawnReader / PlayerSpawnScaffoldCodec for those).
		public const int HandleOffset = 12; // uint32 big-endian
		public const int NameInnerLengthOffset = 304; // 4-byte BE length prefix of the ProtoCoreCharacterIIR (@304 = 469)

		// NOT a length: an early misread. The u32 @133 is a per-character VALUE island in the pre-IIR scaffold
		// frame (Strie 0x322, Hoxxling 0x42B - his stays fixed while the body size varies; see the validated
		// grammar in docs/archive/player-spawn-generation-plan.md). The splice methods below still delta-adjust it on
		// resize - the client demonstrably tolerates that, and the rows-generated frame (PlayerSpawnScaffoldRows)
		// overwrites it with the stored value anyway.
		public const int NameOuterLengthOffset = 133;

		// The entity name (the CharacterIIR's first field) at its fixed offset in the Strie-shaped layout, as a
		// length-prefixed protobuf string `0A <len> <utf8>`. Resizing it must delta-adjust the CharacterIIR length
		// prefix (@304), the value @133 (see above) and the body length @0.
		public const int NameTagOffset = 308; // 0x0A
		public const int PosXOffset = 24; // float little-endian
		public const int PosYOffset = 29;
		public const int PosZOffset = 34;
		public const int RotWOffset = 56;
		public const int RotXOffset = 41; // quaternion floats little-endian
		public const int RotYOffset = 46;
		public const int RotZOffset = 51;
		private const int TransformEndOffset = RotWOffset + 4; // first byte after the constructed transform (60)

		// Builds a complete FA015807 body from a base template + a typed transform + a CharacterIIR: the transform
		// (handle/position/rotation) is reconstructed by BuildSpawnBody and the identity/appearance/equipment is
		// spliced in by SetCharacterMessage. The pre-IIR float block and the post-IIR scaffold are carried over
		// from the template verbatim (still raw, per the player spawn). Byte-identical to the captured body when
		// fed the template's own handle/transform + decoded IIR (the construction oracle).
		public static byte[] BuildSpawn(byte[] template, uint handle, Vector3 position, Quaternion rotation, ProtoCoreCharacterIIR character)
		{
			return SetCharacterMessage(BuildSpawnBody(template, handle, position, rotation), character);
		}

		// Constructs the FA015807 header + transform (position/rotation as typed ProtoVec3/ProtoQuat) and appends
		// the rest of the captured base body via WriteArray (the CharacterIIR + scaffold are replaced downstream
		// by SetCharacterMessage / the rows-generated frame). The header's subop/ack/fieldA and the body length
		// are carried over from the base; only the handle and the transform are parameterized. Byte-identical to
		// the captured body when fed the captured handle/position/rotation (SpawnPacketBuilderTests, against the
		// SpawnStrieTemplate golden).
		public static byte[] BuildSpawnBody(byte[] template, uint handle, Vector3 position, Quaternion rotation)
		{
			var bodyLength = BinaryPrimitives.ReadUInt32BigEndian(template.AsSpan(0, 4));
			var subOpcode = BinaryPrimitives.ReadUInt32BigEndian(template.AsSpan(4, 4));
			var ack = BinaryPrimitives.ReadUInt32BigEndian(template.AsSpan(8, 4));
			var fieldA = BinaryPrimitives.ReadUInt32BigEndian(template.AsSpan(16, 4));

			return new FaolanStream()
				.WriteUInt32(bodyLength)
				.WriteUInt32(subOpcode)
				.WriteUInt32(ack)
				.WriteUInt32(handle)
				.WriteUInt32(fieldA)
				.WriteByte(0x2C) // IIR group frame for the transform component
				.WriteByte(0x0A)
				.WriteArrayByteLength(new ProtoVec3
				{
					X = position.X,
					Y = position.Y,
					Z = position.Z
				}.ToProtobufBytes())
				.WriteByte(0x12)
				.WriteArrayByteLength(new ProtoQuat
				{
					X = rotation.X,
					Y = rotation.Y,
					Z = rotation.Z,
					W = rotation.W
				}.ToProtobufBytes())
				.WriteArray(template.AsSpan(TransformEndOffset))
				.ToArray();
		}

		// Replaces the ENTIRE ProtoCoreCharacterIIR component (name / stats / player_stats / bcc / ... /
		// inventory_entry) with a re-encoded one. Works for ANY FA015807 body (player OR NPC - they share the
		// grammar, see docs/archive/spawn-dissection-plan.md Phase 4): the CharacterIIR is located by its name signature
		// via PlayerSpawnReader (not the fixed Strie offset), and on resize the body length @0 and the IIR's own
		// length prefix @(iirOff-4) are delta-adjusted. The Strie-shaped player layout additionally carries the
		// @133 value island, adjusted only there (NPCs have no such outer length). This is how identity + equipment
		// are rebuilt: the caller decodes the captured base's IIR, overrides fields from the DB, and re-encodes
		// here. The message round-trips byte-identically (PlayerSpawnGenerationTests / NpcSpawnConstructionTests),
		// so re-encoding the decoded message unchanged is a byte-identical no-op (the build-side oracle). Returns
		// the body unchanged if no CharacterIIR is found.
		public static byte[] SetCharacterMessage(byte[] body, ProtoCoreCharacterIIR character)
		{
			if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
				return body;

			var iirOffset = read.Offset;
			var oldLen = read.Length;
			var payload = character.ToProtobufBytes();
			var delta = payload.Length - oldLen;

			var result = new byte[body.Length + delta];
			Array.Copy(body, 0, result, 0, iirOffset); // through the @(iirOff-4) length prefix, up to the message
			payload.CopyTo(result, iirOffset);
			var afterSrc = iirOffset + oldLen;
			Array.Copy(body, afterSrc, result, iirOffset + payload.Length, body.Length - afterSrc);

			AddBigEndian(result, 0, delta); // body length field
			AddBigEndian(result, iirOffset - 4, delta); // the CharacterIIR's own length prefix
			if (iirOffset == NameTagOffset) // Strie-shaped player layout: also the @133 value island (player only)
				AddBigEndian(result, NameOuterLengthOffset, delta);
			return result;
		}

		private static void AddBigEndian(byte[] buffer, int offset, int delta)
		{
			var value = (uint)(BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4)) + delta);
			BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset, 4), value);
		}
	}
}
