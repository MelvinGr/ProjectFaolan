using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;

namespace Faolan.Importer.Modules
{
	public sealed class StockPhrasesModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Cutscene stock-phrase subtitles ({stock_N}: 0x205E req -> 0x205D resp).";
		public string Name => "stock-phrases";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var phrases = ctx.Captures.SelectMany(c => CaptureStockPhraseImporter.Extract(c.Packets));
			var (seen, added, updated) = await CaptureStockPhraseImporter.ImportAsync(ctx.Db, phrases);
			return new ImportResult($"{seen} stock phrases, {added} new, {updated} updated.");
		}
	}
}
