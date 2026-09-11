using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Rdb;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Modules
{
	/// <summary>
	///     Resurrection points; on-demand records fetched from the CDN.
	/// </summary>
	/// <remarks>
	///     Under --dry-run the CDN blob archive (--store) is suppressed: an out-of-DB write the transaction rollback
	///     cannot undo.
	/// </remarks>
	public sealed class RdbRespawnsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Resurrection points (CDN-backed).";
		public string Name => "rdb-respawns";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbRespawnImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			return new ImportResult($"{r.Points} points across {r.Maps} maps ({r.Empty} empty, {r.FetchFailed} fetch failures).");
		}
	}
}
