using Faolan.Core.Crypt;
using Xunit;

namespace Faolan.Tests
{
	// Unit tests for the shared password hashing used by BOTH the web login and the game-server UniverseAgent login
	// (Faolan.Core.Crypt.PasswordHashing). Covers the PBKDF2 round-trip, the legacy-plaintext fallback, the
	// "no password set" (-/empty) accept-any sentinels, and the migrate-on-login (NeedsRehash) signal.
	public class PasswordHashingTests
	{
		[Fact]
		public void Hash_FitsTheAccountPasswordColumn()
		{
			// Account.Password is MaxLength(128); the encoded hash must fit (a longer password must not bloat it).
			var hash = PasswordHashing.Hash(new string('x', 200));
			Assert.True(hash.Length <= 128, $"hash length {hash.Length} > 128");
		}

		[Fact]
		public void Hash_RoundTrips_And_IsSaltedPerCall()
		{
			var a = PasswordHashing.Hash("hunter2");
			var b = PasswordHashing.Hash("hunter2");

			Assert.NotEqual(a, b); // random salt -> different output each call
			Assert.True(PasswordHashing.Verify("hunter2", a)); // ...but both verify
			Assert.True(PasswordHashing.Verify("hunter2", b));
		}

		[Theory, InlineData(null, false), InlineData("", false), InlineData("plaintext", false), InlineData("pbkdf2_sha256$1$x$y", true)]
		public void IsHashed_DetectsThePrefix(string? stored, bool expected)
		{
			Assert.Equal(expected, PasswordHashing.IsHashed(stored));
		}

		[Fact]
		public void Matches_HashedPassword()
		{
			var hash = PasswordHashing.Hash("s3cret");
			Assert.True(PasswordHashing.Matches(hash, "s3cret"));
			Assert.False(PasswordHashing.Matches(hash, "nope"));
		}

		[Fact]
		public void Matches_LegacyPlaintext()
		{
			Assert.True(PasswordHashing.Matches("oldpw", "oldpw"));
			Assert.False(PasswordHashing.Matches("oldpw", "OLDPW"));
			Assert.False(PasswordHashing.Matches("oldpw", "other"));
		}

		[Theory, InlineData(null), InlineData(""), InlineData("-")]
		public void Matches_NoPasswordSet_AcceptsAnything(string? stored)
		{
			// Preserves the historical "any password works" behaviour for accounts without a configured password.
			Assert.True(PasswordHashing.Matches(stored, "anything"));
			Assert.True(PasswordHashing.Matches(stored, ""));
		}

		[Fact]
		public void MigrationScenario_PlaintextThenRehash()
		{
			// 1) Account stored as legacy plaintext: matches, and is flagged for rehash.
			const string typed = "letmein";
			const string legacy = "letmein";
			Assert.True(PasswordHashing.Matches(legacy, typed));
			Assert.True(PasswordHashing.NeedsRehash(legacy));

			// 2) Caller rehashes on successful login.
			var migrated = PasswordHashing.Hash(typed);

			// 3) Now it's a hash: still matches the same password, and no longer needs rehashing.
			Assert.True(PasswordHashing.Matches(migrated, typed));
			Assert.False(PasswordHashing.Matches(migrated, "letmein!"));
			Assert.False(PasswordHashing.NeedsRehash(migrated));
		}

		[Fact]
		public void NeedsRehash_FalseForAHash()
		{
			Assert.False(PasswordHashing.NeedsRehash(PasswordHashing.Hash("x")));
		}

		[Theory, InlineData(null, false), InlineData("", false), InlineData("-", false), InlineData("legacyplaintext", true)]
		public void NeedsRehash_OnlyForRealPlaintext(string? stored, bool expected)
		{
			Assert.Equal(expected, PasswordHashing.NeedsRehash(stored));
		}

		[Fact]
		public void Verify_OnMalformedOrPlaintext_ReturnsFalse_NeverThrows()
		{
			Assert.False(PasswordHashing.Verify("x", "plaintext"));
			Assert.False(PasswordHashing.Verify("x", "pbkdf2_sha256$notanumber$bad$bad"));
			Assert.False(PasswordHashing.Verify("x", "pbkdf2_sha256$1$!!notbase64!!$y"));
			Assert.False(PasswordHashing.Verify("x", null));
		}

		[Fact]
		public void Verify_RejectsWrongPassword()
		{
			var hash = PasswordHashing.Hash("correct horse");
			Assert.False(PasswordHashing.Verify("Correct horse", hash)); // case-sensitive
			Assert.False(PasswordHashing.Verify("", hash));
			Assert.False(PasswordHashing.Verify("wrong", hash));
		}
	}
}
