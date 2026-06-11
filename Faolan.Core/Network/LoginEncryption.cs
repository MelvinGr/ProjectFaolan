using System;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Faolan.Core.Crypt;

namespace Faolan.Core.Network
{
	/// <summary>The three fields the client encrypts in its login block: <c>username|challenge|password</c>.</summary>
	public sealed record LoginCredentials(string Username, string Challenge, string Password);

	// AoC login crypto: Diffie-Hellman (prime N, g=5) key agreement + TEA-CBC. The client sends
	// "<clientPub hex>-<TEA-CBC ciphertext hex>"; the low 128 bits of the DH shared secret are the TEA key. The
	// server reproduces the secret with its OWN private key - which only works if the client used OUR public key
	// (CELLAO_PUB), i.e. a patched/launched client (see docs/login-decryption-plan.md + tools/patch_aoc_login_key.py).
	// An unpatched retail client uses Funcom's public key, so Decrypt returns garbage that fails the challenge check
	// -> the caller falls back to accept-any. Algorithm validated by round-trip (docs plan, 2026-06-08).
	public static class LoginEncryption
	{
		/// <summary>Our DH public key (<c>g^ServerPrivate mod N</c>) - the value to patch into the client.</summary>
		// Computed per access, NOT a { get; } = initializer: static initializers run in textual order, so an
		// initializer above N/G/ServerPrivate sees N = 0 and ModPow throws DivideByZeroException (broke all logins
		// when a formatting pass reordered members).
		public static BigInteger ServerPublic => BigInteger.ModPow(G, ServerPrivate, N);

		private static readonly BigInteger N = ParseHex("eca2e8c85d863dcdc26a429a71a9815ad052f6139669dd659f98ae159d313d13c6bf2838e10a69b6478b64a24bd054ba" +
		                                                "8248e8fa778703b418408249440b2c1edd28853e240d8a7e49540b76d120d3b1ad2878b1b99490eb4a2a5e84caa8a91c" + "ecbdb1aa7c816e8be343246f80c637abc653b893fd91686cf8d32d6cfe5f2a6f");

		private static readonly BigInteger G = 5;

		// 1024-bit safe prime + generator, baked into the client.

		// Our (CellAO/Faolan) DH keypair. ServerPrivate stays server-side; ServerPublic is what gets patched into
		// the client in place of Funcom's key (tools/patch_aoc_login_key.py / the FaolanLauncher).
		private static readonly BigInteger ServerPrivate = ParseHex("7ad852c6494f664e8df21446285ecd6f400cf20e1d872ee96136d7744887424b");

		/// <summary>
		///     Simulates the patched client: DH against <see cref="ServerPublic" /> + TEA-CBC encrypt, producing the
		///     exact <c>AnswerChallenge</c> body the client would send. For tests and any launcher work - the server
		///     never calls this.
		/// </summary>
		public static string BuildClientLoginKey(string username, string challenge, string password, ReadOnlySpan<byte> clientPrivate)
		{
			var s = Encoding.UTF8.GetBytes($"{username}|{challenge}|{password}");

			// [8-byte nonce][uint32 length BE][string], zero-padded to a whole number of 8-byte blocks.
			var bodyLen = 8 + 4 + s.Length;
			if (bodyLen % 8 != 0)
				bodyLen += 8 - bodyLen % 8;
			var body = new byte[bodyLen];
			RandomNumberGenerator.Fill(body.AsSpan(0, 8));
			body[8] = (byte)(s.Length >> 24);
			body[9] = (byte)(s.Length >> 16);
			body[10] = (byte)(s.Length >> 8);
			body[11] = (byte)s.Length;
			s.CopyTo(body, 12);

			var clientPriv = new BigInteger(clientPrivate, true, true);
			var clientPub = BigInteger.ModPow(G, clientPriv, N);
			var key = TeaKeyFromShared(BigInteger.ModPow(ServerPublic, clientPriv, N));

			var cipher = new StringBuilder();
			uint prev0 = 0, prev1 = 0;
			for (var i = 0; i < body.Length; i += 8)
			{
				var x0 = ReadLe(body, i) ^ prev0;
				var x1 = ReadLe(body, i + 4) ^ prev1;
				Tea.EncryptBlock(ref x0, ref x1, key);
				cipher
					.Append(ToLeHex(x0))
					.Append(ToLeHex(x1));
				prev0 = x0;
				prev1 = x1;
			}

			return ToHex(clientPub) + "-" + cipher;
		}

		/// <summary>
		///     Server side: decrypt a client <c>AnswerChallenge</c> body (<c>"clientPubHex-ciphertextHex"</c>) into
		///     the <c>username|challenge|password</c> triple. Returns null if the body is malformed or the block can't
		///     be parsed - which is the normal case for an unpatched client (its block is undecryptable to us), so the
		///     caller should treat null (or a mismatched challenge) as "unpatched" and fall back to the legacy path.
		/// </summary>
		public static LoginCredentials? Decrypt(string loginKey)
		{
			if (string.IsNullOrEmpty(loginKey))
				return null;

			var dash = loginKey.IndexOf('-');
			if (dash <= 0 || dash >= loginKey.Length - 1)
				return null;

			var clientPubHex = loginKey[..dash];
			var cipherHex = loginKey[(dash + 1)..];
			if (cipherHex.Length == 0 || cipherHex.Length % 16 != 0)
				return null;

			try
			{
				var shared = BigInteger.ModPow(ParseHex(clientPubHex), ServerPrivate, N);
				var key = TeaKeyFromShared(shared);
				var plain = CbcDecrypt(cipherHex, key);

				// Layout: [8-byte nonce][uint32 length BE @8][string @12].
				if (plain.Length < 12)
					return null;

				var length = (plain[8] << 24) | (plain[9] << 16) | (plain[10] << 8) | plain[11];
				if (length <= 0 || length > plain.Length - 12)
					return null;

				var text = Encoding.UTF8.GetString(plain, 12, length);
				var parts = text.Split('|');
				return parts.Length == 3
					? new LoginCredentials(parts[0], parts[1], parts[2])
					: null;
			}
			catch (FormatException)
			{
				return null; // non-hex clientPub/ciphertext from a malformed body
			}
		}

		private static byte[] CbcDecrypt(string cipherHex, ReadOnlySpan<uint> key)
		{
			var blocks = cipherHex.Length / 16;
			var output = new byte[blocks * 8];
			uint prev0 = 0, prev1 = 0;
			for (var b = 0; b < blocks; b++)
			{
				var blk = cipherHex.AsSpan(b * 16, 16);
				var c0 = HexLe(blk[..8]);
				var c1 = HexLe(blk[8..]);

				uint d0 = c0, d1 = c1;
				Tea.DecryptBlock(ref d0, ref d1, key);
				d0 ^= prev0;
				d1 ^= prev1;

				WriteLe(output, b * 8, d0);
				WriteLe(output, b * 8 + 4, d1);
				prev0 = c0;
				prev1 = c1;
			}

			return output;
		}

		private static int HexByte(string s, int i)
		{
			return Convert.ToInt32(s.Substring(i, 2), 16);
		}

		private static uint HexLe(ReadOnlySpan<char> word) // 8 hex chars -> 4 bytes -> little-endian u32
		{
			uint v = 0;
			for (var i = 0; i < 4; i++)
				v |= (uint)Convert.ToInt32(new string(word.Slice(i * 2, 2)), 16) << (i * 8);
			return v;
		}

		// Positive BigInteger from hex (force the high bit non-negative); minimal lowercase hex back out (no sign
		// nibble, no leading zeros) to match the client's representation.
		private static BigInteger ParseHex(string hex)
		{
			return BigInteger.Parse("0" + hex, NumberStyles.HexNumber);
		}

		private static uint ReadLe(byte[] b, int o)
		{
			return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
		}

		// The TEA key is the low 128 bits of the shared secret: its lowercase hex, left-padded with '0' to (or
		// truncated from the front to) 32 chars = 16 bytes = four little-endian u32 words (the cpp's popHexInt).
		private static uint[] TeaKeyFromShared(BigInteger shared)
		{
			var h = ToHex(shared);
			h = h.Length < 32
				? h.PadLeft(32, '0')
				: h[..32];

			var key = new uint[4];
			for (var i = 0; i < 4; i++)
			{
				var word = h.Substring(i * 8, 8);
				// 8 hex chars -> 4 bytes b0 b1 b2 b3 -> little-endian u32 (b0 | b1<<8 | b2<<16 | b3<<24).
				key[i] = (uint)(HexByte(word, 0) | (HexByte(word, 2) << 8) | (HexByte(word, 4) << 16) | (HexByte(word, 6) << 24));
			}

			return key;
		}

		private static string ToHex(BigInteger value)
		{
			var h = value
				.ToString("x", CultureInfo.InvariantCulture)
				.TrimStart('0');
			return h.Length == 0
				? "0"
				: h;
		}

		private static string ToLeHex(uint v)
		{
			return $"{(byte)v:x2}{(byte)(v >> 8):x2}{(byte)(v >> 16):x2}{(byte)(v >> 24):x2}";
		}

		private static void WriteLe(byte[] b, int o, uint v)
		{
			b[o] = (byte)v;
			b[o + 1] = (byte)(v >> 8);
			b[o + 2] = (byte)(v >> 16);
			b[o + 3] = (byte)(v >> 24);
		}
	}
}
