using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;

namespace Faolan.Importer.Modules
{
	/// <summary>
	///     Summonable mount creatures, into the dedicated never-loaded Mounts map. Not in the default set; opt in via
	///     --only mounts.
	/// </summary>
	public sealed class MountsModule : IImportModule
	{
		// NpcEquipment.Template is an FK to Items (the worn item's quad + hash), so items must be harvested first.
		public IReadOnlyList<string> DependsOn => [nameof(ItemsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;
		public bool InDefaultSet => false;

		public string Description => "Summonable mount creatures (#27, opt-in).";
		public string Name => "mounts";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (seen, seeded) = await CaptureMountImporter.ImportAsync(ctx.Db, ctx.Captures.Select(c => c.Packets));
			return new ImportResult($"{seen} mount creature(s) found, {seeded} seeded.");
		}
	}
}
