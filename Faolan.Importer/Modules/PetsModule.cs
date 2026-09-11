using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;

namespace Faolan.Importer.Modules
{
	/// <summary>
	///     Summonable pet creatures: lifts each pet's captured FA015807 stat core onto its creature type
	///     (NpcTemplate.Data) so a pet-summon spell spawns it from the type, with no world placement. Opt-in like
	///     mounts; run against a pet-summon capture with `--only pets`.
	/// </summary>
	public sealed class PetsModule : IImportModule
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

		public string Description => "Summonable pet creatures - type-level stat core (#307, opt-in).";
		public string Name => "pets";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (seen, seeded) = await CapturePetImporter.ImportAsync(ctx.Db, ctx.Captures.Select(c => c.Packets));
			return new ImportResult($"{seen} pet creature(s) found, {seeded} type(s) lifted.");
		}
	}
}
