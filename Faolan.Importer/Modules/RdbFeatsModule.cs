using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Rdb;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Modules
{
	// Feat catalog (id / name / tree / group / rank) from the RDB 1000020 PLAINTEXT templates - no TEA key needed.
	public sealed class RdbFeatsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Feat catalog (name/tree/group/rank) from the RDB 1000020 plaintext templates.";
		public string Name => "rdb-feats";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbFeatImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			return new ImportResult($"{r.Found} feat templates ({r.Unnamed} unnamed, skipped); {r.Added} added, {r.Updated} updated.");
		}
	}
}
