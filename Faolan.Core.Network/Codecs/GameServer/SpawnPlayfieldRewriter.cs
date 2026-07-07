using System;

namespace Faolan.Core.Network.Codecs.GameServer
{
	// Rewrites the per-session PLAYFIELD INSTANCE varint inside a built spawn/object body so a from-scratch
	// placement (no live body to inherit the token from - e.g. the create-editor scene) carries the current
	// session's world token instead of whatever session the stored row was captured in. Shared by NpcSpawnBuilder
	// (FA015807 character spawns) and WorldObjectBuilder (66AEDD50 dynels): both bodies are the IIR carrier payload
	// [u32 len][u32 subOpcode][record], and the token lives at the `18 <varint>` that closes the 0x2C transform
	// group. Session tokens are 4-byte LEB128 varints, so the rewrite is splice-free (no length field shifts).
	public static class SpawnPlayfieldRewriter
	{
		// Overwrites the transform's playfield-instance varint in `carrierBody` (the [u32 len][u32 subOpcode][record]
		// payload) with `value`, in place. Returns false (leaving the body untouched) if the value is out of the
		// 4-byte-varint range, the transform can't be located, or the existing token isn't a 4-byte varint.
		public static bool TryRewrite(byte[] carrierBody, uint value)
		{
			if (value is < 1u << 21 or >= 1u << 28 || carrierBody == null || carrierBody.Length <= 8)
				return false;

			var record = carrierBody.AsSpan(8); // skip [u32 len][u32 subOpcode]
			if (!TryPlayfieldVarintStart(record, out var start) || start + 4 > record.Length)
				return false;

			for (var k = 0; k < 4; k++) // the existing token must also be a 4-byte varint
			{
				if ((record[start + k] & 0x80) != 0 != k < 3)
					return false;
			}

			record[start + 0] = (byte)(0x80 | (value & 0x7F));
			record[start + 1] = (byte)(0x80 | ((value >> 7) & 0x7F));
			record[start + 2] = (byte)(0x80 | ((value >> 14) & 0x7F));
			record[start + 3] = (byte)((value >> 21) & 0x7F);
			return true;
		}

		// Offset (within a record body) of the playfield-instance varint that follows the 0x18 tag at the end of the
		// 0x2C transform group: skip the 12-byte header ([type][handle][0]), the 2C group tag, the 0A vec3, the 12
		// quat, an optional 25 scale, then the 0x18 tag.
		private static bool TryPlayfieldVarintStart(ReadOnlySpan<byte> b, out int start)
		{
			start = 0;
			var i = 12;
			if (b.Length <= i || b[i] != 0x2C)
				return false;

			i++;
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
