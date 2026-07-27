using Faolan.Core.Crypt;
using Xunit;

namespace Faolan.Tests
{
	// Unit tests for the shared password hashing used by BOTH the web login and the game-server UniverseAgent login
	// (Faolan.Core.Crypt.PasswordHashing). Covers the PBKDF2 round-trip, the seeded default account, and
	// - since both the legacy plaintext path and the "no password set" sentinels were removed - that a stored value
	// which is not a real hash authenticates NOTHING, which are the cases that used to pass.
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

		[Fact]
		public void Matches_HashedPassword()
		{
			var hash = PasswordHashing.Hash("s3cret");
			Assert.True(PasswordHashing.Verify("s3cret", hash));
			Assert.False(PasswordHashing.Verify("nope", hash));
		}

		// The plaintext path is GONE, and this is the test that would have passed before and must fail now: a stored
		// value equal to the supplied password used to authenticate on a string compare.
		[Theory, InlineData("oldpw", "oldpw"), InlineData("oldpw", "other"), InlineData("letmein", "letmein")]
		public void Matches_PlaintextStoredValue_NeverAuthenticates(string stored, string supplied)
		{
			Assert.False(PasswordHashing.Verify(supplied, stored));
		}

		// The "no password set" sentinels are GONE too: empty / null / "-" used to accept ANY password, which is how
		// the default account worked before it seeded a real hash. They now authenticate nothing, like any other
		// non-hash value.
		[Theory, InlineData(null), InlineData(""), InlineData("-")]
		public void Matches_NoPasswordSentinels_NoLongerAcceptAnything(string? stored)
		{
			Assert.False(PasswordHashing.Verify("anything", stored));
			Assert.False(PasswordHashing.Verify("", stored));
		}

		// The default account a fresh database is seeded with must actually log in with the documented password -
		// that is the whole reason DatabaseInitializer stopped storing an empty string.
		[Fact]
		public void TheSeededDefaultAccount_LogsInWithTheDocumentedPassword()
		{
			var stored = PasswordHashing.Hash(PasswordHashing.DefaultSeedPassword);

			Assert.True(PasswordHashing.Verify(PasswordHashing.DefaultSeedPassword, stored));
			Assert.False(PasswordHashing.Verify("", stored));
			Assert.False(PasswordHashing.Verify("Faolan", stored)); // case-sensitive, unlike the username
		}

		[Fact]
		public void AHashedAccount_MatchesOnlyItsOwnPassword()
		{
			const string typed = "letmein";
			var hash = PasswordHashing.Hash(typed);

			Assert.True(PasswordHashing.Verify(typed, hash));
			Assert.False(PasswordHashing.Verify("letmein!", hash));
			Assert.False(PasswordHashing.Verify("", hash));
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
