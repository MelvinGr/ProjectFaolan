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
	///         <b>A stored value that is not a PBKDF2 hash authenticates nothing.</b> Two fallbacks used to live here
	///         and both are gone: a verbatim compare against a legacy plaintext value, and an empty / <c>"-"</c>
	///         "no password set" sentinel that accepted <i>any</i> password. The second is why the default
	///         <c>faolan</c> account now seeds a real hash (<see cref="DefaultSeedPassword" />) instead of an empty
	///         string — see <c>DatabaseInitializer.EnsureDefaultAccountAsync</c>.
	///     </para>
	///     <para>
	///         An UNPATCHED game client is a separate matter and bypasses this entirely on a server with no
	///         <c>Server:PrivateKey</c> — its login block cannot be decrypted, so there is no password to check in the
	///         first place. A server that HAS a key refuses it instead.
	///     </para>
	/// </summary>
	public static class PasswordHashing
	{
		private const int HashSize = 32;
		private const int Iterations = 210_000;
		private const int SaltSize = 16;
		private const string Prefix = "pbkdf2_sha256$";

		// The password the default `faolan` account is seeded with on a fresh database. A known dev default, not a
		// secret: it exists so a fresh clone can log in at all now that an empty Account.Password no longer means
		// "accept anything". Each install hashes it with its own salt.
		public const string DefaultSeedPassword = "faolan";

		public static string Hash(string password)
		{
			var salt = RandomNumberGenerator.GetBytes(SaltSize);
			var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
			return $"{Prefix}{Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
		}

		// Verify a supplied password against a stored PBKDF2 hash. False for non-hash / malformed input.
		public static bool Verify(string password, string? stored)
		{
			try
			{
				var parts = stored?[Prefix.Length..]?.Split('$');
				if (parts?.Length != 3 || !int.TryParse(parts[0], out var iterations))
					return false;

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
