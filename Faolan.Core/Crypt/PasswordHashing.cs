using System;
using System.Security.Cryptography;

namespace Faolan.Core.Crypt
{
	/// <summary>
	///     Shared password hashing for BOTH the web login and the game-server (UniverseAgent) login, so the single
	///     <c>Account.Password</c> column works for either. PBKDF2-SHA256 with a self-describing format:
	///     <c>pbkdf2_sha256$&lt;iterations&gt;$&lt;base64 salt&gt;$&lt;base64 hash&gt;</c> (~90 chars, fits the 128-char
	///     column).
	///     <para>
	///         <see cref="Matches" /> also accepts a stored LEGACY plaintext value (so existing seed/captured accounts
	///         keep working), and an empty / "-" stored value means "no password set" (accept anything) — this is what
	///         keeps the historical "any password works" behaviour for accounts without a password. Callers should
	///         re-hash on a successful legacy match (see <see cref="NeedsRehash" />) so plaintext is migrated away.
	///     </para>
	/// </summary>
	public static class PasswordHashing
	{
		private const int HashSize = 32;
		private const int Iterations = 210_000;
		private const int SaltSize = 16;
		private const string Prefix = "pbkdf2_sha256$";

		public static string Hash(string password)
		{
			var salt = RandomNumberGenerator.GetBytes(SaltSize);
			var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
			return $"{Prefix}{Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
		}

		public static bool IsHashed(string? stored)
		{
			return stored != null && stored.StartsWith(Prefix, StringComparison.Ordinal);
		}

		// Does the supplied password match the stored value? Handles hashed, legacy plaintext, and "no password set".
		public static bool Matches(string? stored, string supplied)
		{
			if (string.IsNullOrEmpty(stored) || stored == "-")
				return true; // no password configured -> accept anything (historical behaviour)

			return IsHashed(stored)
				? Verify(supplied, stored)
				: string.Equals(stored, supplied, StringComparison.Ordinal); // legacy plaintext
		}

		// True when a stored value is a real legacy plaintext password that should be upgraded to a hash after a
		// successful login (the empty / "-" "no password" sentinels are left alone).
		[Obsolete]
		public static bool NeedsRehash(string? stored)
		{
			return !string.IsNullOrEmpty(stored) && stored != "-" && !IsHashed(stored);
		}

		// Verify a supplied password against a stored PBKDF2 hash. False for non-hash / malformed input.
		public static bool Verify(string password, string? stored)
		{
			if (!IsHashed(stored))
				return false;

			var parts = stored![Prefix.Length..].Split('$');
			if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations))
				return false;

			try
			{
				var salt = Convert.FromBase64String(parts[1]);
				var expected = Convert.FromBase64String(parts[2]);
				var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
				return CryptographicOperations.FixedTimeEquals(actual, expected);
			}
			catch
			{
				return false;
			}
		}
	}
}
