using System.Collections.Generic;
using System.CommandLine;
using Faolan.Core.Cli;
using Keys = Faolan.Core.Cli.FaolanSettings.Keys;

namespace Faolan.Web
{
	// The armory/viewer's command line: configuration keys only, declared so `--help` lists them and a misspelling
	// is an error rather than a setting that silently stays on its default.
	//
	// RdbDir is the one that matters in practice - the 3D viewer needs the client's RDB folder, and it is OPTIONAL:
	// without it the rest of the site (roster, GM tools, mail) works and only the model endpoints fail. Same key as
	// the importer uses, so one name across the two things that read the client's assets.
	public static class WebCommand
	{
		// A VIEW of the one setting catalog (FaolanSettings): the description and the default live there, so
		// `--help` here cannot describe a default the code does not use.
		private static readonly Option[] Options =
			FaolanSettings.Options(Keys.ContentRdbDir, Keys.DatabaseProvider, Keys.DatabaseConnectionString, Keys.CdnPublicAddress);

		// Returns null when the parser handled the invocation itself (--help, or it reported a parse error); the
		// caller should exit with `exitCode`. ASP.NET's own host arguments (--urls, --environment) are passed
		// through untouched, so they are declared here too rather than being rejected as unknown.
		public static Dictionary<string, string?>? Overrides(string[] args, out int exitCode)
		{
			var command = new RootCommand("Faolan.Web - character/equipment 3D viewer, armory and GM tools.");
			foreach (var option in Options)
				command.Options.Add(option);

			foreach (var hostOption in new[] { "urls", "environment", "contentRoot", "applicationName" })
				command.Options.Add(new Option<string>($"--{hostOption}") { Description = $"ASP.NET host setting ({hostOption})." });

			if (FaolanCommandLine.Parse(command, args, out exitCode) is not { } result)
				return null;

			return FaolanCommandLine.Overrides(result, Options);
		}
	}
}
