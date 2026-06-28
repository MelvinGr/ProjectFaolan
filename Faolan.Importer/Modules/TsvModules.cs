using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Faolan.Importer.Cli;
using Faolan.Importer.Tsv;

namespace Faolan.Importer.Modules
{
	// Ellatha scrape backfills. Tsv source = in the default set, but self-skip when the TSV is absent (they
	// only run when the file is present in the captures dir).

	// Spell LEVELS (not in the RDB record), by exact name.
	public sealed class EllathaLevelsModule : IImportModule
	{
		public string Name => "ellatha-levels";
		public string Description => "Spell levels from the ellatha scrape (ellatha_spells.tsv).";
		public ImportSource Source => ImportSource.Tsv;
		public IReadOnlyList<string> DependsOn => [];
		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode> { WriteMode.Insert, WriteMode.Upsert };

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var tsv = Path.Combine(ctx.CapturesDir, "ellatha_spells.tsv");
			if (!File.Exists(tsv))
				return new ImportResult("skipped (no ellatha_spells.tsv).");
			var r = await EllathaSpellLevelImporter.ImportAsync(ctx.Db, tsv);
			return new ImportResult($"{r.Rows} rows, {r.Matched} matched, {r.Updated} levels set.");
		}
	}

	// Combo LEVEL + COOLDOWN (the combos list is the one source with cooldown), by exact name.
	public sealed class EllathaCombosModule : IImportModule
	{
		public string Name => "ellatha-combos";
		public string Description => "Combo level+cooldown from the ellatha scrape (ellatha_combos.tsv).";
		public ImportSource Source => ImportSource.Tsv;
		public IReadOnlyList<string> DependsOn => [];
		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode> { WriteMode.Insert, WriteMode.Upsert };

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var tsv = Path.Combine(ctx.CapturesDir, "ellatha_combos.tsv");
			if (!File.Exists(tsv))
				return new ImportResult("skipped (no ellatha_combos.tsv).");
			var r = await EllathaSpellLevelImporter.ImportCombosAsync(ctx.Db, tsv);
			return new ImportResult($"{r.Combos} combos, {r.Matched} matched, {r.Updated} updated.");
		}
	}
}
