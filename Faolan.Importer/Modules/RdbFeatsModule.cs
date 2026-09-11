using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Importer.Cli;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Modules
{
	/// <summary>
	///     Feat catalog (id / name / tree / group / rank) from the plaintext RDB 1000020 templates; no TEA key is needed.
	/// </summary>
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
