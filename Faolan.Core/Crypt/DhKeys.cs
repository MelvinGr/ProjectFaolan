using System;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using Faolan.Core.Cli;
using Microsoft.Extensions.Configuration;

namespace Faolan.Core.Crypt
{
	/// <summary>
	///     The AoC login Diffie-Hellman parameters and OUR keypair - the one place both sides of the patch agree on.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The client ships Funcom's public key baked into <c>AgeOfConan.exe</c>; the launcher rewrites those bytes
	///         with <see cref="ServerPublicHex" /> so the server can reproduce the shared secret with its own private
	///         key and read the real password (see <c>docs/protocol/01-crypto.md</c>).
	///     </para>
	///     <para>
	///         This lives in <b>Faolan.Core</b> deliberately. It used to be split - the private key in
	///         <c>Faolan.Core.Network.LoginEncryption</c> and a hand-copied ASCII-hex public key in
	///         <c>Faolan.ClientLauncher.ClientExePatcher</c> - because the launcher does not reference
	///         <c>Faolan.Core.Network</c>. Two copies of one keypair is a silent failure waiting to happen: change the
	///         private key and every login falls back to accept-any, with a patched client still carrying the old
	///         public key. Here the public half is DERIVED from the private one, so they cannot drift.
	///     </para>
	///     <para>
	///         <b>Two settings, one per role.</b> <c>Login:PrivateKey</c> is the server's identity - it decrypts the
	///         login block. <c>Login:PublicKey</c> is only what the launcher stamps into an exe, and defaults to the
	///         derived half, so it never has to be set. There is deliberately no setting for the key the launcher
	///         searches FOR: it finds the slot by shape, which is what lets it re-patch a client it already patched
	///         (<c>ClientExePatcher.PatchPublicKey</c>).
	///     </para>
	///     <para>
	///         ⚠️ The built-in private key is the historical CellAO one, and it is NOT a secret - it is in this repo's
	///         history and in every client ever patched with it. It is a default, which is why it lives in the settings
	///         catalog with every other default rather than as a literal here. A deployment that wants its own runs
	///         <c>--generate-login-key</c> and re-patches its clients; the two halves must move together.
	///     </para>
	/// </remarks>
	public static class DhKeys
	{
		/// <summary>The hex length the exe stores a DH public key at - a fixed 256-char ASCII field.</summary>
		public const int PublicHexLength = 256;

		// 1024-bit safe prime + generator, baked into the client. NOT settings: a deployment cannot choose them,
		// because the client's copy is what it is - changing ours would only break the handshake.
		public static readonly BigInteger N = ParseHex(
				"eca2e8c85d863dcdc26a429a71a9815ad052f6139669dd659f98ae159d313d13c6bf2838e10a69b6478b64a24bd054ba8248e8fa778703b418408249440b2c1edd28853e240d8a7e49540b76d120d3b1ad2878b1b99490eb4a2a5e84caa8a91cecbdb1aa7c816e8be343246f80c637abc653b893fd91686cf8d32d6cfe5f2a6f");

		public static readonly BigInteger G = 5;

		// ⚠️ Declared BEFORE _private, and both by field initializer: static initializers run in textual order, so
		// moving this below its use would leave _private at zero. (ParseHex touches neither N nor G, so this pair is
		// independent of the two above - what it must not do is float below the field it seeds.)
		private static readonly BigInteger DefaultPrivate = ParseHex(FaolanSettings.Get("Login:PrivateKey").Default!);

		private static BigInteger _private = DefaultPrivate;
		private static string? _publicOverride;

		/// <summary>Our DH private key. Server-side only; never patched into a client.</summary>
		public static BigInteger ServerPrivate => _private;

		/// <summary>Our DH public key (<c>g^ServerPrivate mod N</c>).</summary>
		// Computed per access, NOT a `{ get; } =` initializer: static initializers run in textual order, so one placed
		// above N/G/_private would see N = 0 and ModPow throws DivideByZeroException. That broke every login once,
		// when a formatting pass reordered the members.
		public static BigInteger ServerPublic => BigInteger.ModPow(G, ServerPrivate, N);

		/// <summary>
		///     <see cref="ServerPublic" /> as the <see cref="PublicHexLength" />-char lowercase ASCII hex the exe stores
		///     it in. Always the DERIVED value, so nothing on the server side can be confused by a configured
		///     <see cref="ClientPatchPublicHex" />.
		/// </summary>
		public static string ServerPublicHex => ToPublicHex(ServerPublic);

		/// <summary>
		///     The public key the launcher writes INTO a client exe. Normally <see cref="ServerPublicHex" /> - the client
		///     must use our public key for the server to reproduce the shared secret - but settable on its own with
		///     <c>Login:PublicKey</c> for the one case where they differ: patching a client to talk to somebody ELSE's
		///     server, whose private key you do not have and must not need.
		/// </summary>
		public static string ClientPatchPublicHex => _publicOverride ?? ServerPublicHex;

		/// <summary>
		///     <see cref="N" /> as the exe stores it - the same 256-char hex field a public key sits in. Not a patch
		///     target: the launcher identifies the key slot by excluding this.
		/// </summary>
		public static string PrimeHex => ToPublicHex(N);

		/// <summary>
		///     Whether a private key other than the built-in default is in force - compared by VALUE, not by whether the
		///     setting was present. The default comes down the configuration stack like any other value, so "was it
		///     set?" is always yes and would have every caller announce a configured key that isn't one.
		/// </summary>
		public static bool IsConfigured => _private != DefaultPrivate;

		/// <summary>
		///     Whether <c>Login:PublicKey</c> is overriding the derived half - i.e. the launcher would patch in a key
		///     that is not ours, so the client it patches cannot log in to THIS server.
		/// </summary>
		public static bool IsPublicOverridden => _publicOverride != null;

		/// <summary>
		///     Applies the whole <c>Login:</c> key configuration. Read <see cref="IsConfigured" /> /
		///     <see cref="IsPublicOverridden" /> afterwards to report which keys are in force.
		/// </summary>
		/// <remarks>
		///     The two halves have separate setters below because they have separate OWNERS and must not share a value:
		///     the private key is the server's identity, while the public override only reaches a client exe and must
		///     never move <see cref="ServerPublicHex" />. What they must not be is applied SEPARATELY by a caller -
		///     forgetting the second silently ignores a configured key - so this is the one entry point, and the
		///     setters are internal.
		/// </remarks>
		/// <exception cref="ArgumentException">Either key is unparseable or out of range - fail loudly.</exception>
		public static void Configure(IConfiguration config)
		{
			ConfigurePrivate(config["Login:PrivateKey"]);
			ConfigurePublic(config["Login:PublicKey"]);
		}

		/// <summary>
		///     A fresh random private key, as the hex <c>Login:PrivateKey</c> takes. 256 bits, the size of the built-in
		///     one - the exponent's size is what a brute-force costs, and 256 bits is past reach either way.
		/// </summary>
		/// <remarks>
		///     ⚠️ Generating one is HALF the job: a client still carries the public key it was patched with, so every
		///     client has to be re-patched (<c>Faolan.ClientLauncher --patch-login</c>) or its login block stops
		///     decrypting - which does not fail loudly, it falls back to accept-any. The two move together.
		/// </remarks>
		public static string GeneratePrivateHex()
		{
			// Rejection-sampled rather than reduced mod N: a modulo would skew the low end, and the loop practically
			// never runs twice (a 256-bit value is <= 1 with probability 2^-255). The bound is the one ParseExponent
			// enforces, so a generated key can never be one the server would then refuse.
			while (true)
			{
				var candidate = new BigInteger(RandomNumberGenerator.GetBytes(32), true, true);
				if (candidate > BigInteger.One && candidate < N)
					return candidate.ToString("x").TrimStart('0');
			}
		}

		/// <summary>
		///     Hex -> BigInteger, forced POSITIVE: a leading high bit would otherwise read as a negative two's-complement
		///     value and every <c>ModPow</c> would be wrong.
		/// </summary>
		public static BigInteger ParseHex(string hex)
		{
			return BigInteger.Parse("0" + hex.Trim(), NumberStyles.HexNumber);
		}

		/// <summary>
		///     Applies the SERVER's private key. Null/empty keeps the built-in default - and so does passing that
		///     default explicitly, because it is the same key; <see cref="IsConfigured" /> compares values, not presence.
		/// </summary>
		/// <exception cref="ArgumentException">Not parseable hex, or not a sane positive integer - fail loudly.</exception>
		internal static void ConfigurePrivate(string? privateKeyHex)
		{
			var hex = privateKeyHex?.Trim();
			_private = string.IsNullOrEmpty(hex) ? DefaultPrivate : ParseExponent(hex, "Login:PrivateKey");
		}

		/// <summary>
		///     Applies the LAUNCHER-side public key (<c>Login:PublicKey</c>). Null/empty restores the derived default;
		///     see <see cref="IsPublicOverridden" />.
		/// </summary>
		/// <exception cref="ArgumentException">Not parseable hex, or out of range - fail loudly.</exception>
		internal static void ConfigurePublic(string? publicKeyHex)
		{
			var hex = publicKeyHex?.Trim();
			_publicOverride = string.IsNullOrEmpty(hex) ? null : ToPublicHex(ParseExponent(hex, "Login:PublicKey"));
		}

		// A DH exponent/element from hex: parseable, and strictly inside (1, N). Anything else is a typo or a truncated
		// paste, and both fail SILENTLY at login time (the accept-any fallback) if they are let through here.
		private static BigInteger ParseExponent(string hex, string key)
		{
			BigInteger parsed;
			try
			{
				parsed = ParseHex(hex);
			}
			catch (Exception e)
			{
				throw new ArgumentException($"{key} is not valid hex ({e.Message}). Leave it unset to use the built-in key.", nameof(hex));
			}

			if (parsed <= BigInteger.One || parsed >= N)
				throw new ArgumentException($"{key} must be a positive hex integer between 1 and the DH prime.", nameof(hex));

			return parsed;
		}

		// Left-padded to the exe's fixed field width, which is what makes a public key and the prime the same shape on
		// disk - and why the launcher has to tell them apart by value rather than by size.
		private static string ToPublicHex(BigInteger value)
		{
			return value.ToString("x").TrimStart('0').PadLeft(PublicHexLength, '0');
		}
	}
}
