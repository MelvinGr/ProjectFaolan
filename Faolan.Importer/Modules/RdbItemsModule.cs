using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Rdb;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Modules
{
	// Decrypts each keyed item's RDB template. Under --dry-run the --dump-templates disk write is suppressed.
	public sealed class RdbItemsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Decrypt keyed item RDB templates (names/descriptions/categories/values).";
		public string Name => "rdb-items";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var dump = ctx.DryRun
				? null
				: ctx.DumpTemplatesDir;
			var r = await RdbItemImporter.ImportAsync(ctx.Db, ctx.RdbDir!, dump, ctx.RdbProxyBase);
			return new ImportResult(
				$"{r.Decrypted}/{r.Keyed} decrypted; {r.NamesAdded} names, {r.DescriptionsAdded} descriptions, {r.CategoriesAdded} categories, {r.ValuesAdded} values, {r.QualitiesAdded} qualities, {r.StatsAdded} stat mods.");
		}
	}
}
