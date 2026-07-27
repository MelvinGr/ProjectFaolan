using System.Collections.Generic;
using System.CommandLine;
using Faolan.Core.Cli;

namespace Faolan.Web.Content
{
	// The CDN mirror's command line. Every option it takes IS a configuration key, so there is nothing here but the
	// declaration - the values go straight into the shared configuration builder (env > command line > faolan.json).
	// Declaring them is still worth it: `--CdnMirror:Prot 7081` is now an error instead of a silently ignored typo
	// that leaves the mirror on its default port, and `--help` lists what the mirror accepts.
	public static class MirrorCommand
	{
		private static readonly Option<string> Store = FaolanCommandLine.ConfigOption("CdnMirror:Store", "Directory the mirror stores fetched CDN blobs in (default: mirror-store).");
		private static readonly Option<string> Ip = FaolanCommandLine.ConfigOption("CdnMirror:Ip", "Address to bind (default: 127.0.0.1).");
		private static readonly Option<string> Port = FaolanCommandLine.ConfigOption("CdnMirror:Port", "Port to bind (default: 7081).");

		// The parsed options as configuration overrides, or null when the parser handled the invocation itself
		// (--help, or it reported a parse error) - the caller should then exit with `exitCode`.
		public static Dictionary<string, string?>? Overrides(string[] args, out int exitCode)
		{
			var command = new RootCommand("Faolan CDN mirror - a store-and-forward mirror of Age of Conan's UPM content CDN.") { Store, Ip, Port };
			if (FaolanCommandLine.Parse(command, args, out exitCode) is not { } result)
				return null;

			return FaolanCommandLine.Overrides(result, Store, Ip, Port);
		}
	}
}
