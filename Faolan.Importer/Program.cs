using System.Threading.Tasks;
using Faolan.Importer.Cli;

namespace Faolan.Importer
{
	/// <summary>
	///     Rebuilds faolan.db from the capture sources; the database is the single source of truth and the server
	///     embeds no data blobs of its own.
	/// </summary>
	/// <remarks>
	///     The importer is module-based - see <see cref="Cli.ImportCli" />, <see cref="Cli.ModuleRegistry" /> and
	///     docs/tooling/importer.md. This is the entry point only and delegates to the CLI: <c>--list</c> shows every
	///     module, <c>--dry-run</c> writes nothing.
	/// </remarks>
	internal static class Program
	{
		private static Task<int> Main(string[] args)
		{
			// Parse once, up front: an unknown option is an error here rather than something silently ignored deeper
			// in. Returns null when the parser handled the invocation itself (--help, or a reported parse error).
			var options = ImporterCommand.Parse(args, out var exitCode);
			return options == null
				? Task.FromResult(exitCode)
				: ImportCli.RunAsync(options);
		}
	}
}
