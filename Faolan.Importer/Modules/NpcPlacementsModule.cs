using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;

namespace Faolan.Importer.Modules
{
	// STATIONARY placement pooling: which near-identical stationary spawns are one NPC harvested twice.
	//
	// A separate module rather than a step of `npcs`, for the same reason `npc-disposition` is: it only STAMPS
	// `PlacementId` on rows that already exist, so it can be re-run on its own against a new corpus without
	// re-harvesting the whole spawn set - which is a much larger and riskier write for a much smaller question.
	// Registration order sequences it after `npcs`, which is required: the pass skips route-carrying spawns, so the
	// patrol pass has to have attached them first.
	public sealed class NpcPlacementsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Pool the stationary spawn rows the corpus proves are one NPC seen twice (PlacementId, #718).";
		public string Name => "npc-placements";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var pooled = await NpcImportOps.ImportStationaryPlacements(ctx.Db, ctx.Captures);
			return new ImportResult($"{pooled} stationary row(s) pooled into their placement.");
		}
	}
}
