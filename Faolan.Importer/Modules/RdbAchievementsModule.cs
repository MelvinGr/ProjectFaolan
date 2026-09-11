using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Rdb;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Modules
{
	public sealed class RdbAchievementsModule : IImportModule
	{
		/// <remarks>No dependency: the catalog is keyed by the client's own tag ids and joins to nothing.</remarks>
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "The achievement catalog (LDB 55701 names / 55700 descriptions), keyed by tag id.";
		public string Name => "rdb-achievements";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbAchievementImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			return new ImportResult($"{r.Names} achievement name(s), {r.Descriptions} description(s), {r.Written} row(s) written.");
		}
	}
}
