using Faolan.ClientLauncher;
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
			var options = LauncherCommand.Parse(["--console", "--universeaddr", "127.0.0.1:7010", "-u", "faolan", "-p", "any", "-c", "tester"], out var exitCode);

			Assert.NotNull(options);
			Assert.Equal(0, exitCode);
			Assert.True(options.Console);
			Assert.Equal("127.0.0.1:7010", options.UniverseAddr);
			Assert.Equal("faolan", options.Username);
			Assert.Equal("any", options.Password);
			Assert.Equal("tester", options.Charname);
			Assert.Empty(options.DebugChannels);
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

		// The DH keys are CONFIG KEYS: the option name IS the configuration path, so what comes back is an override
		// layer keyed exactly as faolan.json spells it - and a key not passed is ABSENT rather than null, so it
		// cannot shadow the config file with nothing.
		[Fact]
		public void Parse_LoginKeys_ComeBackAsAConfigOverrideLayer()
		{
			var options = LauncherCommand.Parse(["--patch-login", "--Login:PrivateKey", "AABB"], out _);

			Assert.NotNull(options);
			Assert.True(options.PatchLogin);
			Assert.Equal("AABB", Assert.Contains(Keys.LoginPrivateKey, options.LoginKeys));
			Assert.DoesNotContain(Keys.LoginPublicKey, options.LoginKeys);
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
		[InlineData("--universe-addr")]
		[InlineData("--exe")]
		[InlineData("--universeaddr")]
		public void Parse_RejectsWhatItDoesNotUnderstand(string bad)
		{
			Assert.Null(LauncherCommand.Parse([bad], out var exitCode));
			Assert.NotEqual(0, exitCode);
		}
	}
}
