using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;

namespace Faolan.GameServer.Spawn
{
	// Builds an FA015807 entity Spawn body from a captured template by overwriting the fixed-offset fields we
	// have mapped so far (handle, position, rotation). Appearance / equipment / the embedded name stay as the
	// template's until they are dissected and parameterized (see docs/spawn-dissection-plan.md and
	// Captures/dissect_spawn.py). The body is the 0x2000 sub-content:
	//   [u32 len][u32 0xFA015807][u32 0x0000C350][u32 handle][u32 fieldA] 0x2C 0x0A <pos> 0x12 <rot> ...
	// Length is never changed here, so every nested length prefix stays valid; the outer Conan length + CRC are
	// (re)computed by PacketStream when the wrapped 0x2000 packet is sent.
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
		public const int PosXOffset = 24; // float little-endian
		public const int PosYOffset = 29;
		public const int PosZOffset = 34;
		public const int RotWOffset = 56;
		public const int RotXOffset = 41; // quaternion floats little-endian
		public const int RotYOffset = 46;
		public const int RotZOffset = 51;
		private const byte TemplateNameLength = 5; // "Strie"

		public static byte[] PlayerTemplateBytes => PlayerTemplate.Value;

		private static readonly Lazy<byte[]> PlayerTemplate = new Lazy<byte[]>(() => LoadEmbedded("spawn-player-template.bin"));

		public static byte[] BuildPlayerSpawnBody(uint handle, Vector3 position, Quaternion rotation)
		{
			return BuildSpawnBody(PlayerTemplate.Value, handle, position, rotation);
		}

		// Clones a captured FA015807 body and overwrites the mapped fields.
		public static byte[] BuildSpawnBody(byte[] template, uint handle, Vector3 position, Quaternion rotation)
		{
			var body = (byte[])template.Clone();
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(HandleOffset, 4), handle);
			BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(PosXOffset, 4), position.X);
			BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(PosYOffset, 4), position.Y);
			BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(PosZOffset, 4), position.Z);
			BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(RotXOffset, 4), rotation.X);
			BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(RotYOffset, 4), rotation.Y);
			BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(RotZOffset, 4), rotation.Z);
			BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(RotWOffset, 4), rotation.W);
			return body;
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
