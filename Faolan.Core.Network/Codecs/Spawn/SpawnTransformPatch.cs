using System;
using System.Numerics;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Spawn
{
	/// <summary>
	///     Send-time in-place patches to an already-built spawn record's transform. Not a codec - the encoding lives in
	///     <see cref="SimpleCharFullUpdateCodec" /> - but the two values that are only known at the moment of sending, and
	///     are therefore stamped over a record built earlier: the playfield instance token, which is per recipient session
	///     and not known until the player actually enters, and the entity's current position, since a cached NPC record
	///     holds the position it was built with at map load.
	/// </summary>
	/// <remarks>
	///     The offsets here are patch offsets into a record whose head is fixed-width, not parse offsets:
	///     <c>[u32 subop][u32 class][u32 handle][u32 fieldA][u8 transformLen][0x0A][u8 len][vec3][0x12][u8 len][quat]...</c>.
	///     Every field ahead of them is decoded by the codec, so nothing here reads a body it has not accounted for.
	/// </remarks>
	public static class SpawnTransformPatch
	{
		/// <summary>
		///     Where the position's three little-endian fixed32 live in the record, and the stride between them, each being
		///     preceded by its own protobuf field tag. The head -
		///     <c>[subop][class][handle][fieldA][transformLen][0x0A][vec3 len][0x0D]</c> - is 20 bytes.
		/// </summary>
		private const int PosXOffset = 20;
		private const int PosStride = 5;

		/// <summary>
		///     Overwrites the transform's playfield-instance varint in the record - the [u32 iirType][record] form the codec
		///     builds - in place. Returns false, leaving the record untouched, if the value is outside the 4-byte varint
		///     range, the transform cannot be located, or the existing token is not a 4-byte varint. Session tokens are
		///     4-byte varints, so the rewrite shifts no length field.
		/// </summary>
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

		/// <summary>
		///     Overwrites the transform's position with the entity's live one, in place. A no-op on a record too short to
		///     hold one.
		/// </summary>
		public static void WritePosition(byte[] record, Vector3 position)
		{
			if (record == null || record.Length < PosXOffset + 2 * PosStride + 4)
				return;

			record.WriteF32Le(PosXOffset, position.X);
			record.WriteF32Le(PosXOffset + PosStride, position.Y);
			record.WriteF32Le(PosXOffset + 2 * PosStride, position.Z);
		}

		/// <summary>
		///     The offset, within the record body past the iirType, of the playfield-instance varint that follows the 0x18
		///     tag at the end of the transform group: skip the 12-byte header, the transform's own length byte, the vec3, the
		///     quaternion, an optional scale, then the tag.
		/// </summary>
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
