using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using Faolan.Core.Data;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Protobuf;

namespace Faolan.GameServer.Spawn
{
	// Builds an FA015807 entity Spawn body. The header (subop/ack/handle/fieldA) and the transform - the entity's
	// position + rotation, i.e. ProtoGameCodeDynelIIR.position/rotation as typed ProtoVec3/ProtoQuat - are
	// CONSTRUCTED; the remaining body (appearance / equipment / stats / the embedded name) is still copied verbatim
	// from the captured template until those IIR components are dissected (see docs/spawn-dissection-plan.md and
	// Captures/dissect_spawn.py). The body is the 0x2000 sub-content:
	//   [u32 len][u32 0xFA015807][u32 0x0000C350][u32 handle][u32 fieldA] 0x2C 0x0A <pos> 0x12 <rot> ...
	// The transform is always the same size, so this stays length-preserving (every nested length prefix in the
	// tail remains valid); the outer Conan length + CRC are (re)computed by the caller / PacketStream.
	public static class SpawnPacketBuilder
	{
		// Offsets within the FA015807 body, verified against the Strie player spawn (Captures/dissect_spawn.py).
		public const int HandleOffset = 12; // uint32 big-endian
		public const int NameInnerLengthOffset = 304; // 4-byte BE enclosing length (@304 = 469)
		public const int NameOuterLengthOffset = 133; // 4-byte BE enclosing length (@133 = 802)

		// The entity name lives at a fixed offset in the Strie player template as a length-prefixed protobuf string
		// `0A <len> <utf8>`. It is enclosed by two 4-byte big-endian length prefixes (verified self-consistent:
		// within @133's 802-byte block, 167 + (4 + @304's 469-byte block) + 162 = 802), then the body length and
		// the packet length. Changing the name length must add the same delta to all four.
		public const int NameTagOffset = 308; // 0x0A
		// The CharacterStats IIR component (sex/side/race/class/health/... = ProtoCoreCharacterIIR.CharacterStats) is
		// a 0x22 length-delimited field immediately after the name. Strie template: 0x22 @315, 67-byte payload @317.
		public const int CharacterStatsTagOffset = 315;
		public const int PosXOffset = 24; // float little-endian
		public const int PosYOffset = 29;
		public const int PosZOffset = 34;
		public const int RotWOffset = 56;
		public const int RotXOffset = 41; // quaternion floats little-endian
		public const int RotYOffset = 46;
		public const int RotZOffset = 51;
		private const int TransformEndOffset = RotWOffset + 4; // first byte after the constructed transform (60)
		private const byte TemplateNameLength = 5; // "Strie"

		public static byte[] PlayerTemplateBytes => PlayerTemplate.Value;

		private static readonly Lazy<byte[]> PlayerTemplate = new Lazy<byte[]>(() => LoadEmbedded("spawn-player-template.bin"));

		public static byte[] BuildPlayerSpawnBody(uint handle, Vector3 position, Quaternion rotation)
		{
			return BuildSpawnBody(PlayerTemplate.Value, handle, position, rotation);
		}

		// Constructs the FA015807 header + transform (position/rotation as typed ProtoVec3/ProtoQuat) and appends the
		// rest of the captured template body via WriteArray (appearance / stats / name - not yet dissected). The
		// header's subop/ack/fieldA and the body length are carried over from the template; only the handle and the
		// transform are parameterized. Byte-identical to the captured body when fed the captured handle/position/
		// rotation - SpawnPacketBuilderTests round-trips the embedded player template.
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
				.WriteByte(0x0A).WriteArrayByteLength(new ProtoVec3
				{
					X = position.X,
					Y = position.Y,
					Z = position.Z
				}.ToProtobufBytes())
				.WriteByte(0x12).WriteArrayByteLength(new ProtoQuat
				{
					X = rotation.X,
					Y = rotation.Y,
					Z = rotation.Z,
					W = rotation.W
				}.ToProtobufBytes())
				.WriteArray(template.AsSpan(TransformEndOffset))
				.ToArray();
		}

		// Returns a copy of a Strie-template spawn body with the name replaced. Adjusts the two enclosing length
		// prefixes and the body length field by the name-length delta. If the body does not look like the Strie
		// template (the name marker is not where expected) or the name is unusable, returns the body unchanged.
		public static byte[] SetSpawnName(byte[] body, string name)
		{
			var nameBytes = Encoding.UTF8.GetBytes(name ?? string.Empty);
			if (nameBytes.Length is 0 or > 127)
				return body;

			// Guard: only operate on the known template layout (0x0A tag + the template's name length).
			if (body.Length <= NameTagOffset + 1 || body[NameTagOffset] != 0x0A || body[NameTagOffset + 1] != TemplateNameLength)
				return body;

			var oldLen = body[NameTagOffset + 1];
			var delta = nameBytes.Length - oldLen;

			var result = new byte[body.Length + delta];
			Array.Copy(body, 0, result, 0, NameTagOffset + 1); // through the 0x0A tag
			result[NameTagOffset + 1] = (byte)nameBytes.Length; // new 1-byte length
			nameBytes.CopyTo(result, NameTagOffset + 2);
			var afterSrc = NameTagOffset + 2 + oldLen;
			Array.Copy(body, afterSrc, result, NameTagOffset + 2 + nameBytes.Length, body.Length - afterSrc);

			// Every enclosing length sits before the name, so its offset is unchanged; only the value grows by delta.
			AddBigEndian(result, 0, delta); // body length field
			AddBigEndian(result, NameOuterLengthOffset, delta); // @133
			AddBigEndian(result, NameInnerLengthOffset, delta); // @304
			return result;
		}

		// Rebuilds the CharacterStats component (the 0x22 field right after the name) from a typed CharacterStats,
		// adjusting the same enclosing length prefixes SetSpawnName does (@0 body length, @133, @304 - both enclose
		// this block) by the size delta. Operates on the known Strie-template layout (0x22 at CharacterStatsTagOffset,
		// single-byte length prefix), returning the body unchanged otherwise. Apply BEFORE SetSpawnName: this block
		// follows the name, so the name's own splice is unaffected, but this offset is only valid while the name is
		// still the template's. Re-encoding the captured stats unchanged is a byte-identical no-op (the build-side
		// oracle); override only the fields whose DB mapping is confirmed.
		public static byte[] SetCharacterStats(byte[] body, ProtoCoreCharacterIIR.CharacterStats stats)
		{
			if (body.Length <= CharacterStatsTagOffset + 1 || body[CharacterStatsTagOffset] != 0x22)
				return body;

			var oldLen = body[CharacterStatsTagOffset + 1];
			if (oldLen >= 0x80)
				return body; // multi-byte length prefix not handled

			var payload = stats.ToProtobufBytes();
			if (payload.Length >= 0x80)
				return body; // keep the length prefix a single byte

			var delta = payload.Length - oldLen;
			var payloadStart = CharacterStatsTagOffset + 2;
			var afterSrc = payloadStart + oldLen;

			var result = new byte[body.Length + delta];
			Array.Copy(body, 0, result, 0, payloadStart); // through the 0x22 tag + length byte
			result[CharacterStatsTagOffset + 1] = (byte)payload.Length;
			payload.CopyTo(result, payloadStart);
			Array.Copy(body, afterSrc, result, payloadStart + payload.Length, body.Length - afterSrc);

			AddBigEndian(result, 0, delta); // body length field
			AddBigEndian(result, NameOuterLengthOffset, delta); // @133 (encloses name + CharacterStats)
			AddBigEndian(result, NameInnerLengthOffset, delta); // @304
			return result;
		}

		// Replaces the ENTIRE ProtoCoreCharacterIIR component (the player char message: name / stats / player_stats /
		// bcc / ... / inventory_entry) with a re-encoded one, adjusting the three enclosing length prefixes (@0 body
		// length, @133, @304) by the size delta. This is how equipment is rebuilt: the worn items are the message's
		// InventoryEntries, so the caller decodes the template's ProtoCoreCharacterIIR, overrides name/stats/inventory
		// from the DB, and re-encodes here. The message round-trips byte-identically (PlayerSpawnGenerationTests), so
		// re-encoding the decoded template unchanged is a byte-identical no-op (the build-side oracle). Returns the
		// body unchanged if it is not the known template layout (0x0A name tag at NameTagOffset).
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

		private static void AddBigEndian(byte[] buffer, int offset, int delta)
		{
			var value = (uint)(BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4)) + delta);
			BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset, 4), value);
		}

		private static byte[] LoadEmbedded(string suffix)
		{
			var asm = Assembly.GetExecutingAssembly();
			var name = asm.GetManifestResourceNames().First(n => n.EndsWith(suffix, StringComparison.Ordinal));
			using var stream = asm.GetManifestResourceStream(name)!;
			using var ms = new MemoryStream();
			stream.CopyTo(ms);
			return ms.ToArray();
		}
	}
}
