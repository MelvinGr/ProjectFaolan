using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Importer.Cli;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Modules
{
	public sealed class RdbLdbNamesModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(RdbSpellsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Backfill empty spell names from the English LDB page (RDB 1030002).";
		public string Name => "rdb-ldb-names";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbLdbNameImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			return new ImportResult($"spells: {r.SpellEnglish} English names, filled {r.SpellsFilled}; items: {r.ItemEnglish} English names, filled {r.ItemsFilled}.");
		}
	}
}
