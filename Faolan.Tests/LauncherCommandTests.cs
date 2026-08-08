using System.CommandLine;
using Faolan.ClientLauncher;
using Faolan.Core.Cli;
using Xunit;
using Keys = Faolan.Core.Cli.FaolanSettings.Keys;

namespace Faolan.Tests
{
	// The launcher's command line, pinned at the level the rest of the launcher reads it: LauncherOptions. These are
	// the cases the old hand-rolled scan over argv got wrong or could not express at all - an option whose value ran
	// off the end of argv threw IndexOutOfRangeException, a misspelling was rejected only because the switch had a
	// default arm, and --debug-channels' optional value was decided by "does the next token start with '-'".
	public class LauncherCommandTests
	{
		// The documented unattended launch (CLAUDE.md, docs/tooling/client-launcher.md) - short aliases and all.
		[Fact]
		public void Parse_ReadsTheDocumentedTestLaunch()
		{
			var options = LauncherCommand.Parse(["--console", "--patch-universeaddr", "-u", "faolan", "-p", "any", "-c", "tester"], out var exitCode);

			Assert.NotNull(options);
			Assert.Equal(0, exitCode);
			Assert.True(options.Console);
			Assert.True(options.PatchUniverseAddr);
			Assert.Equal("faolan", options.Username);
			Assert.Equal("any", options.Password);
			Assert.Equal("tester", options.Charname);
			Assert.Empty(options.DebugChannels);
		}

		// --patch-universeaddr and --patch-cdn are FLAGS: they say whether to redirect the client, and the address comes from the
		// settings the server and the content mirror already read (Server:PublicAddress + Server:UniverseAgentPort,
		// Cdn:PublicAddress). Each used to carry its own address, so a launch line pointed the client at one endpoint
		// while faolan.json pointed the server at another, and only the client's copy could be silently wrong.
		//
		// Bare is therefore a complete invocation - it is what `run.sh --client` passes - and the following token is
		// the next flag, not the address.
		[Fact]
		public void Parse_RedirectFlags_TakeNoAddressAndDoNotEatTheNextFlag()
		{
			var options = LauncherCommand.Parse(["--patch-universeaddr", "--patch-cdn", "--dry-run"], out var exitCode);

			Assert.NotNull(options);
			Assert.Equal(0, exitCode);
			Assert.True(options.PatchUniverseAddr);
			Assert.True(options.PatchCdn);
			Assert.True(options.DryRun);
		}

		[Fact]
		public void Parse_WithoutTheRedirectFlags_LeavesTheClientPointedWhereItIs()
		{
			var options = LauncherCommand.Parse(["--dry-run"], out _);

			Assert.NotNull(options);
			Assert.False(options.PatchUniverseAddr);
			Assert.False(options.PatchCdn);
		}

		// ...and the addresses those flags apply come back on the same resolved stack the DH keys do, so a one-off
		// `--Server:PublicAddress` / `--Cdn:PublicAddress` overrides the file for this launch and nothing else has to know.
		[Fact]
		public void Parse_TheRedirectAddresses_ComeFromTheSettings()
		{
			var options = LauncherCommand.Parse(
				["--patch-universeaddr", "--patch-cdn", "--Server:PublicAddress", "192.168.1.5", "--Server:UniverseAgentPort", "7000", "--Cdn:PublicAddress", "http://192.168.1.5:7081"],
				out _);

			Assert.NotNull(options);
			Assert.Equal("192.168.1.5:7000", options.Configuration.ServerUniverseAddress);
			Assert.Equal("http://192.168.1.5:7081", options.Configuration.CdnPublicAddress);
		}

		// --debug-channels carries an OPTIONAL value: bare it means the default set, with a list it means that list.
		// `run.sh --client2` passes it bare and then appends "$@", so the bare form has to survive being followed by
		// another flag as well as by nothing at all.
		[Fact]
		public void Parse_BareDebugChannels_TakesTheDefaultSetAndImpliesConsole()
		{
			var options = LauncherCommand.Parse(["--debug-channels"], out _);

			Assert.NotNull(options);
			Assert.Equal(ClientDebugChannels.Default, options.DebugChannels);
			Assert.True(options.Console);
		}

		[Fact]
		public void Parse_BareDebugChannels_IsNotFedTheFollowingFlag()
		{
			var options = LauncherCommand.Parse(["--debug-channels", "--dx10"], out _);

			Assert.NotNull(options);
			Assert.Equal(ClientDebugChannels.Default, options.DebugChannels);
			Assert.True(options.Dx10);
		}

		[Fact]
		public void Parse_DebugChannelsList_IsSplitAndTrimmed()
		{
			var options = LauncherCommand.Parse(["--debug-channels", "GameCode/Stats, GameCode/ArmorMitigation"], out _);

			Assert.NotNull(options);
			Assert.Equal(["GameCode/Stats", "GameCode/ArmorMitigation"], options.DebugChannels);
		}

		// Without --debug-channels nothing is written to dbDebug.conf - an empty list, not the default set, is what
		// says "the flag was not given".
		[Fact]
		public void Parse_WithoutDebugChannels_LeavesThemEmpty()
		{
			var options = LauncherCommand.Parse(["--dry-run"], out _);

			Assert.NotNull(options);
			Assert.Empty(options.DebugChannels);
			Assert.False(options.Console);
			Assert.True(options.DryRun);
		}

		// The DH keys are CONFIG KEYS: the option name IS the configuration path, so what the parse contributes is a
		// layer keyed exactly as faolan.json spells it - and a key not passed is ABSENT rather than null, so it
		// cannot shadow the config file with nothing.
		//
		// It asserts the LAYER rather than options.Configuration (which is that layer already resolved against the
		// stack) because "the key was not passed" is only visible before the file layers answer for it: on a machine
		// whose faolan.user.json holds a Server:PublicKey, the resolved config has one whatever the command line said.
		[Fact]
		public void Parse_LoginKeys_ComeBackAsAConfigOverrideLayer()
		{
			var command = new RootCommand("test") { FaolanSettings.Get(Keys.ServerPrivateKey), FaolanSettings.Get(Keys.ServerPublicKey) };
			var layer = FaolanCommandLine.Overrides(command.Parse(["--Server:PrivateKey", "AABB"]), [.. command.Options]);

			Assert.Equal("AABB", Assert.Contains(Keys.ServerPrivateKey, layer));
			Assert.DoesNotContain(Keys.ServerPublicKey, layer);

			// ...and the launcher hands on that layer resolved, so the patcher reads the key from the same stack the
			// server does rather than from the flag alone.
			var options = LauncherCommand.Parse(["--patch-login", "--Server:PrivateKey", "AABB"], out _);

			Assert.NotNull(options);
			Assert.True(options.PatchLogin);
			Assert.Equal("AABB", options.Configuration[Keys.ServerPrivateKey]);
		}

		// A --launcher-notes URL the client cannot use is a typo in the flag, so it is refused during the PARSE -
		// before the exe has been backed up and half-patched.
		[Theory]
		[InlineData("https://host/notes.php?UniverseName=%s&Language=%s")] // the client's libcurl has no SSL
		[InlineData("http://host/notes.php?UniverseName=%s")] // needs both %s
		public void Parse_RejectsALauncherNotesUrlTheClientCannotUse(string url)
		{
			Assert.Null(LauncherCommand.Parse(["--launcher-notes", url], out var exitCode));
			Assert.NotEqual(0, exitCode);
		}

		[Fact]
		public void Parse_AcceptsAValidLauncherNotesUrl()
		{
			var options = LauncherCommand.Parse(["--launcher-notes", "http://127.0.0.1:7081/_stats?UniverseName=%s&Language=%s"], out _);

			Assert.NotNull(options);
			Assert.Equal("http://127.0.0.1:7081/_stats?UniverseName=%s&Language=%s", options.LauncherNotes);
		}

		// What the declared command line buys: a misspelling is an ERROR, and an option whose value ran off the end
		// of argv is one too - the old scan indexed args[++i] and threw.
		[Theory]
		[InlineData("--dryrun")]
		[InlineData("--universeaddr")] // the old address-carrying spelling; it is --patch-universeaddr now
		[InlineData("--universe-addr")]
		[InlineData("--exe")]
		[InlineData("--charname")]
		public void Parse_RejectsWhatItDoesNotUnderstand(string bad)
		{
			Assert.Null(LauncherCommand.Parse([bad], out var exitCode));
			Assert.NotEqual(0, exitCode);
		}
	}
}
