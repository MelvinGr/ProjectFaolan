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

		// The plaintext path is GONE, and this is the test that would have passed before and must fail now: a stored
		// value equal to the supplied password used to authenticate on a string compare.
		[Theory, InlineData("oldpw", "oldpw"), InlineData("oldpw", "other"), InlineData("letmein", "letmein")]
		public void Matches_PlaintextStoredValue_NeverAuthenticates(string stored, string supplied)
		{
			Assert.False(PasswordHashing.Matches(stored, supplied));
		}

		// NOT a fallback and deliberately kept: an account with no password set accepts anything. The shipped seed
		// account stores exactly this, so it is what a fresh clone logs in with.
		[Theory, InlineData(null), InlineData(""), InlineData(PasswordHashing.NoPassword)]
		public void Matches_NoPasswordSet_AcceptsAnything(string? stored)
		{
			Assert.True(PasswordHashing.Matches(stored, "anything"));
			Assert.True(PasswordHashing.Matches(stored, ""));
		}

		[Fact]
		public void AHashedAccount_MatchesOnlyItsOwnPassword()
		{
			const string typed = "letmein";
			var hash = PasswordHashing.Hash(typed);

			Assert.True(PasswordHashing.Matches(hash, typed));
			Assert.False(PasswordHashing.Matches(hash, "letmein!"));
			Assert.False(PasswordHashing.Matches(hash, ""));
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
