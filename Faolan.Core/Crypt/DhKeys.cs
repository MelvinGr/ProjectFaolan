using System;
using System.Numerics;

namespace Faolan.Core.Crypt
{
	/// <summary>
	///     The AoC login Diffie-Hellman parameters and OUR keypair - the one place both sides of the patch agree on.
	/// </summary>
	/// <remarks>
	///     The client ships Funcom's public key baked into <c>AgeOfConan.exe</c>; the launcher rewrites those bytes with
	///     <see cref="ServerPublicHex" /> so the server can reproduce the shared secret with its own private key and read
	///     the real password (see <c>docs/protocol/01-crypto.md</c>).
	///     <para>
	///         This lives in <b>Faolan.Core</b> deliberately. It used to be split - the private key in
	///         <c>Faolan.Core.Network.LoginEncryption</c> and a hand-copied ASCII-hex public key in
	///         <c>Faolan.ClientLauncher.ClientExePatcher</c> - because the launcher does not reference
	///         <c>Faolan.Core.Network</c>. Two copies of one keypair is a silent-failure waiting to happen: change the
	///         private key and every login falls back to accept-any, with a patched client that still carries the old
	///         public key. Here the public key is DERIVED from the private one, so they cannot drift.
	///     </para>
	///     <para>
	///         ⚠️ The default private key is the historical CellAO one. It is public - in this repo's history and in
	///         every patched client - so it is a DEFAULT, not a secret. Override it with <c>Login:PrivateKey</c>
	///         (env <c>Login__PrivateKey</c> / faolan.json) and re-patch your clients; the two must move together.
	///     </para>
	/// </remarks>
	public static class DhKeys
	{
		// 1024-bit safe prime + generator, baked into the client.
		public static readonly BigInteger N =
			ParseHex(
				"eca2e8c85d863dcdc26a429a71a9815ad052f6139669dd659f98ae159d313d13c6bf2838e10a69b6478b64a24bd054ba8248e8fa778703b418408249440b2c1edd28853e240d8a7e49540b76d120d3b1ad2878b1b99490eb4a2a5e84caa8a91cecbdb1aa7c816e8be343246f80c637abc653b893fd91686cf8d32d6cfe5f2a6f");

		public static readonly BigInteger G = 5;

		// The key the RETAIL exe ships with - what the launcher searches for and replaces.
		public const string FuncomPublicHex =
			"9c32cc23d559ca90fc31be72df817d0e124769e809f936bc14360ff4bed758f260a0d596584eacbbc2b88bdd410416163e11dbf62173393fbc0c6fefb2d855f1a03dec8e9f105bbad91b3437d8eb73fe2f44159597aa4053cf788d2f9d7012fb8d7c4ce3876f7d6cd5d0c31754f4cd96166708641958de54a6def5657b9f2e92";

		private const string DefaultPrivateHex = "7ad852c6494f664e8df21446285ecd6f400cf20e1d872ee96136d7744887424b";

		private static BigInteger _private = ParseHex(DefaultPrivateHex);

		/// <summary>Our DH private key. Server-side only; never patched into a client.</summary>
		public static BigInteger ServerPrivate => _private;

		/// <summary>Our DH public key (<c>g^ServerPrivate mod N</c>) - the value the launcher patches in.</summary>
		// Computed per access, NOT a `{ get; } =` initializer: static initializers run in textual order, so one placed
		// above N/G/_private would see N = 0 and ModPow throws DivideByZeroException. That broke every login once,
		// when a formatting pass reordered the members.
		public static BigInteger ServerPublic => BigInteger.ModPow(G, ServerPrivate, N);

		/// <summary>
		///     <see cref="ServerPublic" /> as the 256-char lowercase ASCII hex the exe stores it in.
		/// </summary>
		public static string ServerPublicHex => ServerPublic.ToString("x").TrimStart('0').PadLeft(256, '0');

		/// <summary>Whether a private key other than the built-in default is in force.</summary>
		public static bool IsConfigured { get; private set; }

		/// <summary>
		///     Applies a private key from config. Null/empty keeps the built-in default and returns false, so the caller
		///     can report WHICH key is in force rather than leaving it implicit.
		/// </summary>
		/// <exception cref="ArgumentException">Not parseable hex, or not a sane positive integer - fail loudly.</exception>
		public static bool Configure(string? privateKeyHex)
		{
			var hex = privateKeyHex?.Trim();
			if (string.IsNullOrEmpty(hex))
			{
				_private = ParseHex(DefaultPrivateHex);
				IsConfigured = false;
				return false;
			}

			BigInteger parsed;
			try
			{
				parsed = ParseHex(hex);
			}
			catch (Exception e)
			{
				throw new ArgumentException($"Login:PrivateKey is not valid hex ({e.Message}). Leave it unset to use the built-in key.", nameof(privateKeyHex));
			}

			if (parsed <= BigInteger.One || parsed >= N)
				throw new ArgumentException("Login:PrivateKey must be a positive hex integer between 1 and the DH prime.", nameof(privateKeyHex));

			_private = parsed;
			IsConfigured = true;
			return true;
		}

		// Hex -> BigInteger, forced POSITIVE (a leading high bit would otherwise read as a negative two's-complement
		// value and every ModPow would be wrong).
		public static BigInteger ParseHex(string hex)
		{
			return BigInteger.Parse("0" + hex.Trim(), System.Globalization.NumberStyles.HexNumber);
		}
	}
}
