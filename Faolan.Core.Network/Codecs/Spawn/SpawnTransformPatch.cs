using System;
using System.Numerics;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// SEND-TIME in-place patches to an already-built spawn RECORD's transform. Not a codec - the encoding lives in
	// SimpleCharFullUpdateCodec; these are the two values that are only known at the moment of sending and are
	// therefore stamped over a record built earlier:
	//
	//   - the PLAYFIELD INSTANCE token, which is per-recipient-session (SendWorldSpawn stamps the entering player's
	//     LIVE token; it is not known until they actually enter), and
	//   - the entity's CURRENT POSITION, because a cached NPC record holds the position it was built with at map load.
	//
	// ⚠️ The offsets here are PATCH offsets into a record whose head is fixed-width, not parse offsets (#530 §7):
	//   [u32 subop][u32 class][u32 handle][u32 fieldA][u8 transformLen][0x0A][u8 len][vec3][0x12][u8 len][quat]...
	// Every field ahead of them is decoded by the codec; nothing here reads a body it has not accounted for. The pure
	// "construct" endgame is to rebuild the record per-send with the live values, which needs the source row/handle
	// carried on WorldEntity; until then this targeted patch is the seam.
	public static class SpawnTransformPatch
	{
		// Where the position's three little-endian fixed32 live in the record, and the stride between them (each is
		// preceded by its own protobuf field tag). The head is [subop][class][handle][fieldA][transformLen][0x0A]
		// [vec3 len][0x0D] = 20 bytes.
		private const int PosXOffset = 20;
		private const int PosStride = 5;

		// Overwrites the transform's playfield-instance varint in `record` (the [u32 iirType][record] form the codec
		// builds) with `value`, in place. Returns false (leaving the record untouched) if the value is out of the
		// 4-byte-varint range, the transform can't be located, or the existing token isn't a 4-byte varint. Session
		// tokens are 4-byte LEB128 varints, so the rewrite is splice-free (no length field shifts).
		public static bool TryRewritePlayfield(byte[] record, uint value)
		{
			if (value is < 1u << 21 or >= 1u << 28 || record == null || record.Length <= 4)
				return false;

			var body = record.AsSpan(4); // skip the [u32 iirType]
			if (!TryPlayfieldVarintStart(body, out var start) || start + 4 > body.Length)
				return false;

			for (var k = 0; k < 4; k++) // the existing token must also be a 4-byte varint
			{
				if ((body[start + k] & 0x80) != 0 != k < 3)
					return false;
			}

			body[start + 0] = (byte)(0x80 | (value & 0x7F));
			body[start + 1] = (byte)(0x80 | ((value >> 7) & 0x7F));
			body[start + 2] = (byte)(0x80 | ((value >> 14) & 0x7F));
			body[start + 3] = (byte)((value >> 21) & 0x7F);
			return true;
		}

		// Overwrites the transform's position with the entity's live one, in place. No-op on a record too short to
		// hold one.
		public static void WritePosition(byte[] record, Vector3 position)
		{
			if (record == null || record.Length < PosXOffset + 2 * PosStride + 4)
				return;

			record.WriteF32Le(PosXOffset, position.X);
			record.WriteF32Le(PosXOffset + PosStride, position.Y);
			record.WriteF32Le(PosXOffset + 2 * PosStride, position.Z);
		}

		// Offset (within the record body, past the iirType) of the playfield-instance varint that follows the 0x18
		// tag at the end of the transform group: skip the 12-byte header ([class][handle][fieldA]), the transform's
		// own length byte, the 0A vec3, the 12 quat, an optional 25 scale, then the 0x18 tag.
		private static bool TryPlayfieldVarintStart(ReadOnlySpan<byte> b, out int start)
		{
			start = 0;
			var i = 12;
			if (b.Length <= i)
				return false;

			i++; // the transform's own length varint (0x2C for every 4-byte token)
			if (i + 1 >= b.Length || b[i] != 0x0A)
				return false;

			i += 2 + b[i + 1]; // 0A vec3
			if (i + 1 >= b.Length || b[i] != 0x12)
				return false;

			i += 2 + b[i + 1]; // 12 quat
			if (i < b.Length && b[i] == 0x25)
				i += 5; // optional 25 scale
			if (i >= b.Length || b[i] != 0x18)
				return false;

			start = i + 1;
			return true;
		}
	}
}
