using System.Threading.Tasks;

namespace Faolan.Importer
{
	// Rebuilds faolan.db from the capture sources (DB is the single source of truth; no embedded data blobs in
	// the server). The importer is module-based - see Faolan.Importer.V2 + docs/importer-v2-plan.md. This is the
	// entry point only: it delegates to the v2 CLI. `--list` shows every module; `--dry-run` writes nothing.
	internal static class Program
	{
		private static Task<int> Main(string[] args) => V2.ImportCli.RunAsync(args);
	}
}
