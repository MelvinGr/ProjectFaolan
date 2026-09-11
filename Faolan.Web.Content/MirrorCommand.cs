using System.CommandLine;
using Faolan.Core.Cli;
using Microsoft.Extensions.Configuration;
using Keys = Faolan.Core.Cli.FaolanSettings.Keys;

namespace Faolan.Web.Content
{
	// The CDN mirror's command line. Every option it takes is a configuration key, so there is nothing here but the
	// declaration - the values go straight into the shared configuration builder (env > command line > faolan.json).
	// Declaring them is what makes `--Cdn:Prot 7081` an error rather than a silently ignored typo that leaves the
	// mirror on its default port, and what lets `--help` list what the mirror accepts.
	public static class MirrorCommand
	{
		// A view of the one setting catalog (FaolanSettings): the description and the default live there, so `--help`
		// here cannot describe a default the code does not use - and the mirror's `Cdn:PublicAddress` keeps its
		// deliberate lack of one, because to the mirror unset means "refuse to start" (Program.Main).
		private static readonly Option[] Options =
			FaolanSettings.Options(Keys.CdnStore, Keys.CdnListenAddress, Keys.CdnListenPort, Keys.CdnPublicAddress);

		// The resolved configuration (defaults < faolan.json < faolan.user.json < command line < environment), or null
		// when the parser handled the invocation itself (--help, or it reported a parse error) - the caller should then
		// exit with `exitCode`.
		public static IConfiguration? Configuration(string[] args, out int exitCode)
		{
			var command = new RootCommand("Faolan CDN mirror - a store-and-forward mirror of Age of Conan's UPM content CDN.");
			foreach (var option in Options)
				command.Options.Add(option);

			return FaolanCommandLine.Parse(command, args, out exitCode) is { } result
				? FaolanCommandLine.Build(result, Options)
				: null;
		}
	}
}
