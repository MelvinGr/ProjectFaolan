using System;
using System.IO;
using System.Numerics;
using Faolan.Core.Cli;
using Faolan.Core.Crypt;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Faolan.Tests
{
	// `--generate-login-key` writes a fresh login DH private key into faolan.user.json - the GITIGNORED half of the
	// config, because a keypair is per-deployment and faolan.json is committed.
	//
	// The writer edits that file as TEXT. A JsonNode round-trip would be shorter and would silently delete every
	// comment in it, and the comments are the only thing that says what a machine-specific override is for. So the
	// gate here is what a text edit can get wrong: an existing value quietly lost, a missing comma, a stray one, a
	// section that was not there. Every case ends by reading the file back through the configuration stack, which is
	// the only opinion that counts.
	public class UserConfigWriterTests : IDisposable
	{
		private readonly string _path = Path.Combine(Path.GetTempPath(), $"faolan-user-{Guid.NewGuid():N}.json");

		public void Dispose()
		{
			File.Delete(_path);
		}

		// No file at all: one gets written, with the header that says what it is for.
		[Fact]
		public void ItCreatesTheFile()
		{
			UserConfigWriter.Set(_path, "Login:PrivateKey", "a3f1c2");

			Assert.Equal("a3f1c2", Read()["Login:PrivateKey"]);
			Assert.Contains("GITIGNORED", File.ReadAllText(_path));
		}

		// The realistic case, and the one a JSON round-trip fails: a file with other keys, other sections and the
		// comments explaining them. All of it has to still be there afterwards.
		[Fact]
		public void ItKeepsEverythingElse_CommentsIncluded()
		{
			File.WriteAllText(_path, """
			                         // Per-machine overrides.
			                         {
			                         	// the client's RDB folder
			                         	"RdbDir": "/games/Age of Conan/RDB",

			                         	"Cdn": {
			                         		// the mirror runs on another box
			                         		"PublicAddress": "http://192.168.1.5:7081"
			                         	}
			                         }
			                         """);

			UserConfigWriter.Set(_path, "Login:PrivateKey", "a3f1c2");

			var config = Read();
			Assert.Equal("a3f1c2", config["Login:PrivateKey"]);
			Assert.Equal("/games/Age of Conan/RDB", config["RdbDir"]);
			Assert.Equal("http://192.168.1.5:7081", config["Cdn:PublicAddress"]);

			var text = File.ReadAllText(_path);
			Assert.Contains("// the client's RDB folder", text);
			Assert.Contains("// the mirror runs on another box", text);
		}

		// Into a section that already exists, beside its other keys rather than replacing them - the comma case that a
		// naive insert gets wrong in one direction or the other.
		[Fact]
		public void ItAddsToAnExistingSection()
		{
			File.WriteAllText(_path, """
			                         {
			                         	"Login": {
			                         		"AllowUnpatchedClients": false
			                         	}
			                         }
			                         """);

			UserConfigWriter.Set(_path, "Login:PrivateKey", "a3f1c2");

			var config = Read();
			Assert.Equal("a3f1c2", config["Login:PrivateKey"]);
			Assert.Equal("False", config["Login:AllowUnpatchedClients"], StringComparer.OrdinalIgnoreCase);
		}

		// A key already set is NEVER replaced silently: losing a key somebody chose by hand is the one thing this
		// writer must not do, and a new login key is exactly the change that would lock every patched client out.
		[Fact]
		public void ItRefusesToReplaceAValueWithoutForce()
		{
			UserConfigWriter.Set(_path, "Login:PrivateKey", "a3f1c2");

			var error = Assert.Throws<InvalidOperationException>(() => UserConfigWriter.Set(_path, "Login:PrivateKey", "b4e2d3"));
			Assert.Contains("--force", error.Message);
			Assert.Equal("a3f1c2", Read()["Login:PrivateKey"]); // ...and the file still holds the original

			UserConfigWriter.Set(_path, "Login:PrivateKey", "b4e2d3", true);
			Assert.Equal("b4e2d3", Read()["Login:PrivateKey"]);
		}

		// A COMMENTED-OUT key is not a value - the generated faolan.json is full of them, and treating one as "already
		// set" would make the writer refuse a file that has nothing in it.
		[Fact]
		public void ACommentedKeyIsNotAValue()
		{
			File.WriteAllText(_path, """
			                         {
			                         	"Login": {
			                         		//"PrivateKey": ""
			                         	}
			                         }
			                         """);

			UserConfigWriter.Set(_path, "Login:PrivateKey", "a3f1c2");

			Assert.Equal("a3f1c2", Read()["Login:PrivateKey"]);
		}

		// The catalog is the only list of keys there is, so a key not in it is a typo - and it would be written
		// somewhere nothing ever reads.
		[Fact]
		public void AnUncataloguedKeyIsRefused()
		{
			Assert.Throws<ArgumentException>(() => UserConfigWriter.Set(_path, "Login:PrivateKeys", "a3f1c2"));
			Assert.False(File.Exists(_path));
		}

		// The generated key: in range, hex, and different every time. The bound is the one the CONFIGURED path
		// enforces, so a generated key can never be one the server would then refuse.
		[Fact]
		public void AGeneratedKey_IsAUsableExponent()
		{
			var first = DhKeys.GeneratePrivateHex();
			var second = DhKeys.GeneratePrivateHex();

			Assert.NotEqual(first, second);

			var parsed = DhKeys.ParseHex(first);
			Assert.True(parsed > BigInteger.One && parsed < DhKeys.N);

			try
			{
				DhKeys.ConfigurePrivate(first);
				Assert.True(DhKeys.IsConfigured);
				Assert.Equal(DhKeys.PublicHexLength, DhKeys.ServerPublicHex.Length);
				Assert.Equal(BigInteger.ModPow(DhKeys.G, parsed, DhKeys.N), DhKeys.ServerPublic);
			}
			finally
			{
				DhKeys.ConfigurePrivate(null); // static: restore so test order cannot matter
			}
		}

		private IConfiguration Read()
		{
			return new ConfigurationBuilder().AddJsonFile(_path).Build();
		}
	}
}
