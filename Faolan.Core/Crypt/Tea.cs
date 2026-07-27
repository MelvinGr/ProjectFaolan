using System;
using Faolan.Core.Extensions;

namespace Faolan.Core.Crypt
{
	// TEA (Tiny Encryption Algorithm) - the cipher the AoC client uses for both the login block (TEA-CBC, see
	// LoginEncryption) and the RDBH item-template resources (TEA-ECB; decrypt routine at AgeOfConan.exe va
	// 0xa8f550, the client string `tea_unexpand`). 64-bit block, 128-bit key (4 LE u32), 32 rounds.
	public static class Tea
	{
		private const uint Delta = 0x9E3779B9;

		// Decrypts one 64-bit block (v0,v1) with the 128-bit key k[0..3]. Standard TEA decrypt: sum starts at
		// delta*32 = 0xC6EF3720 and counts down.
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
				var v0 = outBuf.ReadU32Le(i);
				var v1 = outBuf.ReadU32Le(i + 4);
				DecryptBlock(ref v0, ref v1, key);
				outBuf.WriteU32Le(i, v0);
				outBuf.WriteU32Le(i + 4, v1);
			}

			return outBuf;
		}

		// Encrypts one 64-bit block - the inverse of DecryptBlock (sum starts at 0 and counts up). Used to simulate
		// the patched client in tests/launcher work; the server itself only decrypts.
		public static void EncryptBlock(ref uint v0, ref uint v1, ReadOnlySpan<uint> k)
		{
			unchecked
			{
				var sum = 0u;
				for (var i = 0; i < 32; i++)
				{
					sum += Delta;
					v0 += ((v1 << 4) + k[0]) ^ (v1 + sum) ^ ((v1 >> 5) + k[1]);
					v1 += ((v0 << 4) + k[2]) ^ (v0 + sum) ^ ((v0 >> 5) + k[3]);
				}
			}
		}
	}
}
