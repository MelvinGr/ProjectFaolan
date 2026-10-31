using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;

namespace Faolan.Importer.Modules
{
	// Static world objects (decor/containers/zones).
	public sealed class WorldObjectsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Static world objects (decor/containers/zones).";
		public string Name => "world-objects";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var objects = WorldObjectExtractor.Merge(ctx.Captures.SelectMany(c => WorldObjectExtractor.Extract(c.Packets)), out var stateConflicts);

			// Overwrite deletes every stored row the harvest did not yield, so the table is by construction what the
			// captures say. Upsert cannot: it re-writes the live rows and leaves the dead ones, reporting a row-count
			// delta of zero either way, which is how the divergence went unseen. Only a cleanup when the
			// corpus you are importing is a SUPERSET of the one that built the table - read the warning on
			// WorldObjectImportOps.ImportWorldObjects before pointing it at a faolan.db built from the seed.
			await WorldObjectImportOps.ImportWorldObjects(ctx.Db, objects, ctx.ModeFor(Name) == WriteMode.Overwrite);

			// Say when a door was captured in two different states, rather than resolving it silently. The
			// resolution is deterministic, but the fact that the corpus disagreed is worth reading in the log.
			var resolved = stateConflicts == 0
				? ""
				: $" ({stateConflicts} captured in disagreeing open/closed states, resolved to the world-entry state)";
			return new ImportResult($"{objects.Count} world objects.{resolved}");
		}
	}
}
