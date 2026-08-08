using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace Faolan.Core.Cli
{
	// The shared command-line/configuration seam, so every executable declares its options the same way and builds
	// its configuration from the same three layers in the same order.
	//
	// The rule this enforces is ONE NAME PER SETTING: a config option's command-line name IS its configuration path,
	// so `--Database:ConnectionString x` on the command line, `Database__ConnectionString` in the environment and
	// `Database: { ConnectionString }` in faolan.json all name the same thing. There are no short aliases - they
	// were a fourth spelling that had to be kept in step by hand.
	//
	// Config options are declared here but never READ here: they are collected into an override layer and resolved
	// by Build, so the precedence (environment > command line > faolan.json) is identical everywhere. That also
	// keeps the command-line parser away from the configuration provider, whose habit of pairing sequential tokens
	// meant a valueless flag silently swallowed the option that followed it.
	public static class FaolanCommandLine
	{
		// Parses argv against `command`, in the one way every executable here does it: a parse error or `--help` is
		// handled BY THE PARSER (it prints, and `exitCode` is what the process should return) and this returns null;
		// otherwise the caller reads its values off the result. Nothing is invoked on the success path - these commands
		// carry no actions, the Main that owns them is the action.
		public static ParseResult? Parse(Command command, string[] args, out int exitCode)
		{
			var result = command.Parse(args);
			if (result.Errors.Count > 0 || args.Any(a => a is "--help" or "-h" or "-?"))
			{
				exitCode = result.Invoke();
				return null;
			}

			exitCode = 0;
			return result;
		}

		// The values of those options, as a configuration override layer. An option the user did not pass is absent
		// rather than null, so it cannot shadow faolan.json with nothing.
		//
		// ⚠️ "Did the user pass it?" is `Implicit: false`, NOT `GetResult(...) != null`. An option that has a default -
		// which every catalog setting but the three optional ones does - answers GetResult with an IMPLICIT result
		// carrying that default, so the null test would put the CLI's copy of the default into the override layer and
		// shadow faolan.json with it on every run. (Worse for a bool, which has an implicit result whether or not it
		// was declared with a default - see the flag reads in ServerCommand.)
		//
		// The value goes back to text through FaolanSettings.Text, the same renderer the defaults layer uses, so a
		// typed value the parser produced (a ushort port, a FaolanDbProvider, a bool) lands in the configuration
		// stack spelled exactly as its default would have been.
		public static Dictionary<string, string?> Overrides(ParseResult result, params Option[] options)
		{
			var overrides = new Dictionary<string, string?>();
			foreach (var option in options)
			{
				if (result.GetResult(option) is not { Implicit: false } given)
					continue;

				// An explicitly EMPTY value (`--Content:RdbDir ""`) is treated as not given, for the same reason: it
				// would otherwise shadow a configured value with nothing.
				if (FaolanSettings.Text(given.GetValueOrDefault<object>()) is { Length: > 0 } text)
					overrides[FaolanSettings.KeyOf(option)] = text;
			}

			return overrides;
		}

		// DEFAULTS < faolan.json (optional) < faolan.user.json (optional, gitignored) < command line < environment.
		// The one place that order is written down. The user file sits directly above the committed one so a
		// per-machine override needs no edit to a tracked file - see Global.UserConfigPath.
		//
		// The defaults layer at the BOTTOM is what lets a consumer write `config["Log:File"]` and get `faolan.log`.
		// Before it, every read site carried its own `?? "faolan.log"`, which is one copy per reader of a value that
		// has one definition - and three settings had a fallback but no declaration, so no `--help` mentioned them.
		public static IConfiguration Build(IEnumerable<KeyValuePair<string, string?>>? commandLine = null)
		{
			return new ConfigurationBuilder()
				.AddInMemoryCollection(FaolanSettings.Defaults)
				.AddJsonFile(Global.ConfigPath, true)
				.AddJsonFile(Global.UserConfigPath, true)
				.AddInMemoryCollection(commandLine ?? [])
				.AddEnvironmentVariables()
				.Build();
		}
	}
}
