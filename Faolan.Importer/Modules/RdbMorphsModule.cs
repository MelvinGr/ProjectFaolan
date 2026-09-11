using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Importer.Cli;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Modules
{
	public sealed class RdbMorphsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Morph-target registry from the RDB.";
		public string Name => "rdb-morphs";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var n = await RdbMorphTargetImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			return new ImportResult($"{n} morph targets imported.");
		}
	}
}
