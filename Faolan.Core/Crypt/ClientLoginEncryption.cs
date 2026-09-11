using System;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Faolan.Core.Extensions;

namespace Faolan.Core.Crypt
{
	/// <summary>The three fields the client encrypts in its login block: <c>username|challenge|password</c>.</summary>
	public sealed record LoginCredentials(string Username, string Challenge, string Password);

	// AoC login crypto: Diffie-Hellman (prime N, g=5) key agreement + TEA-CBC. The client sends
	// "<clientPub hex>-<TEA-CBC ciphertext hex>"; the low 128 bits of the DH shared secret are the TEA key. The
	// server reproduces the secret with its own private key - which only works if the client used our public key, i.e.
	// a patched/launched client (see docs/protocol/01-crypto.md + Faolan.ClientLauncher/ClientExePatcher.cs).
	// An unpatched retail client uses Funcom's public key, so Decrypt returns garbage that fails the challenge check
	// and the caller falls back to accept-any. A server with no Server:PrivateKey at all reaches that same fallback for
	// every client, by design.
	//
	// The class is stateless: the private key is a parameter of the two operations that need it (Decrypt,
	// PublicHexFor), so a caller cannot fail to supply it. Ambient key state would let a forgotten setup call silently
	// degrade every login to accept-any with nothing at the call site to say the key was missing.
	//
	// This lives in Faolan.Core deliberately: the launcher patches the public half into AgeOfConan.exe and does not
	// reference Faolan.Core.Network, and a hand-copied second copy of the keypair is a silent failure waiting to happen
	// (change the private key and every login falls back to accept-any while the patched client carries the old public
	// key). Here the public half is derived from the private one, so they cannot drift.
	public static class ClientLoginEncryption
	{
		/// <summary>
		///     The hex length a private key is written at - <see cref="GeneratePrivate" /> samples 256 bits, so its
		///     value is 32 bytes and its hex is 64 characters, left-padded where a leading nibble is zero.
		/// </summary>
		public const int PrivateHexLength = 64;

		/// <summary>The hex length the exe stores a DH public key at - a fixed 256-char ASCII field.</summary>
		public const int PublicHexLength = 256;

		public static readonly BigInteger G = 5;

		// 1024-bit safe prime + generator, baked into the client.
		public static readonly BigInteger N = HexExtensions.ToBigInteger(
			"eca2e8c85d863dcdc26a429a71a9815ad052f6139669dd659f98ae159d313d13c6bf2838e10a69b6478b64a24bd054ba8248e8fa778703b418408249440b2c1edd28853e240d8a7e49540b76d120d3b1ad2878b1b99490eb4a2a5e84caa8a91cecbdb1aa7c816e8be343246f80c637abc653b893fd91686cf8d32d6cfe5f2a6f");

		/// <summary>
		///     <see cref="N" /> as the exe stores it - the same 256-char hex field a public key sits in. Not a patch
		///     target: the launcher identifies the key slot by excluding this.
		/// </summary>
		public static string PrimeHex => field ??= N.ToHex(PublicHexLength);

		/// <summary>
		///     Server side: decrypt a client <c>AnswerChallenge</c> body (<c>"clientPubHex-ciphertextHex"</c>) into
		///     the <c>username|challenge|password</c> triple. Returns null if the body is malformed or the block cannot
		///     be parsed - which is the normal case for an unpatched client (its block is undecryptable to us), so the
		///     caller should treat null (or a mismatched challenge) as "unpatched" and fall back to accept-any.
		/// </summary>
		/// <param name="loginKey">The client's <c>AnswerChallenge</c> body.</param>
		/// <param name="privateKey">
		///     This deployment's <c>Server:PrivateKey</c>. A parameter rather than ambient state: a keyless server is a
		///     legitimate configuration, so "we cannot decrypt" has to be something the caller states rather than
		///     something it can forget to set up.
		/// </param>
		/// <remarks>
		///     With no <c>Server:PrivateKey</c> there is nothing to decrypt with, so every block reads as undecryptable.
		///     That is the same null a genuinely unpatched client produces - which is why the caller decides what to
		///     do about an unpatched client on the key rather than on this null: a keyless server that denied them
		///     would refuse everyone.
		/// </remarks>
		public static LoginCredentials? Decrypt(string loginKey, BigInteger privateKey)
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
				var shared = BigInteger.ModPow(HexExtensions.ToBigInteger(clientPubHex), privateKey, N);
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

		/// <summary>
		///     Simulates the patched client: DH against <paramref name="serverPublic" /> + TEA-CBC encrypt, producing
		///     the exact <c>AnswerChallenge</c> body the client would send. For tests and any launcher work - the server
		///     never calls this.
		/// </summary>
		public static string Encrypt(LoginCredentials loginCredentials, BigInteger serverPublic, BigInteger clientPrivate)
		{
			var s = Encoding.UTF8.GetBytes($"{loginCredentials.Username}|{loginCredentials.Challenge}|{loginCredentials.Password}");

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

			var clientPub = BigInteger.ModPow(G, clientPrivate, N);
			var key = TeaKeyFromShared(BigInteger.ModPow(serverPublic, clientPrivate, N));

			var cipher = new StringBuilder();
			uint prev0 = 0, prev1 = 0;
			for (var i = 0; i < body.Length; i += 8)
			{
				var x0 = body.ReadU32Le(i) ^ prev0;
				var x1 = body.ReadU32Le(i + 4) ^ prev1;
				Tea.EncryptBlock(ref x0, ref x1, key);
				cipher.Append(HexExtensions.FromU32Le(x0)).Append(HexExtensions.FromU32Le(x1));
				prev0 = x0;
				prev1 = x1;
			}

			return HexExtensions.ToHex(clientPub) + "-" + cipher;
		}

		/// <summary>
		///     A fresh random private key, as the hex <c>Server:PrivateKey</c> takes. 256 bits - the exponent's size is
		///     what a brute-force costs, and 256 bits is past reach.
		/// </summary>
		/// <remarks>
		///     Generating one is half the job: a client still carries the public key it was patched with, so every
		///     client has to be re-patched (<c>Faolan.ClientLauncher --patch-login</c>) or its login block stops
		///     decrypting - which does not fail loudly, it falls back to accept-any. The two move together.
		/// </remarks>
		public static BigInteger GeneratePrivate()
		{
			// Rejection-sampled rather than reduced mod N: a modulo would skew the low end, and the loop practically
			// never runs twice (a 256-bit value is <= 1 with probability 2^-255). The bound is the one ParseKey
			// enforces, so a generated key can never be one the server would then refuse.
			while (true)
			{
				var candidate = new BigInteger(RandomNumberGenerator.GetBytes(32), true, true);
				if (candidate > BigInteger.One && candidate < N)
					return candidate;
			}
		}

		/// <summary>Our DH public key for a given private key: <c>g^serverPrivate mod N</c>.</summary>
		public static BigInteger PublicFor(BigInteger serverPrivate)
		{
			return BigInteger.ModPow(G, serverPrivate, N);
		}

		/// <summary>
		///     <see cref="PublicFor" /> as the <see cref="PublicHexLength" />-char ASCII hex field the exe stores it in -
		///     the value the launcher patches into a client. Always derived from the private key, so the two halves of
		///     one keypair cannot be made to disagree.
		/// </summary>
		public static string PublicHexFor(BigInteger serverPrivate)
		{
			return PublicFor(serverPrivate).ToHex(PublicHexLength);
		}

		private static byte[] CbcDecrypt(string cipherHex, ReadOnlySpan<uint> key)
		{
			var blocks = cipherHex.Length / 16;
			var output = new byte[blocks * 8];
			uint prev0 = 0, prev1 = 0;
			for (var b = 0; b < blocks; b++)
			{
				var blk = cipherHex.AsSpan(b * 16, 16);
				var c0 = HexExtensions.ToU32Le(blk[..8]);
				var c1 = HexExtensions.ToU32Le(blk[8..]);

				uint d0 = c0, d1 = c1;
				Tea.DecryptBlock(ref d0, ref d1, key);
				d0 ^= prev0;
				d1 ^= prev1;

				output.WriteU32Le(b * 8, d0);
				output.WriteU32Le(b * 8 + 4, d1);
				prev0 = c0;
				prev1 = c1;
			}

			return output;
		}

		// The TEA key is the low 128 bits of the shared secret: its lowercase hex, left-padded with '0' to (or
		// truncated from the front to) 32 chars = 16 bytes = four little-endian u32 words (the cpp's popHexInt).
		private static uint[] TeaKeyFromShared(BigInteger shared)
		{
			var h = HexExtensions.ToHex(shared);
			h = h.Length < 32
				? h.PadLeft(32, '0')
				: h[..32];

			var key = new uint[4];
			for (var i = 0; i < 4; i++)
				key[i] = HexExtensions.ToU32Le(h.AsSpan(i * 8, 8));

			return key;
		}
	}
}
