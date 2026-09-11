using System;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Spawn
{
	/// <summary>
	///     Splice-side builder for an entity spawn body: it takes a captured spawn as the base, constructs the header and
	///     transform (position and rotation as a typed ProtoGameCodeDynelIIR), and splices a per-character
	///     ProtoCoreCharacterIIR over the captured one. It exists for the capture-comparison tests, which rebuild a
	///     captured body from its own decoded parts; the live paths generate the whole record instead - see
	///     docs/re/player-spawn-generation.md.
	/// </summary>
	/// <remarks>
	///     The body is the spawn sub-content:
	///     <c>[u32 len][u32 subop][u32 class][u32 handle][u32 fieldA] 0x2C 0x0A pos 0x12 rot ...</c>. The transform is
	///     always the same size, so <see cref="BuildSpawnBody" /> is length-preserving; the outer Conan length and CRC are
	///     computed when the message is serialized.
	/// </remarks>
	public static class SpawnPacketBuilder
	{
		/// <summary>
		///     Offsets within the spawn body, valid only for the captured beach layout the goldens use. Other characters'
		///     spawns shift with name and equipment length, so use <see cref="SpawnBodyReader" /> or the codec for those.
		/// </summary>
		public const int HandleOffset = 12; // uint32 big-endian
		public const int NameInnerLengthOffset = 304; // 4-byte BE length prefix of the ProtoCoreCharacterIIR (@304 = 469)

		/// <summary>
		///     The u32 at 133 is a per-character value island in the pre-IIR scaffold frame, not a length: it stays fixed
		///     while the body size varies (see the validated grammar in docs/re/player-spawn-generation.md). The splice
		///     methods below still delta-adjust it on resize - the client tolerates that, and the rows-generated frame
		///     overwrites it with the stored value.
		/// </summary>
		public const int NameOuterLengthOffset = 133;

		/// <summary>
		///     The entity name - the CharacterIIR's first field - at its fixed offset in the captured layout, as a
		///     length-prefixed protobuf string. Resizing it must delta-adjust the CharacterIIR length prefix, the value
		///     island at 133 and the body length at 0.
		/// </summary>
		public const int NameTagOffset = 308; // 0x0A
		public const int PosXOffset = 24; // float little-endian
		public const int PosYOffset = 29;
		public const int PosZOffset = 34;
		public const int RotWOffset = 56;
		public const int RotXOffset = 41; // quaternion floats little-endian
		public const int RotYOffset = 46;
		public const int RotZOffset = 51;
		private const int TransformEndOffset = RotWOffset + 4; // first byte after the constructed transform (60)

		/// <summary>
		///     Builds a complete spawn body from a base template, a typed transform and a CharacterIIR: the transform is
		///     reconstructed by <see cref="BuildSpawnBody" /> and the identity, appearance and equipment spliced in by
		///     <see cref="SetCharacterMessage" />, with the pre-IIR and post-IIR regions carried over from the template
		///     verbatim. Byte-identical to the captured body when fed the template's own handle, transform and decoded IIR,
		///     which is the construction oracle.
		/// </summary>
		public static byte[] BuildSpawn(byte[] template, uint handle, ProtoVec3 position, ProtoQuat rotation, ProtoCoreCharacterIIR character)
		{
			return SetCharacterMessage(BuildSpawnBody(template, handle, position, rotation), character);
		}

		/// <summary>
		///     Constructs the spawn header and transform and appends the rest of the captured base body. The header's
		///     sub-opcode, ack and fieldA and the body length are carried over from the base; only the handle and the
		///     transform are parameterized. Byte-identical to the captured body when fed the captured handle, position and
		///     rotation.
		/// </summary>
		public static byte[] BuildSpawnBody(byte[] template, uint handle, ProtoVec3 position, ProtoQuat rotation)
		{
			var bodyLength = template.ReadU32Be(0);
			var subOpcode = template.ReadU32Be(4);
			var ack = template.ReadU32Be(8);
			var fieldA = template.ReadU32Be(16);

			using var s = new FaolanStream();
			return s
				.WriteUInt32(bodyLength)
				.WriteUInt32(subOpcode)
				.WriteUInt32(ack)
				.WriteUInt32(handle)
				.WriteUInt32(fieldA)
				.WriteByte(0x2C) // IIR group frame for the transform component
				// ProtoGameCodeDynelIIR fields 1 and 2 only: the playfield varint (field 3) and everything after it
				// come from the template tail below, so this writes the head of the message rather than a whole one.
				.WriteArray(new ProtoGameCodeDynelIIR
				{
					Position = position,
					Rotation = rotation
				}.ToProtobuf())
				.WriteArray(template.AsSpan(TransformEndOffset))
				.ToArray();
		}

		/// <summary>
		///     Replaces the entire ProtoCoreCharacterIIR component - name, stats, player_stats, bcc, inventory entries - with
		///     a re-encoded one. It works for any spawn body, player or NPC, since the two share the grammar: the
		///     CharacterIIR is located by its name signature through <see cref="SpawnBodyReader" /> rather than at a fixed
		///     offset, and on resize the body length and the IIR's own length prefix are delta-adjusted, as is the player
		///     layout's value island, which an NPC body does not carry. Returns the body unchanged if no CharacterIIR is
		///     found.
		/// </summary>
		/// <remarks>
		///     The message round-trips byte-identically (PlayerSpawnGenerationTests, NpcSpawnConstructionTests), so
		///     re-encoding the decoded message unchanged is a byte-identical no-op - the build-side oracle.
		/// </remarks>
		public static byte[] SetCharacterMessage(byte[] body, ProtoCoreCharacterIIR character)
		{
			if (!SpawnBodyReader.TryReadCharacterMessage(body, out var read))
				return body;

			var iirOffset = read.Offset;
			var oldLen = read.Length;
			var payload = character.ToProtobuf();
			var delta = payload.Length - oldLen;

			var result = new byte[body.Length + delta];
			Array.Copy(body, 0, result, 0, iirOffset); // through the length prefix, up to the message
			payload.CopyTo(result, iirOffset);
			var afterSrc = iirOffset + oldLen;
			Array.Copy(body, afterSrc, result, iirOffset + payload.Length, body.Length - afterSrc);

			AddBigEndian(result, 0, delta); // body length field
			AddBigEndian(result, iirOffset - 4, delta); // the CharacterIIR's own length prefix
			if (iirOffset == NameTagOffset) // the captured player layout also carries the value island
				AddBigEndian(result, NameOuterLengthOffset, delta);
			return result;
		}

		/// <summary>
		///     Splice-side length fixup: the CharacterIIR is re-encoded in place, so the enclosing length prefixes are
		///     delta-adjusted rather than rebuilt.
		/// </summary>
		private static void AddBigEndian(byte[] buffer, int offset, int delta)
		{
			var value = (uint)(buffer.ReadU32Be(offset) + delta);
			buffer.WriteU32Be(offset, value);
		}
	}
}
