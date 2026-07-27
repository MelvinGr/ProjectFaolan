using System;
using System.Collections.Generic;
using Faolan.ClientLauncher;
using Faolan.Core.Crypt;
using System.Text;
using System.Globalization;
using System.Numerics;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Faolan.Tests
{
	// Self-contained round-trip for the AoC login crypto (DH g=5 over the 1024-bit prime + TEA-CBC). Simulates a
	// PATCHED client (BuildClientLoginKey encrypts against our public key) and decrypts server-side with our private
	// key - no game client needed. See docs/protocol/01-crypto.md.
	public class LoginEncryptionTests
	{
		// A 32-byte client DH private (big-endian, unsigned). Arbitrary but fixed for determinism.
		private static readonly byte[] ClientPrivate =
		[
			0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF, 0x10, 0x32, 0x54, 0x76, 0x98, 0xBA, 0xDC,
			0xFE, 0x42
		];

		[Theory, InlineData(""), InlineData("nodash"), InlineData("-"), InlineData("abcd-"), InlineData("-abcd"), InlineData("abcd-0011"), InlineData("zzzz-00112233445566778899aabbccddeeff")]
		// empty ciphertext
		// empty clientPub
		// ciphertext not a whole number of 8-byte blocks (needs 16 hex/block)
		// non-hex clientPub
		public void Decrypt_Malformed_ReturnsNull(string loginKey)
		{
			Assert.Null(LoginEncryption.Decrypt(loginKey));
		}

		[Fact]
		public void Decrypt_MatchesKeyProducedByReferencePython()
		{
			// Cross-impl check: this login key was produced by the proven reference impl (the /tmp/roundtrip_login.py
			// algorithm) encrypting faolan|deadbeef...|s3cr3t! against CELLAO_PUB with a fixed client private. If the
			// C# port deviates in DH, the TEA-key derivation, CBC chaining, or endianness, this won't decode.
			const string key = "21565c2774be9dc08df3786974ee6c293c45eb74311442c19969e0a8330bd7fdf5781c73b97808e94b416430b251bdc1" +
			                   "0cd6b9b6f46870c715dc5c7bf476dba936364da6ee838f8e9185f857c2d626d6df1ef31995e8cd312d04209d114ae372" +
			                   "5f1ecbbf85cc91360b787ac3f7ed24c988ea38a0786bb43e71f5e33958d4ca96-" +
			                   "8ff7c0700737ee89ddb269c75fdb2b00543cadba8be0d0eb69e320d67033cf05579721b4b32fe19861b32fc0eda29a0a" + "c62ba35a0879c9413f33f06b7a3a7e5f";

			var creds = LoginEncryption.Decrypt(key);

			Assert.NotNull(creds);
			Assert.Equal("faolan", creds!.Username);
			Assert.Equal("deadbeefcafebabe0123456789abcdef", creds.Challenge);
			Assert.Equal("s3cr3t!", creds.Password);
		}

		[Fact]
		public void Decrypt_TamperedBlock_DoesNotRecoverChallenge()
		{
			// An unpatched client's block is undecryptable to us; model that by corrupting a valid block. Decrypt
			// must NOT yield the original challenge (so the listener falls back to accept-any instead of validating).
			var key = LoginEncryption.BuildClientLoginKey("faolan", "deadbeefcafebabe0123456789abcdef", "pw", ClientPrivate);
			var dash = key.IndexOf('-');
			var firstCipherChar = dash + 1;
			var flipped = key[firstCipherChar] == 'a'
				? 'b'
				: 'a';
			var tampered = key[..firstCipherChar] + flipped + key[(firstCipherChar + 1)..];

			var creds = LoginEncryption.Decrypt(tampered);

			Assert.True(creds == null || creds.Challenge != "deadbeefcafebabe0123456789abcdef");
		}

		[Fact]
		public void RoundTrip_MatchesValidatedFixture()
		{
			// The exact triple the proven /tmp/roundtrip_login.py recovered.
			var key = LoginEncryption.BuildClientLoginKey("faolan", "64653811119f501db0812b357ed19694", "hunter2", ClientPrivate);

			var creds = LoginEncryption.Decrypt(key);

			Assert.NotNull(creds);
			Assert.Equal("faolan|64653811119f501db0812b357ed19694|hunter2", $"{creds!.Username}|{creds.Challenge}|{creds.Password}");
		}

		[Fact]
		public void RoundTrip_RecoversCredentials()
		{
			var key = LoginEncryption.BuildClientLoginKey("faolan", "deadbeefcafebabe0123456789abcdef", "s3cr3t!", ClientPrivate);

			var creds = LoginEncryption.Decrypt(key);

			Assert.NotNull(creds);
			Assert.Equal("faolan", creds!.Username);
			Assert.Equal("deadbeefcafebabe0123456789abcdef", creds.Challenge);
			Assert.Equal("s3cr3t!", creds.Password);
		}

		[Fact]
		public void ServerPublic_IsCellaoPublicKey()
		{
			// g^ServerPrivate mod N must equal the documented CELLAO_PUB (the value patched into the client).
			var expected = BigInteger.Parse(
				"0" + "26b5a3b4ac1177f24a2d9de44bafef477ff23ef1cb5f646919b1be26516053030b65d5afb60cef6f49de539958ba0b79" +
				"22a099319b8016a8673cb27a696ae4b60fdece25ddcdad42e7f0056b87fc35687fe033b242e17e960d79806fd46c4a79" + "cbc64f558660a50cabc1c242dace70de6af452e3433f97e30e202567f187de70",
				NumberStyles.HexNumber);

			Assert.Equal(expected, LoginEncryption.ServerPublic);
		}

		// The launcher patches ServerPublicHex into the exe and the server decrypts with the matching private key, so
		// the two must be ONE keypair. They used to be two hand-copied literals in different assemblies (the private in
		// Faolan.Core.Network, the public in Faolan.ClientLauncher), which fails SILENTLY: change one and every login
		// falls back to accept-any while patched clients still carry the old key. Both now derive from DhKeys; this
		// pins the derivation itself.
		[Fact]
		public void LauncherPublicKey_IsDerivedFromTheServerPrivateKey()
		{
			Assert.Equal(DhKeys.ServerPublicHex, Encoding.ASCII.GetString(ClientExePatcher.NewPublicKey));
			Assert.Equal(256, DhKeys.ServerPublicHex.Length);
			Assert.Equal(BigInteger.ModPow(DhKeys.G, DhKeys.ServerPrivate, DhKeys.N), DhKeys.ServerPublic);
		}

		// Configure() must fail LOUDLY on junk rather than quietly keeping the default - a mistyped key that silently
		// falls back would look like a working server whose every login is accept-any.
		[Fact]
		public void Configure_RejectsJunk_AndEmptyKeepsTheDefault()
		{
			var original = DhKeys.ServerPublicHex;
			try
			{
				Assert.False(DhKeys.Configure(null)); // unset -> built-in default, reported as "not configured"
				Assert.Equal(original, DhKeys.ServerPublicHex);
				Assert.Throws<ArgumentException>(() => DhKeys.Configure("not-hex"));
				Assert.Throws<ArgumentException>(() => DhKeys.Configure("1")); // <= 1 is not a usable exponent

				Assert.True(DhKeys.Configure("a3f1c2")); // a different key changes the public half
				Assert.NotEqual(original, DhKeys.ServerPublicHex);
			}
			finally
			{
				DhKeys.Configure(null); // static: restore so test order cannot matter
			}
		}

		// The launcher patches ClientPatchPublicHex, which is the derived key UNLESS Login:PublicKey names another one -
		// the only case being a client that should talk to somebody else's server. Setting it must not disturb
		// ServerPublicHex, which the server side derives, or the two roles would share one mutable value.
		[Fact]
		public void ConfigurePublic_OverridesOnlyWhatTheLauncherPatchesIn()
		{
			var derived = DhKeys.ServerPublicHex;
			try
			{
				Assert.False(DhKeys.ConfigurePublic(null)); // unset -> patch our own derived key
				Assert.Equal(derived, DhKeys.ClientPatchPublicHex);

				Assert.True(DhKeys.ConfigurePublic("a3f1c2"));
				Assert.Equal(derived, DhKeys.ServerPublicHex); // the server's own key is untouched…
				Assert.NotEqual(derived, DhKeys.ClientPatchPublicHex); // …but the exe gets the configured one
				Assert.Equal(DhKeys.PublicHexLength, DhKeys.ClientPatchPublicHex.Length); // left-padded to the exe's field
				Assert.Equal(DhKeys.ClientPatchPublicHex, Encoding.ASCII.GetString(ClientExePatcher.NewPublicKey));

				Assert.Throws<ArgumentException>(() => DhKeys.ConfigurePublic("not-hex"));
				Assert.Throws<ArgumentException>(() => DhKeys.ConfigurePublic(null, "not-hex"));
			}
			finally
			{
				DhKeys.ConfigurePublic(null);
			}
		}

		// The key the launcher SEARCHES for defaults to retail's and is overridable for a client build shipping another.
		[Fact]
		public void RetailPublicKey_DefaultsToRetailsAndIsOverridable()
		{
			try
			{
				Assert.StartsWith("9c32cc23d559ca90", DhKeys.RetailPublicHex);
				Assert.Equal(DhKeys.PublicHexLength, DhKeys.RetailPublicHex.Length);

				DhKeys.ConfigurePublic(null, "a3f1c2");
				Assert.EndsWith("a3f1c2", DhKeys.RetailPublicHex);
			}
			finally
			{
				DhKeys.ConfigurePublic(null);
			}
		}

		// Whether an UNPATCHED client (one whose block we cannot decrypt, so it proved only the account name) may log
		// in is Login:AllowUnpatchedClients, read by UniverseAgentListener's AnswerChallenge handler.
		//
		// The polarity is the point, and it fails in opposite directions: unset must mean ALLOW, or a server that
		// never configured this locks out every client the moment it upgrades; and only an explicit "false" may deny,
		// or a typo'd value quietly turns the password check on and looks like a mass wrong-password outage.
		[Theory]
		[InlineData(null, true)] // unset: the historical accept-any behaviour
		[InlineData("true", true)]
		[InlineData("True", true)]
		[InlineData("false", false)]
		[InlineData("False", false)]
		[InlineData("no", true)] // not a bool: allow, never a silent deny
		[InlineData("0", true)]
		[InlineData("", true)]
		public void LoginAllowUnpatchedClients_DefaultsToAllowAndOnlyAnExplicitFalseDenies(string? configured, bool expected)
		{
			var config = new ConfigurationBuilder()
				.AddInMemoryCollection([new KeyValuePair<string, string?>("Login:AllowUnpatchedClients", configured)])
				.Build();

			Assert.Equal(expected, config.LoginAllowUnpatchedClients);
		}
	}
}
