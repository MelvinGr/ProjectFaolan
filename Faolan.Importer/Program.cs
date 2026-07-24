using System.Threading.Tasks;
using Faolan.Importer.Cli;

namespace Faolan.Importer
{
	// Rebuilds faolan.db from the capture sources (DB is the single source of truth; no embedded data blobs in
	// the server). The importer is module-based - see ImportCli/ModuleRegistry + docs/tooling/importer.md. This is
	// the entry point only: it delegates to the CLI. `--list` shows every module; `--dry-run` writes nothing.
	internal static class Program
	{
		private static Task<int> Main(string[] args)
		{
			// Parse ONCE, up front: an unknown option is an error here rather than something silently ignored deeper
			// in. Returns null when the parser handled the invocation itself (--help, or it reported a parse error).
			var options = ImporterCommand.Parse(args, out var exitCode);
			return options == null
				? Task.FromResult(exitCode)
				: ImportCli.RunAsync(options);
		}
	}
}
