using System.CommandLine;
using Faolan.Core.Cli;
using Xunit;
using Keys = Faolan.Core.Cli.FaolanSettings.Keys;

namespace Faolan.Tests
{
	// The server's own command line: `--role`, the two flags that make the process do something else and exit, and the
	// configuration keys.
	//
	// The bug this class exists for is the one a `--help` test cannot see. `--generate-login-key` was read as
	// `result.GetResult(GenerateLoginKey) != null`, and an Option<bool> the user never mentioned STILL has a result -
	// an implicit one carrying false. So the test was true on every run: `dotnet run --project Faolan` printed a DH
	// keypair and exited, and the server could not be started at all. Nothing failed loudly, which is why it wants a
	// gate: the no-flag path is the ONLY one that returns options, and every other one returns null.
	public class ServerCommandTests
	{
		[Fact]
		public void WithNoArguments_TheServerStarts()
		{
			var options = ServerCommand.Parse([], out var exitCode);

			Assert.NotNull(options); // ...not "the parser handled it itself and the process should exit"
			Assert.Equal(0, exitCode);
			Assert.Empty(options.Roles); // no --role = all-in-one (login + the first realm)
			Assert.NotNull(options.Configuration); // the whole stack, resolved once, at the parse
		}

		// Nothing passed means the command line contributes NOTHING - it must not hand on its own copy of each default
		// and shadow faolan.json with it. Asserted on the layer rather than on options.Configuration, because a
		// resolved value cannot say which layer answered.
		[Fact]
		public void WithNoArguments_TheCommandLineContributesNothing()
		{
			var command = new RootCommand("test") { FaolanSettings.Get(Keys.McpPort), FaolanSettings.Get(Keys.McpEnabled), FaolanSettings.Get(Keys.LogFile) };

			Assert.Empty(FaolanCommandLine.Overrides(command.Parse([]), [.. command.Options]));
		}

		// --role is repeatable, which is what co-hosts a login tier and one realm in a single process. It is also the
		// one option here the configuration stack must NOT swallow: a binder keeps only the last value of a key.
		[Fact]
		public void RoleIsRepeatable()
		{
			var options = ServerCommand.Parse(["--role", "login", "--role", "realm=2"], out var exitCode);

			Assert.NotNull(options);
			Assert.Equal(0, exitCode);
			Assert.Equal(["login", "realm=2"], options.Roles);
		}

		// A flag that makes the process print something and exit returns null - the caller exits with `exitCode`. This
		// is the direct inverse of WithNoArguments_TheServerStarts: only a GIVEN flag does this.
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

		// ...and a value the setting cannot hold is refused HERE, by the parser, rather than at whatever startup first
		// reads it. `--Mcp:Port banana` used to be accepted (every setting was an Option<string>) and then took the
		// process down mid-boot, after the log file and the listeners were already up.
		[Fact]
		public void AMalformedConfigurationValue_IsRefusedBeforeTheServerBoots()
		{
			Assert.Null(ServerCommand.Parse(["--Mcp:Port", "banana"], out var exitCode));
			Assert.NotEqual(0, exitCode);
		}
	}
}
