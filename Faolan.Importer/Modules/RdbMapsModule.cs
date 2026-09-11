using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Importer.Cli;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Modules
{
	public sealed class RdbMapsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Map names + ids from the RDB playfield descriptions.";
		public string Name => "rdb-maps";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbMapImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			return new ImportResult($"{r.Found} playfields ({r.Gated} unreachable); {r.Created} created, {r.Renamed} renamed.");
		}
	}
}
