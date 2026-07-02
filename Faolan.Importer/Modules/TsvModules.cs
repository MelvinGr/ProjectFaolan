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
		public ImportSource Source => ImportSource.Tsv;
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes =>
			new HashSet<WriteMode>
			{
				WriteMode.Insert,
				WriteMode.Upsert
			};

		public string Description => "Spell levels from the ellatha scrape (ellatha_spells.tsv).";
		public string Name => "ellatha-levels";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var tsv = Path.Combine(ctx.CapturesDir, "ellatha_spells.tsv");
			if (!File.Exists(tsv))
				return new ImportResult("skipped (no ellatha_spells.tsv).");

			var r = await EllathaSpellLevelImporter.ImportAsync(ctx.Db, tsv);
			return new ImportResult($"{r.Rows} rows, {r.Matched} matched, {r.Updated} levels set.");
		}
	}

	// Class<->spell ACCESS map from the AoC fandom wiki's per-class spell categories (#296), by base name.
	public sealed class WikiClassSpellsModule : IImportModule
	{
		public ImportSource Source => ImportSource.Tsv;
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes =>
			new HashSet<WriteMode>
			{
				WriteMode.Insert,
				WriteMode.Upsert
			};

		public string Description => "Class<->spell access map from the AoC fandom wiki (aoc_wiki_class_spells.tsv).";
		public string Name => "wiki-class-spells";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var tsv = Path.Combine(ctx.CapturesDir, "aoc_wiki_class_spells.tsv");
			if (!File.Exists(tsv))
				return new ImportResult("skipped (no aoc_wiki_class_spells.tsv).");

			var r = await WikiClassSpellImporter.ImportAsync(ctx.Db, tsv);
			return new ImportResult($"{r.Rows} wiki rows, {r.Matched} matched, {r.Inserted} new ClassSpell rows.");
		}
	}

	// Combo LEVEL + COOLDOWN (the combos list is the one source with cooldown), by exact name.
	public sealed class EllathaCombosModule : IImportModule
	{
		public ImportSource Source => ImportSource.Tsv;
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes =>
			new HashSet<WriteMode>
			{
				WriteMode.Insert,
				WriteMode.Upsert
			};

		public string Description => "Combo level+cooldown from the ellatha scrape (ellatha_combos.tsv).";
		public string Name => "ellatha-combos";

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
