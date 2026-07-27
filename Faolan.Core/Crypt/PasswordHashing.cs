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
	///         An empty / <c>"-"</c> stored value means <b>no password set</b> and accepts anything — that is a
	///         deliberate dev convenience, not a fallback, and it is what the shipped seed account uses.
	///     </para>
	///     <para>
	///         There is no plaintext path. <see cref="Matches" /> used to compare a non-hash stored value to the
	///         supplied password verbatim, so a row that had never been migrated authenticated on a string compare —
	///         and only the WEB login ever re-hashed after such a match, so a plaintext account logging in through the
	///         game server stayed plaintext forever. Anything that is neither a hash nor the "no password" sentinel is
	///         now simply rejected.
	///     </para>
	/// </summary>
	public static class PasswordHashing
	{
		private const int HashSize = 32;
		private const int Iterations = 210_000;
		private const int SaltSize = 16;
		private const string Prefix = "pbkdf2_sha256$";

		// The "this account has no password" sentinel, as stored in Account.Password.
		public const string NoPassword = "-";

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

		// Does the supplied password match the stored value? Either the account has no password set, or the stored
		// value is a PBKDF2 hash - a stored value that is neither is malformed and matches nothing.
		public static bool Matches(string? stored, string supplied)
		{
			if (string.IsNullOrEmpty(stored) || stored == NoPassword)
				return true; // no password configured -> accept anything (deliberate dev convenience)

			return Verify(supplied, stored);
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
