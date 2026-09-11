using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Rdb;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Modules
{
	/// <summary>
	///     Authoritative open-world NPC spawn points from the RDB "NPC roam/movement" layout (type 1010506).
	/// </summary>
	/// <remarks>
	///     Overwrite-only: the set is fully derived from the fixed client RDB, so every run is a clean rebuild.
	/// </remarks>
	public sealed class RdbNpcSpawnsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Authoritative open-world ambient spawn areas (RDB 1010506 layout, #332/#363).";
		public string Name => "rdb-npc-spawns";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbNpcSpawnImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			var res = r.Resolved;
			return new ImportResult($"{r.Areas} spawn areas ({r.Points} points) across {r.Maps} playfields ({r.EmptyMaps} empty, {r.Unreadable} unreadable, {r.UnknownMap} for a playfield not in Maps, {r.HighIdSpace} in the >= 0x40000000 id space); " +
			                        $"resolved {res.Total}/{res.Areas} areas (name {res.Name}, containment {res.Containment}, id-prop {res.IdProp}, race-unique {res.RaceUnique}); linked {res.Linked} captured spawns.");
		}
	}
}
