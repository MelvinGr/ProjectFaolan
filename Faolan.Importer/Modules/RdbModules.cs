using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Rdb;
using Faolan.Importer.Cli;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Modules
{
	// RDB-sourced modules. Opt-in (Source == Rdb keeps them out of the default set); all require --rdb. The two
	// with out-of-DB write side effects (rdb-respawns -> --store blobs, rdb-items -> --dump-templates) suppress
	// that write under --dry-run.

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

		public ImportSource Source => ImportSource.Rdb;
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public string Description => "Read-only RDB resource-type inventory.";
		public string Name => "rdb-inventory";

		public Task<ImportResult> RunAsync(ImportContext ctx)
		{
			using var rdb = new RdbReader(ctx.RdbDir!);
			ctx.Out.WriteLine($"RDB: {rdb.Count} index entries in {ctx.RdbDir}");
			ctx.Out.WriteLine("  type        count   name");
			foreach (var (type, count) in rdb.TypeCounts())
				ctx.Out.WriteLine($"  {type,-10}  {count,6}  {(TypeNames.TryGetValue(type, out var n) ? n : "")}");
			return Task.FromResult(new ImportResult($"{rdb.Count} index entries inventoried (read-only)."));
		}
	}

	public sealed class RdbMorphsModule : IImportModule
	{
		public ImportSource Source => ImportSource.Rdb;
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public string Description => "Morph-target registry from the RDB.";
		public string Name => "rdb-morphs";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var n = await RdbMorphTargetImporter.ImportAsync(ctx.Db, ctx.RdbDir!);
			return new ImportResult($"{n} morph targets imported.");
		}
	}

	public sealed class RdbAppearanceModule : IImportModule
	{
		public ImportSource Source => ImportSource.Rdb;
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public string Description => "Character-creator appearance choices (per sex/race hair/colours/tattoos/heads + names) from the RDB.";
		public string Name => "rdb-appearance";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var n = await RdbAppearanceImporter.ImportAsync(ctx.Db, ctx.RdbDir!);
			return new ImportResult($"{n} appearance choices imported.");
		}
	}

	public sealed class RdbSpellsModule : IImportModule
	{
		public ImportSource Source => ImportSource.Rdb;
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public string Description => "Named spell records (cast time/name) from the RDB.";
		public string Name => "rdb-spells";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbSpellImporter.ImportAsync(ctx.Db, ctx.RdbDir!);
			return new ImportResult($"{r.Seen} named records, {r.Inserted} new, {r.NamesSet} names, {r.CastTimesSet} cast times.");
		}
	}

	// Ability names from the RDB 1000020 templates, decrypted with each ability's own harvested Data1..4 TEA key.
	public sealed class RdbAbilitiesModule : IImportModule
	{
		public ImportSource Source => ImportSource.Rdb;
		public IReadOnlyList<string> DependsOn => ["abilities"];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public string Description => "Ability names from the RDB 1000020 templates (TEA-decrypted with the 0x2052 key).";
		public string Name => "rdb-abilities";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (decrypted, namesSet) = await RdbAbilityImporter.ImportAsync(ctx.Db, ctx.RdbDir!);
			return new ImportResult($"{decrypted} templates decrypted, {namesSet} names set.");
		}
	}

	// Combo catalog (name / unlock level / featid / actions / ...) from the RDB 1010206 plaintext-XML combo defs.
	public sealed class RdbCombosModule : IImportModule
	{
		public ImportSource Source => ImportSource.Rdb;
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public string Description => "Combo catalog (name/level/featid/actions) from the RDB 1010206 plaintext XML.";
		public string Name => "rdb-combos";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (seen, imported) = await RdbComboImporter.ImportAsync(ctx.Db, ctx.RdbDir!);
			return new ImportResult($"{seen} combo XML entries, {imported} combos imported.");
		}
	}

	public sealed class RdbMapsModule : IImportModule
	{
		public ImportSource Source => ImportSource.Rdb;
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public string Description => "Map names + ids from the RDB playfield descriptions.";
		public string Name => "rdb-maps";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbMapImporter.ImportAsync(ctx.Db, ctx.RdbDir!);
			return new ImportResult($"{r.Found} playfields ({r.Gated} v6-gated); {r.Created} created, {r.Renamed} renamed.");
		}
	}

	public sealed class RdbLuaModule : IImportModule
	{
		public ImportSource Source => ImportSource.Rdb;
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public string Description => "LuaArchive conversation/quest scripts from the RDB.";
		public string Name => "rdb-lua";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var n = await RdbLuaImporter.ImportAsync(ctx.Db, ctx.RdbDir!);
			return new ImportResult($"{n} scripts imported.");
		}
	}

	// Resurrection points; on-demand records fetched from the CDN. Under --dry-run the CDN blob archive
	// (--store) is suppressed (an out-of-DB write the transaction rollback can't undo).
	public sealed class RdbRespawnsModule : IImportModule
	{
		public ImportSource Source => ImportSource.Rdb;
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public string Description => "Resurrection points (CDN-backed).";
		public string Name => "rdb-respawns";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var cdn = ctx.RdbCdnBase ?? RdbRespawnImporter.DefaultCdnBase;
			var store = ctx.DryRun
				? null
				: ctx.RdbStoreDir; // don't archive blobs to disk on a dry run
			var r = await RdbRespawnImporter.ImportAsync(ctx.Db, ctx.RdbDir!, cdn, store);
			return new ImportResult($"{r.Points} points across {r.Maps} maps ({r.Empty} empty, {r.FetchFailed} fetch failures).");
		}
	}

	// Decrypts each keyed item's RDB template. Under --dry-run the --dump-templates disk write is suppressed.
	public sealed class RdbItemsModule : IImportModule
	{
		public ImportSource Source => ImportSource.Rdb;
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public string Description => "Decrypt keyed item RDB templates (names/categories/values).";
		public string Name => "rdb-items";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var dump = ctx.DryRun
				? null
				: ctx.DumpTemplatesDir;
			var r = await RdbItemImporter.ImportAsync(ctx.Db, ctx.RdbDir!, dump);
			return new ImportResult($"{r.Decrypted}/{r.Keyed} decrypted; {r.NamesAdded} names, {r.CategoriesAdded} categories, {r.ValuesAdded} values, {r.StatsAdded} stat mods.");
		}
	}
}
