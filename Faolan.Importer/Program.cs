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
			return ImportCli.RunAsync(args);
		}
	}
}
