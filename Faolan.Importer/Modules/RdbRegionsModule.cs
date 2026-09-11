using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Modules
{
	// The bounded-area trigger volumes a subsystem actually consumes (RDB 1000053), into
	// MapRegion. Clean rebuild - the set is derived from the fixed client RDB - so Overwrite-only.
	//
	// Depends on rdb-respawns and the quest import: it links regions to the RespawnPoints and QuestGoalDefinitions
	// that are already in the DB, so running it against a database without them imports nothing and reports so.
	//
	// The lava volumes come through here too. A hazard is a behaviour side table on the region rather than a table
	// with its own copy of the geometry, so one importer owns the 1000053 geometry and no second module rebuilds
	// it.
	public sealed class RdbRegionsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => ["rdb-respawns"];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Consumed bounded-area trigger volumes -> MapRegion (1000053, #519/#521/#513).";
		public string Name => "rdb-regions";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			// The captured banner sightings are harvested here and handed in - they are an input to the match, not
			// state: a banner ends up as a column on the region that fires it, so nothing about one is read
			// back out of the database.
			var captured = AreaNameExtractor.Merge(ctx.Captures.SelectMany(c => AreaNameExtractor.Extract(c.Packets)));
			var r = await RdbRegionImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase, captured);
			// Anything a consumer asked for and did not get is named, not swallowed.
			foreach (var note in r.Notes)
				ctx.Out.WriteLine($"  rdb-regions unresolved: {note}");

			return new ImportResult($"{r.Regions} regions ({r.Vertices} vertices); linked {r.DiscoveryLinks} respawn discovery volume(s), " +
			                        $"{r.GoalLinks} quest goal region(s), {r.Hazards} lava hazard(s) and {r.NoMount} no-mount volume(s); " +
			                        $"{r.Banners} area banner(s) ({r.GeometricBanners} matched geometrically, {r.DroppedBanners} captured banner(s) dropped).");
		}
	}
}
