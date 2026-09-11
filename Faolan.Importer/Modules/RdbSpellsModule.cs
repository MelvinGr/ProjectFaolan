using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Importer.Cli;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Modules
{
	public sealed class RdbSpellsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		// It writes `Spells.DurationMs` from property field 8 unconditionally, and a family of records leaves that
		// field empty - so an rdb-spells run on its own would reinstate the 0/1 ms rows `buff-durations` corrects.
		public IReadOnlyList<string> RequiredAfter => [nameof(BuffDurationsModule)];

		public string Description => "Named spell records (cast time/name) + the mount catalog from the RDB.";
		public string Name => "rdb-spells";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbSpellImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			return new ImportResult($"{r.Seen} named records, {r.Inserted} new, {r.NamesSet} names, {r.CastTimesSet} cast times, {r.LevelsSet} levels, {r.Mounts} mounts, {r.EffectDurations} effect-catalog durations, {r.EffectLinks} effect links, {r.UnparsedElements} UNPARSED element heads; {r.UnparsedSamples}.");
		}
	}
}
