using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;
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
	// GENERATION side - see docs/player-spawn-generation-plan.md). The body is the 0x2000 sub-content:
	//   [u32 len][u32 0xFA015807][u32 0x0000C350][u32 handle][u32 fieldA] 0x2C 0x0A <pos> 0x12 <rot> ...
	// The transform is always the same size, so BuildSpawnBody stays length-preserving; the outer Conan length
	// + CRC are (re)computed by the caller / PacketStream.
	public static class SpawnPacketBuilder
	{
		// The CharacterStats IIR component (sex/side/race/class/health/... = ProtoCoreCharacterIIR.CharacterStats) is
		// a 0x22 length-delimited field immediately after the name. Strie layout: 0x22 @315, 67-byte payload @317.
		public const int CharacterStatsTagOffset = 315;

		// Offsets within the FA015807 body, valid for the Strie-shaped beach layout (the burst's player spawn and
		// the SpawnStrieTemplate test golden; other characters' spawns shift with name/equipment length - use
		// PlayerSpawnReader / PlayerSpawnScaffoldCodec for those).
		public const int HandleOffset = 12; // uint32 big-endian
		public const int NameInnerLengthOffset = 304; // 4-byte BE length prefix of the ProtoCoreCharacterIIR (@304 = 469)

		// NOT a length: an early misread. The u32 @133 is a per-character VALUE island in the pre-IIR scaffold
		// frame (Strie 0x322, Hoxxling 0x42B - his stays fixed while the body size varies; see the validated
		// grammar in docs/player-spawn-generation-plan.md). The splice methods below still delta-adjust it on
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
		private const byte BaseNameLength = 5; // "Strie" - the captured base body's name
		private const int TransformEndOffset = RotWOffset + 4; // first byte after the constructed transform (60)

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

		// Replaces the ENTIRE ProtoCoreCharacterIIR component (the player char message: name / stats / player_stats /
		// bcc / ... / inventory_entry) with a re-encoded one, delta-adjusting the body length @0, the @133 value
		// island and the CharacterIIR length prefix @304. This is how identity + equipment are rebuilt: the caller
		// decodes the captured base's ProtoCoreCharacterIIR, overrides name/stats/inventory from the DB, and
		// re-encodes here. The message round-trips byte-identically (PlayerSpawnGenerationTests), so re-encoding
		// the decoded message unchanged is a byte-identical no-op (the build-side oracle). Returns the body
		// unchanged if it is not the known layout (0x0A name tag at NameTagOffset).
		public static byte[] SetCharacterMessage(byte[] body, ProtoCoreCharacterIIR character)
		{
			if (body.Length <= NameTagOffset || body[NameTagOffset] != 0x0A)
				return body;

			var oldLen = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(NameInnerLengthOffset, 4));
			if (NameTagOffset + oldLen > body.Length)
				return body;

			var payload = character.ToProtobufBytes();
			var delta = payload.Length - oldLen;

			var result = new byte[body.Length + delta];
			Array.Copy(body, 0, result, 0, NameTagOffset); // through the @304 length prefix, up to the message
			payload.CopyTo(result, NameTagOffset);
			var afterSrc = NameTagOffset + oldLen;
			Array.Copy(body, afterSrc, result, NameTagOffset + payload.Length, body.Length - afterSrc);

			AddBigEndian(result, 0, delta); // body length field
			AddBigEndian(result, NameOuterLengthOffset, delta); // @133 (encloses the CharacterIIR + scaffolds)
			AddBigEndian(result, NameInnerLengthOffset, delta); // @304 (the CharacterIIR's own length prefix)
			return result;
		}

		// Returns a copy of a Strie-shaped spawn body with the name replaced. Delta-adjusts the CharacterIIR
		// length prefix (@304), the @133 value (see NameOuterLengthOffset) and the body length. If the body does
		// not look like the Strie-shaped layout (the name marker is not where expected) or the name is unusable,
		// returns the body unchanged. Fallback path only - the main path replaces the whole CharacterIIR via
		// SetCharacterMessage.
		public static byte[] SetSpawnName(byte[] body, string name)
		{
			var nameBytes = Encoding.UTF8.GetBytes(name ?? string.Empty);
			if (nameBytes.Length is 0 or > 127)
				return body;

			// Guard: only operate on the known layout (0x0A tag + the base body's "Strie" name length).
			if (body.Length <= NameTagOffset + 1 || body[NameTagOffset] != 0x0A || body[NameTagOffset + 1] != BaseNameLength)
				return body;

			var oldLen = body[NameTagOffset + 1];
			var delta = nameBytes.Length - oldLen;

			var result = new byte[body.Length + delta];
			Array.Copy(body, 0, result, 0, NameTagOffset + 1); // through the 0x0A tag
			result[NameTagOffset + 1] = (byte)nameBytes.Length; // new 1-byte length
			nameBytes.CopyTo(result, NameTagOffset + 2);
			var afterSrc = NameTagOffset + 2 + oldLen;
			Array.Copy(body, afterSrc, result, NameTagOffset + 2 + nameBytes.Length, body.Length - afterSrc);

			// Both adjusted fields sit before the name, so their offsets are unchanged; only the values change.
			AddBigEndian(result, 0, delta); // body length field
			AddBigEndian(result, NameOuterLengthOffset, delta); // @133 value island (legacy adjust, tolerated)
			AddBigEndian(result, NameInnerLengthOffset, delta); // @304 CharacterIIR length prefix
			return result;
		}

		private static void AddBigEndian(byte[] buffer, int offset, int delta)
		{
			var value = (uint)(BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4)) + delta);
			BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset, 4), value);
		}
	}
}
