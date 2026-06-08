using System;
using System.Buffers.Binary;

namespace Faolan.Importer
{
	// TEA (Tiny Encryption Algorithm) - the cipher the AoC client uses for RDBH item-template resources (the
	// decrypt routine is at AgeOfConan.exe va 0xa8f550; same cipher family as the login `tea_unexpand`). 64-bit
	// block, 128-bit key (4 LE u32), 32 rounds. RDBH item templates (RDB types 1000020/1000620) are encrypted in
	// ECB mode with a per-item key = the item's ProtoDecryptionKey, which the server streams in inventory and we
	// store as Item.Data1..Data4. See docs/rdb-extraction-plan.md ("RDBH cracked").
	public static class Tea
	{
		private const uint Delta = 0x9E3779B9;

		// Decrypts one 64-bit block (v0,v1) with the 128-bit key k[0..3]. Matches the client's standard TEA decrypt
		// (sum starts at delta*32 = 0xC6EF3720 and counts down).
		public static void DecryptBlock(ref uint v0, ref uint v1, ReadOnlySpan<uint> k)
		{
			unchecked
			{
				var sum = 0xC6EF3720u;
				for (var i = 0; i < 32; i++)
				{
					v1 -= ((v0 << 4) + k[2]) ^ (v0 + sum) ^ ((v0 >> 5) + k[3]);
					v0 -= ((v1 << 4) + k[0]) ^ (v1 + sum) ^ ((v1 >> 5) + k[1]);
					sum -= Delta;
				}
			}
		}

		// Decrypts a buffer in ECB mode (independent 8-byte blocks, little-endian words). Any trailing bytes that
		// don't fill a block are copied through unchanged (RDBH ciphertext is always a whole number of blocks).
		public static byte[] DecryptEcb(ReadOnlySpan<byte> data, ReadOnlySpan<uint> key)
		{
			if (key.Length != 4)
				throw new ArgumentException("TEA key must be 4 u32 words", nameof(key));

			var outBuf = data.ToArray();
			for (var i = 0; i + 8 <= outBuf.Length; i += 8)
			{
				var v0 = BinaryPrimitives.ReadUInt32LittleEndian(outBuf.AsSpan(i, 4));
				var v1 = BinaryPrimitives.ReadUInt32LittleEndian(outBuf.AsSpan(i + 4, 4));
				DecryptBlock(ref v0, ref v1, key);
				BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(i, 4), v0);
				BinaryPrimitives.WriteUInt32LittleEndian(outBuf.AsSpan(i + 4, 4), v1);
			}

			return outBuf;
		}
	}
}
