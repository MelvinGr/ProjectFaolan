using System.CommandLine;
using System.Linq;
using Faolan.Core.Cli;
using Xunit;
using Keys = Faolan.Core.Cli.FaolanSettings.Keys;

namespace Faolan.Tests
{
	// The server's own command line: `--role`, the two flags that make the process do something else and exit, and the
	// configuration keys.
	//
	// The bug this class exists for is the one a `--help` test cannot see. `--generate-login-key` was read as
	// `result.GetResult(GenerateLoginKey) != null`, and an Option<bool> the user never mentioned still has a result -
	// an implicit one carrying false. So the test was true on every run: `dotnet run --project Faolan` printed a DH
	// keypair and exited, and the server could not be started at all. Nothing failed loudly, which is why it wants a
	// gate: the no-flag path is the only one that returns options, and every other one returns null.
	public class ServerCommandTests
	{
		[Fact]
		public void WithNoArguments_TheServerStarts()
		{
			var options = ServerCommand.Parse([], out var exitCode);

			Assert.NotNull(options); // ...not "the parser handled it itself and the process should exit"
			Assert.Equal(0, exitCode);
			Assert.True(options.RunLogin); // no --role = all-in-one (login + the first realm)
			Assert.True(options.RunRealm);
			Assert.Null(options.RealmId);
			Assert.NotNull(options.Configuration); // the whole stack, resolved once, at the parse
		}

		// Nothing passed means the command line contributes nothing - it must not hand on its own copy of each default
		// and shadow faolan.json with it. Asserted on the layer rather than on options.Configuration, because a
		// resolved value cannot say which layer answered.
		[Fact]
		public void WithNoArguments_TheCommandLineContributesNothing()
		{
			var command = new RootCommand("test") { FaolanSettings.Get(Keys.McpPort), FaolanSettings.Get(Keys.McpEnabled), FaolanSettings.Get(Keys.LogFile) };

			Assert.Empty(FaolanCommandLine.Overrides(command.Parse([]), [.. command.Options]));
		}

		// --role is repeatable, which is what co-hosts a login tier and one realm in a single process. It is also the
		// one option here the configuration stack must not swallow: a binder keeps only the last value of a key.
		[Fact]
		public void RoleIsRepeatable()
		{
			var options = ServerCommand.Parse(["--role", "login", "--role", "realm=2"], out var exitCode);

			Assert.NotNull(options);
			Assert.Equal(0, exitCode);
			Assert.True(options.RunLogin);
			Assert.True(options.RunRealm);
			Assert.Equal(2u, options.RealmId);
		}


		// A bad role set is a command-line error, not a crash. Parsing it after the command line has accepted the
		// string turns a typo into an unhandled exception - a stack trace and a sigabrt. Parse returning null with a
		// non-zero exit is the same channel `--Mcp:Port banana` uses: one way for a bad option to be wrong.
		[Theory]
		[InlineData("realm=1", "realm=2")] // the invariant: co-hosting realms would leak chat/session state
		[InlineData("realm", "realm")]
		[InlineData("realm=abc", null)] // an id that is not a number
		[InlineData("wharf", null)] // not a role at all
		public void ABadRoleSet_IsAParseErrorAndNotACrash(string first, string? second)
		{
			string[] args = second == null ? ["--role", first] : ["--role", first, "--role", second];

			Assert.Null(ServerCommand.Parse(args, out var exitCode));
			Assert.NotEqual(0, exitCode);
		}

		// The legal sets, so the validator cannot be tightened into refusing a deployment that has to work: the login
		// tier alone, one realm alone, and the two co-hosted (which is a legal single process - the rule forbids two
		// realms, not a realm beside the router tier).
		[Theory]
		[InlineData(new[] { "login" }, true, false, null)]
		[InlineData(new[] { "realm" }, false, true, null)]
		[InlineData(new[] { "realm=7" }, false, true, 7u)]
		[InlineData(new[] { "login", "realm=7" }, true, true, 7u)]
		public void TheLegalRoleSets_Parse(string[] roles, bool runLogin, bool runRealm, uint? realmId)
		{
			var args = roles.SelectMany(r => new[] { "--role", r }).ToArray();

			var options = ServerCommand.Parse(args, out var exitCode);

			Assert.NotNull(options);
			Assert.Equal(0, exitCode);
			Assert.Equal(runLogin, options.RunLogin);
			Assert.Equal(runRealm, options.RunRealm);
			Assert.Equal(realmId, options.RealmId);
		}

		// A flag that makes the process print something and exit returns null - the caller exits with `exitCode`. This
		// is the direct inverse of WithNoArguments_TheServerStarts: only a given flag does this.
		[Fact]
		public void GenerateLoginKey_HandlesTheInvocationItself()
		{
			Assert.Null(ServerCommand.Parse(["--generate-login-key"], out var exitCode));
			Assert.Equal(0, exitCode);
		}

		// A configuration key reaches the server through the resolved configuration - typed by the catalog on the way in,
		// and read back through the accessor that owns it, so this covers the whole path rather than the raw token.
		[Fact]
		public void AConfigurationKey_ReachesTheResolvedConfiguration()
		{
			var options = ServerCommand.Parse(["--Mcp:Port", "7098", "--Mcp:Enabled"], out _);

			Assert.NotNull(options);
			Assert.Equal(7098, options.Configuration.McpPort);
			Assert.True(options.Configuration.McpEnabled);
			Assert.Equal("http://127.0.0.1:7098/mcp", options.Configuration.McpUrl); // ...and the derived value follows it
		}

		// ...and a value the setting cannot hold is refused here, by the parser, rather than at whatever startup first
		// reads it. Accepted as a bare string, `--Mcp:Port banana` would take the process down mid-boot, after the log
		// file and the listeners are already up.
		[Fact]
		public void AMalformedConfigurationValue_IsRefusedBeforeTheServerBoots()
		{
			Assert.Null(ServerCommand.Parse(["--Mcp:Port", "banana"], out var exitCode));
			Assert.NotEqual(0, exitCode);
		}
	}
}
