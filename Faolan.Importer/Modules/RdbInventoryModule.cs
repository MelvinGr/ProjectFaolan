using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Rdb;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Modules
{
	// Read-only inventory of the RDB resource types (writes nothing). The friendly names cover the resource
	// types we care about (item-template / NPC-stat / dialogue / playfield).
	public sealed class RdbInventoryModule : IImportModule
	{
		private static readonly Dictionary<uint, string> TypeNames = new Dictionary<uint, string>
		{
			[0] = "ScriptGroup",
			[1000010] = "Filenames",
			[1000077] = "ContentSpawnDataExport",
			[1000078] = "AcgSpawnDataExport",
			[1000079] = "NpcSpawnDataExport",
			[1000083] = "TreasureDataExport",
			[1000090] = "ResurrectionPointsExport",
			[1010001] = "3DModel",
			[1010004] = "Texture/FCTX",
			[1010035] = "TextScript",
			[1010201] = "MonsterData",
			[1010223] = "LuaArchive",
			[1010700] = "CharacterCreator",
			[1020003] = "Lip",
			[1030002] = "TextStrings",
			[1070003] = "PlayfieldDescription"
		};

		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Read-only RDB resource-type inventory.";
		public string Name => "rdb-inventory";

		public Task<ImportResult> RunAsync(ImportContext ctx)
		{
			using var rdb = new RdbReader(ctx.RdbDir!, ctx.RdbProxyBase);
			ctx.Out.WriteLine($"RDB: {rdb.Count} index entries in {ctx.RdbDir}");
			ctx.Out.WriteLine("  type        count   name");
			foreach (var (type, count) in rdb.TypeCounts())
				ctx.Out.WriteLine($"  {type,-10}  {count,6}  {(TypeNames.TryGetValue(type, out var n) ? n : "")}");

			// a (type, id) the index states twice means an override layer is present, and TryGet's last-wins
			// rule is deciding which copy every consumer gets. Say so where a human is looking, rather than leaving
			// it to be discovered as wrong content. Zero on this client - printed anyway, because "0 duplicate keys"
			// is the answer to the question and a silent absence is not.
			ctx.Out.WriteLine($"  duplicate (type,id) keys: {rdb.DuplicateKeys.Count}"
				+ (rdb.DuplicateKeys.Count == 0
					? ""
					: " - TryGet serves the LAST entry of each"));
			foreach (var (type, id, count) in rdb.DuplicateKeys.Take(20))
				ctx.Out.WriteLine($"    {type}:{id} x{count}");

			return Task.FromResult(new ImportResult($"{rdb.Count} index entries inventoried, {rdb.DuplicateKeys.Count} duplicate keys (read-only)."));
		}
	}
}
