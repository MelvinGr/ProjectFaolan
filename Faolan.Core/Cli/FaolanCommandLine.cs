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
		// An option whose name is a configuration path, taken from the ONE catalog (FaolanSettings) so its description
		// and its default cannot drift from the value the code actually uses. A command declares the SUBSET it offers;
		// the defaults themselves come from the whole catalog in Build, so an executable that reads a setting it does
		// not declare still gets it.
		public static Option<string> ConfigOption(string key)
		{
			return FaolanSettings.Options(key)[0];
		}

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
		public static Dictionary<string, string?> Overrides(ParseResult result, params Option<string>[] options)
		{
			return options
				.Where(o => result.GetValue(o) is { Length: > 0 })
				.ToDictionary(o => o.Name.TrimStart('-'), o => (string?)result.GetValue(o));
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
