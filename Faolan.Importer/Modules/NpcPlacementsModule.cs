using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;
using Faolan.Core.Rdb;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Modules
{
	// STATIONARY placement pooling (#718/#765): which near-identical stationary spawns are one NPC harvested twice.
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
