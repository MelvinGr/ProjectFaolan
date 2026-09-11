using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;

namespace Faolan.Importer.Modules
{
	/// <summary>
	///     Per-class starting spells, from the create captures' 0x200A grants.
	/// </summary>
	public sealed class StartingSpellsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Per-class spell access map (create 0x200A grants).";
		public string Name => "class-spells";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (harvested, added) = await CaptureSpellImporter.ImportClassSpellsAsync(ctx.Db, ctx.CreateCaptures);
			return new ImportResult($"{harvested} harvested, {added} new.");
		}
	}
}
